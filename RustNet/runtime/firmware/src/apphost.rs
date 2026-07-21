//! Managed app execution: binds the IL interpreter to the device.
//!
//! `FirmwareHost` implements `rustnet_core::RuntimeHost`, mapping
//! `RustNet.*` internal calls onto the HAL board, filesystem, display,
//! WiFi and MQTT services. `AppRunner` executes an app on its own thread
//! in fuel slices so it can be stopped, profiled and debugged.

use rustnet_core::{HostValue, Interpreter, Module, RunExit, RuntimeHost};
use rustnet_diag::{Logger, PerfMonitor};
use rustnet_fs::Vfs;
use rustnet_gfx::{Color, Framebuffer};
use rustnet_hal::camera::{Camera, CameraConfig, PixelFormat};
use rustnet_hal::i2s::{I2sConfig, I2sFormat};
use rustnet_hal::gpio::{Level, PinMode};
use rustnet_hal::Board;
use rustnet_hal_host::SimCamera;
use rustnet_net::mqtt::MqttClient;
use rustnet_usb::descriptor::{DeviceDescriptor, UsbClass};
use rustnet_usb::device::{CdcAcm, UsbDevice};
use rustnet_usb::host::{CdcHostDriver, HidHostDriver, MscHostDriver, UsbHost};
use rustnet_usb::sim::SimBus;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

/// Native stack (bytes) for the OS thread that runs each managed app.
/// Zero = OS default. Heap-constrained firmware (ESP32 after WiFi
/// fragments DRAM) sets a small value with [`set_app_stack`].
pub static APP_STACK_BYTES: AtomicUsize = AtomicUsize::new(0);

/// Set the per-app native stack size (see [`APP_STACK_BYTES`]).
pub fn set_app_stack(bytes: usize) {
    APP_STACK_BYTES.store(bytes, Ordering::Relaxed);
}
use std::sync::{Arc, Mutex};
use std::time::Instant;

#[derive(Default)]
pub struct WifiState {
    pub ssid: Option<String>,
    pub psk: Option<String>,
    pub connected: bool,
}

/// Device state shared between the protocol server and running apps.
#[derive(Clone)]
pub struct SharedState {
    pub logger: Logger,
    pub perf: PerfMonitor,
    pub board: Arc<Mutex<Box<dyn Board>>>,
    pub fs: Arc<Mutex<Box<dyn Vfs>>>,
    pub display: Arc<Mutex<Option<Framebuffer>>>,
    pub wifi: Arc<Mutex<WifiState>>,
    pub epoch: Instant,
    /// Modbus slave the virtual device exposes on unit id 1 (every master
    /// call round-trips real RTU frames through it).
    pub modbus: Arc<Mutex<rustnet_net::modbus::SimSlave>>,
    /// Open database handles (index = handle given to the app).
    pub dbs: Arc<Mutex<Vec<Option<rustnet_db::Database>>>>,
    /// Image sensor (v0.8, chip-gated). The virtual device carries the
    /// colour-bar simulator; chip bring-up swaps in the real sensor.
    pub camera: Arc<Mutex<Box<dyn Camera>>>,
    /// Cumulative PCM samples the audio (I2S) sink has accepted (v0.8).
    pub audio_played: Arc<Mutex<u64>>,
    /// Whether the VNC framebuffer server is running (v0.8).
    pub vnc_running: Arc<AtomicBool>,
    /// USB device-side stack (v0.8): what this device presents when acting as
    /// a USB peripheral (CDC-ACM/HID/MSC). None until configured.
    pub usb_device: Arc<Mutex<Option<UsbDevice>>>,
    /// The simulated USB "cable" linking the device stack to the host stack.
    pub usb_bus: Arc<Mutex<SimBus>>,
    /// USB host-side stack (v0.8): enumerates + talks to attached devices.
    pub usb_host: Arc<Mutex<UsbHost>>,
    /// One-shot latch so the first `Display.Present()` logs the panel-flush
    /// outcome exactly once (physical-panel bring-up diagnostic; the panel is
    /// off-screen so this is the only signal available over RNDP).
    pub panel_logged: Arc<AtomicBool>,
}

impl SharedState {
    pub fn new(board: Box<dyn Board>, fs: Box<dyn Vfs>) -> Self {
        let logger = Logger::new(512);
        let epoch = Instant::now();
        let epoch2 = epoch;
        logger.set_clock(Box::new(move || epoch2.elapsed().as_millis() as u64));
        Self {
            logger,
            perf: PerfMonitor::new(),
            board: Arc::new(Mutex::new(board)),
            fs: Arc::new(Mutex::new(fs)),
            display: Arc::new(Mutex::new(None)),
            wifi: Arc::new(Mutex::new(WifiState::default())),
            epoch,
            modbus: Arc::new(Mutex::new(rustnet_net::modbus::SimSlave::new(1))),
            dbs: Arc::new(Mutex::new(Vec::new())),
            camera: Arc::new(Mutex::new(Box::new(SimCamera::new()))),
            audio_played: Arc::new(Mutex::new(0)),
            vnc_running: Arc::new(AtomicBool::new(false)),
            usb_device: Arc::new(Mutex::new(None)),
            usb_bus: Arc::new(Mutex::new(SimBus::new())),
            usb_host: Arc::new(Mutex::new({
                let mut h = UsbHost::new();
                h.register_driver(Box::new(CdcHostDriver));
                h.register_driver(Box::new(HidHostDriver));
                h.register_driver(Box::new(MscHostDriver));
                h
            })),
            panel_logged: Arc::new(AtomicBool::new(false)),
        }
    }
}

/// Bridges `rustnet_db::Storage` onto the device VFS so databases live on
/// whatever the path's mount is: internal flash (`/data`), SD card (`/sd`)
/// or USB drive (`/usb`).
struct VfsDbStorage {
    fs: Arc<Mutex<Box<dyn Vfs>>>,
    path: String,
}

impl VfsDbStorage {
    fn wal_path(&self) -> String {
        format!("{}.wal", self.path)
    }
}

impl rustnet_db::Storage for VfsDbStorage {
    fn load(&mut self) -> Option<Vec<u8>> {
        self.fs.lock().unwrap().read(&self.path).ok()
    }
    fn save(&mut self, bytes: &[u8]) -> Result<(), String> {
        self.fs.lock().unwrap().write(&self.path, bytes).map_err(|e| e.to_string())
    }
    // WAL lives in a sibling `<db>.wal` file: mutations append there and a
    // periodic checkpoint rewrites the snapshot and truncates it.
    fn supports_wal(&self) -> bool {
        true
    }
    fn append_wal(&mut self, record: &[u8]) -> Result<(), String> {
        let p = self.wal_path();
        self.fs.lock().unwrap().append(&p, record).map_err(|e| e.to_string())
    }
    fn read_wal(&mut self) -> Vec<u8> {
        let p = self.wal_path();
        self.fs.lock().unwrap().read(&p).unwrap_or_default()
    }
    fn truncate_wal(&mut self) -> Result<(), String> {
        let p = self.wal_path();
        self.fs.lock().unwrap().write(&p, &[]).map_err(|e| e.to_string())
    }
}

/// Standard base64 (with padding) — cloud IoT SAS tokens carry it in the
/// signature field.
fn base64_standard(data: &[u8]) -> String {
    const T: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity((data.len() + 2) / 3 * 4);
    for chunk in data.chunks(3) {
        let b = [chunk[0], *chunk.get(1).unwrap_or(&0), *chunk.get(2).unwrap_or(&0)];
        let n = (b[0] as u32) << 16 | (b[1] as u32) << 8 | b[2] as u32;
        out.push(T[(n >> 18 & 0x3F) as usize] as char);
        out.push(T[(n >> 12 & 0x3F) as usize] as char);
        out.push(if chunk.len() > 1 { T[(n >> 6 & 0x3F) as usize] as char } else { '=' });
        out.push(if chunk.len() > 2 { T[(n & 0x3F) as usize] as char } else { '=' });
    }
    out
}

/// Minimal JSON escaping for strings surfaced to managed code.
fn json_escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out
}

fn db_result_json(result: &rustnet_db::ExecResult) -> String {
    use rustnet_db::{ExecResult, Value as Dv};
    match result {
        ExecResult::Affected(n) => format!("{{\"affected\":{n}}}"),
        ExecResult::Rows { columns, rows } => {
            let cols: Vec<String> =
                columns.iter().map(|c| format!("\"{}\"", json_escape(c))).collect();
            let mut out = format!("{{\"columns\":[{}],\"rows\":[", cols.join(","));
            for (i, row) in rows.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                out.push('[');
                for (j, v) in row.iter().enumerate() {
                    if j > 0 {
                        out.push(',');
                    }
                    match v {
                        Dv::Null => out.push_str("null"),
                        Dv::Int(n) => out.push_str(&n.to_string()),
                        Dv::Real(f) => out.push_str(&format!("{f}")),
                        Dv::Text(s) => out.push_str(&format!("\"{}\"", json_escape(s))),
                        Dv::Blob(b) => {
                            let hex: String = b.iter().map(|x| format!("{x:02x}")).collect();
                            out.push_str(&format!("\"0x{hex}\""));
                        }
                    }
                }
                out.push(']');
            }
            out.push_str("]}");
            out
        }
    }
}

/// Debugger rendezvous between the RNDP server and the app thread.
#[derive(Default)]
pub struct DebugState {
    pub pending_breakpoints: Vec<(u32, u32)>,
    pub pending_clears: Vec<(u32, u32)>,
    pub paused_at: Option<(u32, u32)>,
    pub stack: Vec<String>,
    pub locals: Vec<String>,
    pub resume: bool,
    /// Set with `resume` to single-step one instruction instead of running.
    pub step: bool,
}

pub struct FirmwareHost {
    pub state: SharedState,
    pub stop: Arc<AtomicBool>,
    mqtt: Option<MqttClient>,
}

impl FirmwareHost {
    pub fn new(state: SharedState, stop: Arc<AtomicBool>) -> Self {
        Self { state, stop, mqtt: None }
    }

    fn arg_i32(args: &[HostValue], i: usize) -> Result<i32, String> {
        match args.get(i) {
            Some(HostValue::I32(v)) => Ok(*v),
            Some(HostValue::I64(v)) => Ok(*v as i32),
            Some(HostValue::F64(v)) => Ok(*v as i32),
            other => Err(format!("arg {i}: expected int, got {other:?}")),
        }
    }

    fn arg_str(args: &[HostValue], i: usize) -> Result<String, String> {
        match args.get(i) {
            Some(HostValue::Str(s)) => Ok(s.clone()),
            Some(HostValue::Null) => Ok(String::new()),
            other => Err(format!("arg {i}: expected string, got {other:?}")),
        }
    }

    fn arg_bool(args: &[HostValue], i: usize) -> Result<bool, String> {
        Ok(Self::arg_i32(args, i)? != 0)
    }

    fn arg_i64(args: &[HostValue], i: usize) -> Result<i64, String> {
        match args.get(i) {
            Some(HostValue::I64(v)) => Ok(*v),
            Some(HostValue::I32(v)) => Ok(*v as i64),
            other => Err(format!("arg {i}: expected long, got {other:?}")),
        }
    }

    fn arg_bytes(args: &[HostValue], i: usize) -> Result<Vec<u8>, String> {
        match args.get(i) {
            Some(HostValue::Bytes(b)) => Ok(b.clone()),
            Some(HostValue::Null) => Ok(Vec::new()),
            other => Err(format!("arg {i}: expected byte[], got {other:?}")),
        }
    }

    /// Round-trip one Modbus request through the on-device slave using
    /// real RTU framing (encode, CRC check, decode).
    fn modbus_transact(&self, unit: u8, pdu: &[u8]) -> Result<Vec<u8>, String> {
        use rustnet_net::modbus as mb;
        let mut adu = Vec::new();
        mb::rtu_encode(unit, pdu, &mut adu);
        let resp_adu = self
            .state
            .modbus
            .lock()
            .unwrap()
            .handle_rtu(&adu)
            .ok_or_else(|| format!("no modbus slave answered unit {unit}"))?;
        let (_, resp_pdu) = mb::rtu_decode(&resp_adu)?;
        mb::parse_response(pdu[0], resp_pdu).map(|p| p.to_vec())
    }
}

impl RuntimeHost for FirmwareHost {
    fn console_write(&mut self, text: &str) {
        for line in text.trim_end_matches('\n').split('\n') {
            self.state.logger.info("app", line.to_string());
        }
    }

    fn now_ms(&mut self) -> u64 {
        self.state.epoch.elapsed().as_millis() as u64
    }

    fn sleep_ms(&mut self, ms: u64) {
        // Sleep in slices so StopApp stays responsive.
        let mut remaining = ms;
        while remaining > 0 && !self.stop.load(Ordering::Relaxed) {
            let slice = remaining.min(20);
            std::thread::sleep(std::time::Duration::from_millis(slice));
            remaining -= slice;
        }
    }

    fn invoke(&mut self, name: &str, args: Vec<HostValue>) -> Result<HostValue, String> {
        let a = &args;
        match name {
            // ---- GPIO ----
            "RustNet.Hal.Gpio::SetMode(i4,i4)" => {
                let pin = Self::arg_i32(a, 0)? as u32;
                let mode = match Self::arg_i32(a, 1)? {
                    0 => PinMode::Input,
                    1 => PinMode::InputPullUp,
                    2 => PinMode::InputPullDown,
                    3 => PinMode::Output,
                    _ => PinMode::OutputOpenDrain,
                };
                let mut board = self.state.board.lock().unwrap();
                board.gpio(pin).and_then(|p| p.set_mode(mode)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Gpio::Write(i4,bool)" => {
                let pin = Self::arg_i32(a, 0)? as u32;
                let level = if Self::arg_bool(a, 1)? { Level::High } else { Level::Low };
                let mut board = self.state.board.lock().unwrap();
                board.gpio(pin).and_then(|p| p.write(level)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Gpio::Read(i4)" => {
                let pin = Self::arg_i32(a, 0)? as u32;
                let mut board = self.state.board.lock().unwrap();
                let level = board.gpio(pin).and_then(|p| p.read()).map_err(|e| e.to_string())?;
                Ok(HostValue::Bool(level == Level::High))
            }
            "RustNet.Hal.Gpio::Toggle(i4)" => {
                let pin = Self::arg_i32(a, 0)? as u32;
                let mut board = self.state.board.lock().unwrap();
                board.gpio(pin).and_then(|p| p.toggle()).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            // ---- ADC / PWM ----
            "RustNet.Hal.Adc::ReadRaw(i4)" => {
                let ch = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let raw = board.adc(ch).and_then(|c| c.read_raw()).map_err(|e| e.to_string())?;
                Ok(HostValue::I32(raw as i32))
            }
            "RustNet.Hal.Adc::ReadMillivolts(i4)" => {
                let ch = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let mv =
                    board.adc(ch).and_then(|c| c.read_millivolts()).map_err(|e| e.to_string())?;
                Ok(HostValue::I32(mv as i32))
            }
            "RustNet.Hal.Pwm::Configure(i4,i4,i4)" => {
                let ch = Self::arg_i32(a, 0)? as u8;
                let hz = Self::arg_i32(a, 1)? as u32;
                let duty = Self::arg_i32(a, 2)? as u16;
                let mut board = self.state.board.lock().unwrap();
                let p = board.pwm(ch).map_err(|e| e.to_string())?;
                p.set_frequency(hz).and_then(|_| p.set_duty(duty)).and_then(|_| p.enable())
                    .map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            // ---- I2C ----
            "RustNet.Hal.I2c::Write(i4,i4,u1[])" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u8;
                let data = match a.get(2) {
                    Some(HostValue::Bytes(b)) => b.clone(),
                    other => return Err(format!("expected byte[], got {other:?}")),
                };
                let mut board = self.state.board.lock().unwrap();
                board.i2c(bus).and_then(|b| b.write(addr, &data)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.I2c::Read(i4,i4,i4)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u8;
                let len = Self::arg_i32(a, 2)? as usize;
                let mut buf = vec![0u8; len];
                let mut board = self.state.board.lock().unwrap();
                board.i2c(bus).and_then(|b| b.read(addr, &mut buf)).map_err(|e| e.to_string())?;
                Ok(HostValue::Bytes(buf))
            }
            // ---- FileSystem ----
            "RustNet.IO.FileSystem::WriteAllText(string,string)" => {
                let path = Self::arg_str(a, 0)?;
                let text = Self::arg_str(a, 1)?;
                self.state.fs.lock().unwrap().write(&path, text.as_bytes()).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::AppendText(string,string)" => {
                let path = Self::arg_str(a, 0)?;
                let text = Self::arg_str(a, 1)?;
                self.state.fs.lock().unwrap().append(&path, text.as_bytes()).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::ReadAllText(string)" => {
                let path = Self::arg_str(a, 0)?;
                let data = self.state.fs.lock().unwrap().read(&path).map_err(|e| e.to_string())?;
                Ok(HostValue::Str(String::from_utf8_lossy(&data).to_string()))
            }
            "RustNet.IO.FileSystem::Exists(string)" => {
                let path = Self::arg_str(a, 0)?;
                Ok(HostValue::Bool(self.state.fs.lock().unwrap().exists(&path)))
            }
            "RustNet.IO.FileSystem::Delete(string)" => {
                let path = Self::arg_str(a, 0)?;
                self.state.fs.lock().unwrap().delete(&path).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::List(string)" => {
                let path = Self::arg_str(a, 0)?;
                let entries = self.state.fs.lock().unwrap().list(&path).map_err(|e| e.to_string())?;
                let names: Vec<String> = entries.into_iter().map(|e| e.name).collect();
                Ok(HostValue::Str(names.join("\n")))
            }
            "RustNet.IO.FileSystem::CreateDirectory(string)" => {
                let path = Self::arg_str(a, 0)?;
                match self.state.fs.lock().unwrap().mkdir(&path) {
                    Ok(()) | Err(rustnet_fs::FsError::AlreadyExists(_)) => Ok(HostValue::Void),
                    Err(e) => Err(e.to_string()),
                }
            }
            // ---- Display (virtual panel; tools fetch it via GET_DISPLAY) ----
            "RustNet.Graphics.Display::Init(i4,i4)" => {
                let w = Self::arg_i32(a, 0)?.clamp(1, 1024) as u32;
                let h = Self::arg_i32(a, 1)?.clamp(1, 1024) as u32;
                *self.state.display.lock().unwrap() = Some(Framebuffer::new(w, h));
                Ok(HostValue::Void)
            }
            // Panel configuration: driver select + physical size + rotation.
            // The virtual device is a framebuffer, so the driver id is
            // advisory (logged); size and rotation shape the surface.
            "RustNet.Graphics.Display::ConfigurePanel(i4,i4,i4,i4)" => {
                let driver = Self::arg_i32(a, 0)?;
                let w = Self::arg_i32(a, 1)?.clamp(1, 1024) as u32;
                let h = Self::arg_i32(a, 2)?.clamp(1, 1024) as u32;
                let rotation = Self::arg_i32(a, 3)?.rem_euclid(360) as u16;
                let mut fb = Framebuffer::new(w, h);
                fb.set_rotation(rotation);
                *self.state.display.lock().unwrap() = Some(fb);
                self.state.logger.info(
                    "display",
                    format!("panel driver={driver} {w}x{h} rot={rotation}"),
                );
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::Width()" => {
                let w = self
                    .state
                    .display
                    .lock()
                    .unwrap()
                    .as_ref()
                    .map(|fb| fb.logical_size().0 as i32)
                    .unwrap_or(0);
                Ok(HostValue::I32(w))
            }
            "RustNet.Graphics.Display::Height()" => {
                let h = self
                    .state
                    .display
                    .lock()
                    .unwrap()
                    .as_ref()
                    .map(|fb| fb.logical_size().1 as i32)
                    .unwrap_or(0);
                Ok(HostValue::I32(h))
            }
            "RustNet.Graphics.Display::SetClip(i4,i4,i4,i4)" => {
                let (x, y, w, h) = (
                    Self::arg_i32(a, 0)?,
                    Self::arg_i32(a, 1)?,
                    Self::arg_i32(a, 2)?,
                    Self::arg_i32(a, 3)?,
                );
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.set_clip(x, y, w, h);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::ClearClip()" => {
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.clear_clip();
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::Clear(i4)" => {
                let c = Color(Self::arg_i32(a, 0)? as u16);
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.clear(c);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::SetPixel(i4,i4,i4)" => {
                let (x, y) = (Self::arg_i32(a, 0)?, Self::arg_i32(a, 1)?);
                let c = Color(Self::arg_i32(a, 2)? as u16);
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.set_pixel(x, y, c);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::FillRect(i4,i4,i4,i4,i4)" => {
                let (x, y, w, h) = (
                    Self::arg_i32(a, 0)?,
                    Self::arg_i32(a, 1)?,
                    Self::arg_i32(a, 2)?,
                    Self::arg_i32(a, 3)?,
                );
                let c = Color(Self::arg_i32(a, 4)? as u16);
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.fill_rect(x, y, w, h, c);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::FillCircle(i4,i4,i4,i4)" => {
                let (cx, cy, r) = (Self::arg_i32(a, 0)?, Self::arg_i32(a, 1)?, Self::arg_i32(a, 2)?);
                let c = Color(Self::arg_i32(a, 3)? as u16);
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.fill_circle(cx, cy, r, c);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::DrawText(i4,i4,string,i4,i4)" => {
                let (x, y) = (Self::arg_i32(a, 0)?, Self::arg_i32(a, 1)?);
                let text = Self::arg_str(a, 2)?;
                let c = Color(Self::arg_i32(a, 3)? as u16);
                let scale = Self::arg_i32(a, 4)?;
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.draw_text(x, y, &text, c, scale);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::DrawLine(i4,i4,i4,i4,i4)" => {
                let (x0, y0, x1, y1) = (
                    Self::arg_i32(a, 0)?,
                    Self::arg_i32(a, 1)?,
                    Self::arg_i32(a, 2)?,
                    Self::arg_i32(a, 3)?,
                );
                let c = Color(Self::arg_i32(a, 4)? as u16);
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.draw_line(x0, y0, x1, y1, c);
                }
                Ok(HostValue::Void)
            }
            // Blit a decoded image: x, y, w, h, RGB565 little-endian bytes.
            "RustNet.Graphics.Display::DrawImage(i4,i4,i4,i4,u1[])" => {
                let (x, y) = (Self::arg_i32(a, 0)?, Self::arg_i32(a, 1)?);
                let w = Self::arg_i32(a, 2)?.max(0) as u32;
                let h = Self::arg_i32(a, 3)?.max(0) as u32;
                let bytes = Self::arg_bytes(a, 4)?;
                let src: Vec<u16> = bytes
                    .chunks_exact(2)
                    .map(|c| u16::from_le_bytes([c[0], c[1]]))
                    .collect();
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.draw_image(x, y, w, h, &src);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::FillGradient(i4,i4,i4,i4,i4,i4,bool)" => {
                let (x, y) = (Self::arg_i32(a, 0)?, Self::arg_i32(a, 1)?);
                let (w, h) = (Self::arg_i32(a, 2)?, Self::arg_i32(a, 3)?);
                let c0 = Color(Self::arg_i32(a, 4)? as u16);
                let c1 = Color(Self::arg_i32(a, 5)? as u16);
                let vertical = Self::arg_bool(a, 6)?;
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.fill_gradient(x, y, w, h, c0, c1, vertical);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::BlendImage(i4,i4,i4,i4,u1[],i4)" => {
                let (x, y) = (Self::arg_i32(a, 0)?, Self::arg_i32(a, 1)?);
                let w = Self::arg_i32(a, 2)?.max(0) as u32;
                let h = Self::arg_i32(a, 3)?.max(0) as u32;
                let bytes = Self::arg_bytes(a, 4)?;
                let alpha = Self::arg_i32(a, 5)?.clamp(0, 255) as u8;
                let src: Vec<u16> = bytes
                    .chunks_exact(2)
                    .map(|c| u16::from_le_bytes([c[0], c[1]]))
                    .collect();
                if let Some(fb) = self.state.display.lock().unwrap().as_mut() {
                    fb.blend_image(x, y, w, h, &src, alpha);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::Present()" => {
                // Flush the framebuffer to the physical panel (if any) WITHOUT
                // cloning — a 320x240 RGB565 frame is 150 KB and a copy would
                // double peak heap. Lock order is always display→board (the
                // board never locks display), so this cannot deadlock.
                let disp = self.state.display.lock().unwrap();
                if let Some(fb) = disp.as_ref() {
                    let (w, h) = (fb.width, fb.height);
                    let res = self.state.board.lock().unwrap().present_frame(&fb.pixels, w, h);
                    // Log the first flush outcome once — the only bring-up signal
                    // available while the panel itself is off-screen.
                    if !self.state.panel_logged.swap(true, Ordering::Relaxed) {
                        match &res {
                            Ok(()) => self
                                .state
                                .logger
                                .info("display", format!("present_frame ok ({w}x{h})")),
                            Err(e) => self
                                .state
                                .logger
                                .warn("display", format!("present_frame failed: {e}")),
                        }
                    }
                }
                drop(disp);
                Ok(HostValue::Void)
            }
            // ---- image decoders (heavy formats decode in Rust) ----
            #[cfg(not(feature = "image-codecs"))]
            "RustNet.Drawing.Native::DecodeRgb565(u1[])" => {
                Err("PNG/JPEG decoding is not available on this chip".to_string())
            }
            #[cfg(feature = "image-codecs")]
            "RustNet.Drawing.Native::DecodeRgb565(u1[])" => {
                let bytes = Self::arg_bytes(a, 0)?;
                let img = image::load_from_memory(&bytes)
                    .map_err(|e| format!("image decode failed: {e}"))?;
                let rgb = img.to_rgb8();
                let (w, h) = (rgb.width(), rgb.height());
                if w > u16::MAX as u32 || h > u16::MAX as u32 {
                    return Err(format!("image too large: {w}x{h}"));
                }
                let mut out = Vec::with_capacity(4 + (w * h * 2) as usize);
                out.extend_from_slice(&(w as u16).to_le_bytes());
                out.extend_from_slice(&(h as u16).to_le_bytes());
                for px in rgb.pixels() {
                    let [r, g, b] = px.0;
                    let v = (((r as u16) & 0xF8) << 8)
                        | (((g as u16) & 0xFC) << 3)
                        | ((b as u16) >> 3);
                    out.extend_from_slice(&v.to_le_bytes());
                }
                Ok(HostValue::Bytes(out))
            }
            // ---- camera (v0.8, chip-gated; sim = colour bars) ----
            "RustNet.Media.Camera::ConfigureRaw(i4,i4,i4)" => {
                let w = Self::arg_i32(a, 0)?.max(0) as u32;
                let h = Self::arg_i32(a, 1)?.max(0) as u32;
                let format = match Self::arg_i32(a, 2)? {
                    1 => PixelFormat::Grayscale,
                    _ => PixelFormat::Rgb565,
                };
                self.state
                    .camera
                    .lock()
                    .unwrap()
                    .configure(CameraConfig { width: w, height: h, format })
                    .map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Media.Camera::Capture()" => {
                let frame =
                    self.state.camera.lock().unwrap().capture().map_err(|e| e.to_string())?;
                Ok(HostValue::Bytes(frame))
            }
            "RustNet.Media.Camera::Width()" => {
                Ok(HostValue::I32(self.state.camera.lock().unwrap().config().width as i32))
            }
            "RustNet.Media.Camera::Height()" => {
                Ok(HostValue::I32(self.state.camera.lock().unwrap().config().height as i32))
            }
            // ---- audio playback (v0.8, over the I2S HAL) ----
            "RustNet.Media.Audio::Configure(i4,i4,i4)" => {
                let sample_rate = Self::arg_i32(a, 0)?.max(1) as u32;
                let bits = Self::arg_i32(a, 1)?.clamp(8, 32) as u8;
                let channels = Self::arg_i32(a, 2)?.clamp(1, 2) as u8;
                let mut board = self.state.board.lock().unwrap();
                let cfg = I2sConfig { sample_rate, bits_per_sample: bits, channels,
                    format: I2sFormat::Standard };
                board.i2s(0).and_then(|d| d.configure(cfg)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Media.Audio::Play(u1[])" => {
                let bytes = Self::arg_bytes(a, 0)?;
                // Interpret the buffer as little-endian 16-bit PCM samples.
                let samples: Vec<i16> = bytes
                    .chunks_exact(2)
                    .map(|c| i16::from_le_bytes([c[0], c[1]]))
                    .collect();
                let mut board = self.state.board.lock().unwrap();
                let n = board.i2s(0).and_then(|d| d.write(&samples)).map_err(|e| e.to_string())?;
                *self.state.audio_played.lock().unwrap() += n as u64;
                Ok(HostValue::I32(n as i32))
            }
            "RustNet.Media.Audio::SamplesPlayed()" => {
                let n = *self.state.audio_played.lock().unwrap();
                Ok(HostValue::I32(n.min(i32::MAX as u64) as i32))
            }
            // ---- MJPEG video: JPEG-encode a frame (for the video stream) ----
            #[cfg(not(feature = "image-codecs"))]
            "RustNet.Media.Video::EncodeJpeg(u1[],i4,i4,i4)" => {
                Err("JPEG encoding is not available on this chip".to_string())
            }
            #[cfg(feature = "image-codecs")]
            "RustNet.Media.Video::EncodeJpeg(u1[],i4,i4,i4)" => {
                let bytes = Self::arg_bytes(a, 0)?;
                let w = Self::arg_i32(a, 1)?.max(0) as u32;
                let h = Self::arg_i32(a, 2)?.max(0) as u32;
                let quality = Self::arg_i32(a, 3)?.clamp(1, 100) as u8;
                // RGB565 LE -> RGB8 for the encoder.
                let mut rgb = Vec::with_capacity((w * h * 3) as usize);
                for px in bytes.chunks_exact(2) {
                    let v = u16::from_le_bytes([px[0], px[1]]);
                    rgb.push((((v >> 11) & 0x1F) as u32 * 255 / 31) as u8);
                    rgb.push((((v >> 5) & 0x3F) as u32 * 255 / 63) as u8);
                    rgb.push(((v & 0x1F) as u32 * 255 / 31) as u8);
                }
                let mut out = Vec::new();
                let mut enc =
                    image::codecs::jpeg::JpegEncoder::new_with_quality(&mut out, quality);
                enc.encode(&rgb, w, h, image::ExtendedColorType::Rgb8)
                    .map_err(|e| format!("jpeg encode failed: {e}"))?;
                Ok(HostValue::Bytes(out))
            }
            // ---- VNC server: stream the framebuffer over RFB/TCP ----
            "RustNet.Media.Vnc::Start(i4)" => {
                let port = Self::arg_i32(a, 0)?.clamp(1, 65535) as u16;
                if self.state.vnc_running.load(Ordering::SeqCst) {
                    return Ok(HostValue::Bool(true));
                }
                crate::vnc::start(
                    port,
                    self.state.display.clone(),
                    self.state.vnc_running.clone(),
                    self.state.logger.clone(),
                );
                Ok(HostValue::Bool(true))
            }
            "RustNet.Media.Vnc::Stop()" => {
                self.state.vnc_running.store(false, Ordering::SeqCst);
                Ok(HostValue::Void)
            }
            "RustNet.Media.Vnc::IsRunning()" => {
                Ok(HostValue::Bool(self.state.vnc_running.load(Ordering::SeqCst)))
            }
            // ---- USB device side (client / PC communication over CDC-ACM) ----
            "RustNet.Usb.UsbClient::BeginCdc(i4,i4,string)" => {
                let vid = Self::arg_i32(a, 0)? as u16;
                let pid = Self::arg_i32(a, 1)? as u16;
                let product = Self::arg_str(a, 2)?;
                let descriptor = DeviceDescriptor {
                    vendor_id: vid,
                    product_id: pid,
                    class: UsbClass::Cdc,
                    manufacturer: "RustNet".to_string(),
                    product,
                    serial: "0001".to_string(),
                };
                let mut device = UsbDevice::new(descriptor, Box::new(CdcAcm::new()));
                self.state.usb_bus.lock().unwrap().attach(&mut device);
                *self.state.usb_device.lock().unwrap() = Some(device);
                Ok(HostValue::Bool(true))
            }
            "RustNet.Usb.UsbClient::Read()" => {
                let mut dev = self.state.usb_device.lock().unwrap();
                let data = dev
                    .as_mut()
                    .and_then(|d| d.class_mut::<CdcAcm>())
                    .map(|c| c.take_rx())
                    .unwrap_or_default();
                Ok(HostValue::Bytes(data))
            }
            "RustNet.Usb.UsbClient::Write(u1[])" => {
                let data = Self::arg_bytes(a, 0)?;
                let mut dev = self.state.usb_device.lock().unwrap();
                if let Some(c) = dev.as_mut().and_then(|d| d.class_mut::<CdcAcm>()) {
                    c.queue_tx(&data);
                }
                Ok(HostValue::Void)
            }
            // ---- USB host side (enumerate + bulk transfer) ----
            "RustNet.Usb.UsbHost::Enumerate()" => {
                let mut host = self.state.usb_host.lock().unwrap();
                let mut bus = self.state.usb_bus.lock().unwrap();
                let info = match host.enumerate(&mut bus) {
                    Some(d) => format!(
                        "{:04x}:{:04x}:{}:{}",
                        d.vendor_id,
                        d.product_id,
                        match d.class {
                            UsbClass::Cdc => "cdc",
                            UsbClass::Hid => "hid",
                            UsbClass::MassStorage => "msc",
                            UsbClass::Vendor => "vendor",
                        },
                        d.product
                    ),
                    None => String::new(),
                };
                Ok(HostValue::Str(info))
            }
            "RustNet.Usb.UsbHost::BulkOut(u1[])" => {
                let data = Self::arg_bytes(a, 0)?;
                let mut host = self.state.usb_host.lock().unwrap();
                let mut bus = self.state.usb_bus.lock().unwrap();
                let mut dev = self.state.usb_device.lock().unwrap();
                if let Some(device) = dev.as_mut() {
                    host.bulk_out(&mut bus, device, &data);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Usb.UsbHost::BulkIn()" => {
                let mut host = self.state.usb_host.lock().unwrap();
                let mut bus = self.state.usb_bus.lock().unwrap();
                let mut dev = self.state.usb_device.lock().unwrap();
                let data = match dev.as_mut() {
                    Some(device) => host.bulk_in(&mut bus, device),
                    None => Vec::new(),
                };
                Ok(HostValue::Bytes(data))
            }
            // ---- WiFi ----
            "RustNet.Net.Wifi::Connect(string,string)" => {
                let ssid = Self::arg_str(a, 0)?;
                let psk = Self::arg_str(a, 1)?;
                let mut wifi = self.state.wifi.lock().unwrap();
                wifi.ssid = Some(ssid.clone());
                wifi.psk = Some(psk);
                wifi.connected = !ssid.is_empty();
                self.state.logger.info("wifi", format!("connected to '{ssid}'"));
                Ok(HostValue::Bool(wifi.connected))
            }
            "RustNet.Net.Wifi::IsConnected()" => {
                Ok(HostValue::Bool(self.state.wifi.lock().unwrap().connected))
            }
            // ---- MQTT ----
            "RustNet.Net.Mqtt::Connect(string,string)" => {
                let addr = Self::arg_str(a, 0)?;
                let client_id = Self::arg_str(a, 1)?;
                match MqttClient::connect(&addr, &client_id) {
                    Ok(c) => {
                        self.mqtt = Some(c);
                        Ok(HostValue::Bool(true))
                    }
                    Err(e) => {
                        self.state.logger.warn("mqtt", format!("connect failed: {e:?}"));
                        Ok(HostValue::Bool(false))
                    }
                }
            }
            "RustNet.Net.Mqtt::ConnectAuth(string,string,string,string)" => {
                let addr = Self::arg_str(a, 0)?;
                let client_id = Self::arg_str(a, 1)?;
                let user = Self::arg_str(a, 2)?;
                let pass = Self::arg_str(a, 3)?;
                let u = if user.is_empty() { None } else { Some(user.as_str()) };
                let p = if pass.is_empty() { None } else { Some(pass.as_str()) };
                match MqttClient::connect_auth(&addr, &client_id, u, p) {
                    Ok(c) => {
                        self.mqtt = Some(c);
                        Ok(HostValue::Bool(true))
                    }
                    Err(e) => {
                        self.state.logger.warn("mqtt", format!("connect(auth) failed: {e:?}"));
                        Ok(HostValue::Bool(false))
                    }
                }
            }
            // ---- Security: HMAC-SHA256 (cloud IoT SAS/JWT signing) ----
            "RustNet.Security.Hmac::Sha256Base64(u1[],string)" => {
                let key = Self::arg_bytes(a, 0)?;
                let data = Self::arg_str(a, 1)?;
                let mac = rustnet_crypto::hmac_sha256(&key, data.as_bytes());
                Ok(HostValue::Str(base64_standard(&mac)))
            }
            "RustNet.Net.Mqtt::Publish(string,string,i4)" => {
                let topic = Self::arg_str(a, 0)?;
                let payload = Self::arg_str(a, 1)?;
                let qos = Self::arg_i32(a, 2)? as u8;
                let client = self.mqtt.as_mut().ok_or("MQTT not connected")?;
                client.publish(&topic, payload.as_bytes(), qos).map_err(|e| format!("{e:?}"))?;
                Ok(HostValue::Void)
            }
            "RustNet.Net.Mqtt::Subscribe(string)" => {
                let topic = Self::arg_str(a, 0)?;
                let client = self.mqtt.as_mut().ok_or("MQTT not connected")?;
                client.subscribe(&topic).map_err(|e| format!("{e:?}"))?;
                Ok(HostValue::Void)
            }
            "RustNet.Net.Mqtt::Poll()" => {
                let client = self.mqtt.as_mut().ok_or("MQTT not connected")?;
                let (topic, payload) = client.poll_message().map_err(|e| format!("{e:?}"))?;
                Ok(HostValue::Str(format!("{topic}\n{}", String::from_utf8_lossy(&payload))))
            }
            // ---- HTTP ----
            "RustNet.Net.Http::Get(string,string)" => {
                let addr = Self::arg_str(a, 0)?;
                let path = Self::arg_str(a, 1)?;
                let resp = rustnet_net::http::get(&addr, &path).map_err(|e| e.to_string())?;
                Ok(HostValue::Str(resp.body_text()))
            }
            // ---- Diagnostics ----
            "RustNet.Diagnostics.Log::Info(string)" => {
                self.state.logger.info("managed", Self::arg_str(a, 0)?);
                Ok(HostValue::Void)
            }
            "RustNet.Diagnostics.Log::Warn(string)" => {
                self.state.logger.warn("managed", Self::arg_str(a, 0)?);
                Ok(HostValue::Void)
            }
            "RustNet.Diagnostics.Log::Error(string)" => {
                self.state.logger.error("managed", Self::arg_str(a, 0)?);
                Ok(HostValue::Void)
            }
            // ---- Device info / power ----
            "RustNet.Sys.DeviceInfo::Chip()" => {
                Ok(HostValue::Str(crate::chip::chip_family().name().to_string()))
            }
            "RustNet.Sys.DeviceInfo::Board()" => {
                Ok(HostValue::Str(crate::chip::board_name().to_string()))
            }
            "RustNet.Sys.Power::BatteryMillivolts()" => {
                let mut board = self.state.board.lock().unwrap();
                let mv = board.power().battery().map(|b| b.millivolts).unwrap_or(0);
                Ok(HostValue::I32(mv as i32))
            }
            "RustNet.Sys.Power::Sleep(i4,i4)" => {
                let mode = match Self::arg_i32(a, 0)? {
                    0 => rustnet_hal::power::SleepMode::Light,
                    1 => rustnet_hal::power::SleepMode::Deep,
                    _ => rustnet_hal::power::SleepMode::Hibernate,
                };
                let ms = Self::arg_i32(a, 1)? as u64;
                let mut board = self.state.board.lock().unwrap();
                board.power().sleep(mode, Some(ms)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            // ---- UART ----
            "RustNet.Hal.Uart::Configure(i4,i4)" => {
                let port = Self::arg_i32(a, 0)? as u8;
                let baud = Self::arg_i32(a, 1)? as u32;
                let mut board = self.state.board.lock().unwrap();
                let config = rustnet_hal::uart::UartConfig { baud, ..Default::default() };
                board.uart(port).and_then(|u| u.configure(config)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Uart::Write(i4,u1[])" => {
                let port = Self::arg_i32(a, 0)? as u8;
                let data = Self::arg_bytes(a, 1)?;
                let mut board = self.state.board.lock().unwrap();
                board.uart(port).and_then(|u| u.write(&data)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Uart::Read(i4,i4)" => {
                let port = Self::arg_i32(a, 0)? as u8;
                let max = Self::arg_i32(a, 1)?.max(0) as usize;
                let mut buf = vec![0u8; max];
                let mut board = self.state.board.lock().unwrap();
                let n = board.uart(port).and_then(|u| u.read(&mut buf)).map_err(|e| e.to_string())?;
                buf.truncate(n);
                Ok(HostValue::Bytes(buf))
            }
            "RustNet.Hal.Uart::Available(i4)" => {
                let port = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let n = board.uart(port).and_then(|u| u.bytes_available()).map_err(|e| e.to_string())?;
                Ok(HostValue::I32(n as i32))
            }
            // ---- CAN bus ----
            "RustNet.Buses.Can::Init(i4,i4,bool)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let config = rustnet_hal::can::CanConfig {
                    bitrate: Self::arg_i32(a, 1)? as u32,
                    loopback: Self::arg_bool(a, 2)?,
                    silent: false,
                };
                let mut board = self.state.board.lock().unwrap();
                board.can(bus).and_then(|c| c.configure(config)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Buses.Can::Write(i4,i4,u1[])" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let id = Self::arg_i32(a, 1)? as u32;
                let data = Self::arg_bytes(a, 2)?;
                let frame = rustnet_hal::can::CanFrame::new(id, &data);
                let mut board = self.state.board.lock().unwrap();
                board.can(bus).and_then(|c| c.transmit(&frame)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Buses.Can::Available(i4)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let n = board.can(bus).map(|c| c.rx_pending()).map_err(|e| e.to_string())?;
                Ok(HostValue::I32(n as i32))
            }
            // Frame comes back packed: id u32 LE | flags u8 (b0 ext, b1 rtr) | len u8 | data.
            "RustNet.Buses.Can::ReadRaw(i4)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let frame = board.can(bus).and_then(|c| c.receive()).map_err(|e| e.to_string())?;
                match frame {
                    None => Ok(HostValue::Null),
                    Some(f) => {
                        let mut out = Vec::with_capacity(6 + f.data.len());
                        out.extend_from_slice(&f.id.to_le_bytes());
                        out.push(f.extended as u8 | (f.rtr as u8) << 1);
                        out.push(f.data.len() as u8);
                        out.extend_from_slice(&f.data);
                        Ok(HostValue::Bytes(out))
                    }
                }
            }
            "RustNet.Buses.Can::SetFilter(i4,i4,i4)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let id = Self::arg_i32(a, 1)? as u32;
                let mask = Self::arg_i32(a, 2)? as u32;
                let mut board = self.state.board.lock().unwrap();
                board.can(bus).and_then(|c| c.set_filter(id, mask)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            // ---- Modbus master (registers travel as big-endian u16 pairs) ----
            "RustNet.Buses.Modbus::ReadHolding(i4,i4,i4)"
            | "RustNet.Buses.Modbus::ReadInput(i4,i4,i4)" => {
                use rustnet_net::modbus as mb;
                let unit = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u16;
                let count = Self::arg_i32(a, 2)? as u16;
                let f = if name.contains("Holding") { mb::fc::READ_HOLDING } else { mb::fc::READ_INPUT };
                let mut pdu = Vec::new();
                mb::build_read(f, addr, count, &mut pdu);
                let payload = self.modbus_transact(unit, &pdu)?;
                let regs = mb::parse_registers(&payload)?;
                let mut out = Vec::with_capacity(regs.len() * 2);
                for r in regs {
                    out.extend_from_slice(&r.to_be_bytes());
                }
                Ok(HostValue::Bytes(out))
            }
            "RustNet.Buses.Modbus::ReadCoils(i4,i4,i4)" => {
                use rustnet_net::modbus as mb;
                let unit = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u16;
                let count = Self::arg_i32(a, 2)? as u16;
                let mut pdu = Vec::new();
                mb::build_read(mb::fc::READ_COILS, addr, count, &mut pdu);
                let payload = self.modbus_transact(unit, &pdu)?;
                let bits = mb::parse_bits(&payload, count as usize)?;
                Ok(HostValue::Bytes(bits.into_iter().map(|b| b as u8).collect()))
            }
            "RustNet.Buses.Modbus::WriteCoil(i4,i4,bool)" => {
                use rustnet_net::modbus as mb;
                let unit = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u16;
                let on = Self::arg_bool(a, 2)?;
                let mut pdu = Vec::new();
                mb::build_write_coil(addr, on, &mut pdu);
                self.modbus_transact(unit, &pdu)?;
                Ok(HostValue::Void)
            }
            "RustNet.Buses.Modbus::WriteRegister(i4,i4,i4)" => {
                use rustnet_net::modbus as mb;
                let unit = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u16;
                let value = Self::arg_i32(a, 2)? as u16;
                let mut pdu = Vec::new();
                mb::build_write_register(addr, value, &mut pdu);
                self.modbus_transact(unit, &pdu)?;
                Ok(HostValue::Void)
            }
            "RustNet.Buses.Modbus::WriteRegistersRaw(i4,i4,u1[])" => {
                use rustnet_net::modbus as mb;
                let unit = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u16;
                let bytes = Self::arg_bytes(a, 2)?;
                let values: Vec<u16> = bytes
                    .chunks_exact(2)
                    .map(|c| u16::from_be_bytes([c[0], c[1]]))
                    .collect();
                let mut pdu = Vec::new();
                mb::build_write_registers(addr, &values, &mut pdu);
                self.modbus_transact(unit, &pdu)?;
                Ok(HostValue::Void)
            }
            // ---- 1-Wire ----
            "RustNet.Buses.OneWire::Reset(i4)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let presence = board.onewire(bus).and_then(|b| b.reset()).map_err(|e| e.to_string())?;
                Ok(HostValue::Bool(presence))
            }
            // ROM codes come back as 8 bytes each, little-endian.
            "RustNet.Buses.OneWire::SearchRaw(i4)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let roms = board.onewire(bus).and_then(|b| b.search()).map_err(|e| e.to_string())?;
                let mut out = Vec::with_capacity(roms.len() * 8);
                for rom in roms {
                    out.extend_from_slice(&rom.to_le_bytes());
                }
                Ok(HostValue::Bytes(out))
            }
            "RustNet.Buses.OneWire::Select(i4,i8)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let rom = Self::arg_i64(a, 1)? as u64;
                let mut board = self.state.board.lock().unwrap();
                board.onewire(bus).and_then(|b| b.select(rom)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Buses.OneWire::Skip(i4)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                board.onewire(bus).and_then(|b| b.skip()).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Buses.OneWire::Write(i4,u1[])" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let data = Self::arg_bytes(a, 1)?;
                let mut board = self.state.board.lock().unwrap();
                board.onewire(bus).and_then(|b| b.write(&data)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Buses.OneWire::Read(i4,i4)" => {
                let bus = Self::arg_i32(a, 0)? as u8;
                let len = Self::arg_i32(a, 1)? as usize;
                let mut buf = vec![0u8; len];
                let mut board = self.state.board.lock().unwrap();
                board.onewire(bus).and_then(|b| b.read(&mut buf)).map_err(|e| e.to_string())?;
                Ok(HostValue::Bytes(buf))
            }
            // ---- Network interfaces ----
            "RustNet.Net.Ethernet::Up()"
            | "RustNet.Net.Ethernet::Up(string,string)"
            | "RustNet.Net.Ppp::Up(i4,string,string)"
            | "RustNet.Net.Cellular::Up(string,string,string)" => {
                use rustnet_hal::netif::{NetIfConfig, NetIfKind};
                let (kind, config) = if name.starts_with("RustNet.Net.Ethernet") {
                    let config = if a.is_empty() {
                        NetIfConfig::default()
                    } else {
                        NetIfConfig {
                            static_ip: Self::arg_str(a, 0)?,
                            gateway: Self::arg_str(a, 1)?,
                            ..Default::default()
                        }
                    };
                    (NetIfKind::Ethernet, config)
                } else if name.starts_with("RustNet.Net.Ppp") {
                    (NetIfKind::Ppp, NetIfConfig {
                        uart_port: Self::arg_i32(a, 0)? as u8,
                        username: Self::arg_str(a, 1)?,
                        password: Self::arg_str(a, 2)?,
                        ..Default::default()
                    })
                } else {
                    (NetIfKind::Cellular, NetIfConfig {
                        apn: Self::arg_str(a, 0)?,
                        username: Self::arg_str(a, 1)?,
                        password: Self::arg_str(a, 2)?,
                        ..Default::default()
                    })
                };
                let mut board = self.state.board.lock().unwrap();
                let netif = board.netif(kind).map_err(|e| e.to_string())?;
                match netif.bring_up(&config) {
                    Ok(()) => {
                        let ip = netif.status().map(|s| s.ip).unwrap_or_default();
                        drop(board);
                        self.state.logger.info("net", format!("{} up, ip {ip}", kind.as_str()));
                        Ok(HostValue::Bool(true))
                    }
                    Err(e) => {
                        drop(board);
                        self.state.logger.warn("net", format!("{} up failed: {e}", kind.as_str()));
                        Ok(HostValue::Bool(false))
                    }
                }
            }
            "RustNet.Net.Ethernet::Down()" | "RustNet.Net.Ppp::Down()" | "RustNet.Net.Cellular::Down()" => {
                use rustnet_hal::netif::NetIfKind;
                let kind = if name.contains("Ethernet") {
                    NetIfKind::Ethernet
                } else if name.contains("Ppp") {
                    NetIfKind::Ppp
                } else {
                    NetIfKind::Cellular
                };
                let mut board = self.state.board.lock().unwrap();
                board.netif(kind).and_then(|n| n.bring_down()).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Net.Ethernet::GetIp()" | "RustNet.Net.Ppp::GetIp()" | "RustNet.Net.Cellular::GetIp()" => {
                use rustnet_hal::netif::NetIfKind;
                let kind = if name.contains("Ethernet") {
                    NetIfKind::Ethernet
                } else if name.contains("Ppp") {
                    NetIfKind::Ppp
                } else {
                    NetIfKind::Cellular
                };
                let mut board = self.state.board.lock().unwrap();
                let ip = board.netif(kind).and_then(|n| n.status()).map(|s| s.ip).map_err(|e| e.to_string())?;
                Ok(HostValue::Str(ip))
            }
            "RustNet.Net.Ethernet::IsUp()" | "RustNet.Net.Ppp::IsUp()" | "RustNet.Net.Cellular::IsUp()" => {
                use rustnet_hal::netif::NetIfKind;
                let kind = if name.contains("Ethernet") {
                    NetIfKind::Ethernet
                } else if name.contains("Ppp") {
                    NetIfKind::Ppp
                } else {
                    NetIfKind::Cellular
                };
                let mut board = self.state.board.lock().unwrap();
                let up = board.netif(kind).map(|n| n.is_up()).unwrap_or(false);
                Ok(HostValue::Bool(up))
            }
            "RustNet.Net.Cellular::GetOperator()" => {
                let mut board = self.state.board.lock().unwrap();
                let op = board
                    .netif(rustnet_hal::netif::NetIfKind::Cellular)
                    .and_then(|n| n.status())
                    .map(|s| s.operator_name)
                    .map_err(|e| e.to_string())?;
                Ok(HostValue::Str(op))
            }
            "RustNet.Net.Cellular::GetRssi()" => {
                let mut board = self.state.board.lock().unwrap();
                let rssi = board
                    .netif(rustnet_hal::netif::NetIfKind::Cellular)
                    .and_then(|n| n.status())
                    .map(|s| s.rssi_dbm)
                    .map_err(|e| e.to_string())?;
                Ok(HostValue::I32(rssi))
            }
            // ---- Power management ----
            "RustNet.Sys.Power::ArmWakeGpio(i4,bool)" => {
                let pin = Self::arg_i32(a, 0)? as u32;
                let rising = Self::arg_bool(a, 1)?;
                let mut board = self.state.board.lock().unwrap();
                board
                    .power()
                    .arm_wake(rustnet_hal::power::WakeSource::Gpio { pin, rising })
                    .map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Sys.Power::ArmWakeRtc(i4)" => {
                let seconds = Self::arg_i32(a, 0)?.max(0) as u64;
                let mut board = self.state.board.lock().unwrap();
                board
                    .power()
                    .arm_wake(rustnet_hal::power::WakeSource::Rtc { seconds })
                    .map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Sys.Power::ClearWakeSources()" => {
                self.state.board.lock().unwrap().power().clear_wake_sources();
                Ok(HostValue::Void)
            }
            "RustNet.Sys.Power::WakeReason()" => {
                let reason = self.state.board.lock().unwrap().power().wake_reason();
                Ok(HostValue::Str(reason.as_str().to_string()))
            }
            // On the virtual device Reset/Shutdown halt the app and log;
            // real chips reboot / power off here instead.
            "RustNet.Sys.Power::Reset()" => {
                self.state.logger.warn("power", "device reset requested (virtual device: app halted)".to_string());
                self.stop.store(true, Ordering::Relaxed);
                Ok(HostValue::Void)
            }
            "RustNet.Sys.Power::Shutdown()" => {
                self.state.logger.warn("power", "device shutdown requested (virtual device: app halted)".to_string());
                self.stop.store(true, Ordering::Relaxed);
                Ok(HostValue::Void)
            }
            // ---- RTC ----
            "RustNet.Sys.Rtc::Epoch()" => {
                let mut board = self.state.board.lock().unwrap();
                let epoch = board.rtc().epoch().map_err(|e| e.to_string())?;
                Ok(HostValue::I64(epoch as i64))
            }
            "RustNet.Sys.Rtc::Set(i8)" => {
                let epoch = Self::arg_i64(a, 0)?.max(0) as u64;
                let mut board = self.state.board.lock().unwrap();
                board
                    .rtc()
                    .set(rustnet_hal::rtc::DateTime::from_epoch(epoch))
                    .map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Sys.Rtc::NowString()" => {
                let mut board = self.state.board.lock().unwrap();
                let dt = board.rtc().now().map_err(|e| e.to_string())?;
                Ok(HostValue::Str(format!(
                    "{:04}-{:02}-{:02} {:02}:{:02}:{:02}",
                    dt.year, dt.month, dt.day, dt.hour, dt.minute, dt.second
                )))
            }
            "RustNet.Sys.Rtc::SetAlarm(i8)" => {
                let epoch = Self::arg_i64(a, 0)?.max(0) as u64;
                let mut board = self.state.board.lock().unwrap();
                board.rtc().set_alarm(epoch).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Sys.Rtc::ClearAlarm()" => {
                let mut board = self.state.board.lock().unwrap();
                board.rtc().clear_alarm().map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            // ---- Watchdog ----
            "RustNet.Sys.Watchdog::Start(i4)" => {
                let ms = Self::arg_i32(a, 0)?.max(1) as u32;
                let mut board = self.state.board.lock().unwrap();
                board.watchdog().start(ms).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Sys.Watchdog::Feed()" => {
                let mut board = self.state.board.lock().unwrap();
                board.watchdog().feed().map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Sys.Watchdog::Stop()" => {
                let mut board = self.state.board.lock().unwrap();
                board.watchdog().stop().map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Sys.Watchdog::IsRunning()" => {
                let mut board = self.state.board.lock().unwrap();
                let running = board.watchdog().is_running();
                Ok(HostValue::Bool(running))
            }
            // ---- External memory ----
            "RustNet.Sys.ExtMemory::Size(i4)" => {
                let idx = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let size = board.extmem(idx).map(|m| m.size()).map_err(|e| e.to_string())?;
                Ok(HostValue::I32(size as i32))
            }
            "RustNet.Sys.ExtMemory::Kind(i4)" => {
                let idx = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let kind = board.extmem(idx).map(|m| m.kind()).map_err(|e| e.to_string())?;
                Ok(HostValue::Str(
                    match kind {
                        rustnet_hal::extmem::ExtMemKind::QspiFlash => "qspi-flash",
                        rustnet_hal::extmem::ExtMemKind::Sdram => "sdram",
                    }
                    .to_string(),
                ))
            }
            "RustNet.Sys.ExtMemory::Read(i4,i4,i4)" => {
                let idx = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u32;
                let len = Self::arg_i32(a, 2)?.max(0) as usize;
                let mut buf = vec![0u8; len];
                let mut board = self.state.board.lock().unwrap();
                board.extmem(idx).and_then(|m| m.read(addr, &mut buf)).map_err(|e| e.to_string())?;
                Ok(HostValue::Bytes(buf))
            }
            "RustNet.Sys.ExtMemory::Write(i4,i4,u1[])" => {
                let idx = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u32;
                let data = Self::arg_bytes(a, 2)?;
                let mut board = self.state.board.lock().unwrap();
                board.extmem(idx).and_then(|m| m.write(addr, &data)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Sys.ExtMemory::Erase(i4,i4,i4)" => {
                let idx = Self::arg_i32(a, 0)? as u8;
                let addr = Self::arg_i32(a, 1)? as u32;
                let len = Self::arg_i32(a, 2)?.max(0) as u32;
                let mut board = self.state.board.lock().unwrap();
                board.extmem(idx).and_then(|m| m.erase(addr, len)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Sys.ExtMemory::SectorSize(i4)" => {
                let idx = Self::arg_i32(a, 0)? as u8;
                let mut board = self.state.board.lock().unwrap();
                let s = board.extmem(idx).map(|m| m.sector_size()).map_err(|e| e.to_string())?;
                Ok(HostValue::I32(s as i32))
            }
            // ---- Device info ----
            "RustNet.Sys.DeviceInfo::Version()" => {
                Ok(HostValue::Str(env!("CARGO_PKG_VERSION").to_string()))
            }
            "RustNet.Sys.DeviceInfo::UptimeMs()" => {
                Ok(HostValue::I64(self.state.epoch.elapsed().as_millis() as i64))
            }
            "RustNet.Sys.DeviceInfo::Json()" => {
                let uptime = self.state.epoch.elapsed().as_millis();
                Ok(HostValue::Str(format!(
                    "{{\"chip\":\"{}\",\"board\":\"{}\",\"version\":\"{}\",\"uptime_ms\":{uptime}}}",
                    crate::chip::chip_family().name(),
                    json_escape(crate::chip::board_name()),
                    env!("CARGO_PKG_VERSION"),
                )))
            }
            // ---- Signal control (timings travel as u32 LE arrays) ----
            "RustNet.Hal.Signal::GenerateRaw(i4,bool,u1[])" => {
                let pin = Self::arg_i32(a, 0)? as u32;
                let initial = Self::arg_bool(a, 1)?;
                let bytes = Self::arg_bytes(a, 2)?;
                let timings: Vec<u32> = bytes
                    .chunks_exact(4)
                    .map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]]))
                    .collect();
                let mut board = self.state.board.lock().unwrap();
                board.signal(pin).and_then(|s| s.generate(initial, &timings)).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Signal::CaptureRaw(i4,i4,i4)" => {
                let pin = Self::arg_i32(a, 0)? as u32;
                let max_edges = Self::arg_i32(a, 1)?.max(0) as usize;
                let timeout = Self::arg_i32(a, 2)?.max(0) as u32;
                let mut board = self.state.board.lock().unwrap();
                let widths = board
                    .signal(pin)
                    .and_then(|s| s.capture(max_edges, timeout))
                    .map_err(|e| e.to_string())?;
                let mut out = Vec::with_capacity(widths.len() * 4);
                for w in widths {
                    out.extend_from_slice(&w.to_le_bytes());
                }
                Ok(HostValue::Bytes(out))
            }
            "RustNet.Hal.Signal::PulseFeedback(i4,bool,i4,i4)" => {
                let pin = Self::arg_i32(a, 0)? as u32;
                let high = Self::arg_bool(a, 1)?;
                let pulse = Self::arg_i32(a, 2)?.max(0) as u32;
                let timeout = Self::arg_i32(a, 3)?.max(0) as u32;
                let mut board = self.state.board.lock().unwrap();
                let echo = board
                    .signal(pin)
                    .and_then(|s| s.pulse_feedback(high, pulse, timeout))
                    .map_err(|e| e.to_string())?;
                Ok(HostValue::I32(echo as i32))
            }
            // ---- Database ----
            "RustNet.Data.Db::Open(string)" => {
                let path = Self::arg_str(a, 0)?;
                let db = if path.is_empty() || path == ":memory:" {
                    rustnet_db::Database::in_memory()
                } else {
                    rustnet_db::Database::open(Box::new(VfsDbStorage {
                        fs: self.state.fs.clone(),
                        path,
                    }))?
                };
                let mut dbs = self.state.dbs.lock().unwrap();
                let handle = match dbs.iter().position(|d| d.is_none()) {
                    Some(i) => {
                        dbs[i] = Some(db);
                        i
                    }
                    None => {
                        dbs.push(Some(db));
                        dbs.len() - 1
                    }
                };
                Ok(HostValue::I32(handle as i32))
            }
            "RustNet.Data.Db::Exec(i4,string)" => {
                let handle = Self::arg_i32(a, 0)? as usize;
                let sql = Self::arg_str(a, 1)?;
                let mut dbs = self.state.dbs.lock().unwrap();
                let db = dbs
                    .get_mut(handle)
                    .and_then(|d| d.as_mut())
                    .ok_or("bad database handle")?;
                match db.execute(&sql, &[])? {
                    rustnet_db::ExecResult::Affected(n) => Ok(HostValue::I32(n as i32)),
                    rustnet_db::ExecResult::Rows { rows, .. } => Ok(HostValue::I32(rows.len() as i32)),
                }
            }
            "RustNet.Data.Db::Query(i4,string)" => {
                let handle = Self::arg_i32(a, 0)? as usize;
                let sql = Self::arg_str(a, 1)?;
                let mut dbs = self.state.dbs.lock().unwrap();
                let db = dbs
                    .get_mut(handle)
                    .and_then(|d| d.as_mut())
                    .ok_or("bad database handle")?;
                let result = db.execute(&sql, &[])?;
                Ok(HostValue::Str(db_result_json(&result)))
            }
            "RustNet.Data.Db::Close(i4)" => {
                let handle = Self::arg_i32(a, 0)? as usize;
                let mut dbs = self.state.dbs.lock().unwrap();
                if let Some(slot) = dbs.get_mut(handle) {
                    *slot = None;
                }
                Ok(HostValue::Void)
            }
            // ---- FileSystem byte APIs (streams) ----
            "RustNet.IO.FileSystem::ReadAllBytes(string)" => {
                let path = Self::arg_str(a, 0)?;
                let data = self.state.fs.lock().unwrap().read(&path).map_err(|e| e.to_string())?;
                Ok(HostValue::Bytes(data))
            }
            "RustNet.IO.FileSystem::WriteAllBytes(string,u1[])" => {
                let path = Self::arg_str(a, 0)?;
                let data = Self::arg_bytes(a, 1)?;
                self.state.fs.lock().unwrap().write(&path, &data).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::AppendBytes(string,u1[])" => {
                let path = Self::arg_str(a, 0)?;
                let data = Self::arg_bytes(a, 1)?;
                self.state.fs.lock().unwrap().append(&path, &data).map_err(|e| e.to_string())?;
                Ok(HostValue::Void)
            }
            other => Err(format!("unknown internal call: {other}")),
        }
    }
}

/// A managed app running on its own thread.
pub struct AppRunner {
    pub name: String,
    stop: Arc<AtomicBool>,
    handle: Option<std::thread::JoinHandle<()>>,
}

impl AppRunner {
    pub fn start(
        name: &str,
        rnx: Vec<u8>,
        state: SharedState,
        debug: Arc<Mutex<DebugState>>,
    ) -> Result<AppRunner, String> {
        // Validate before spawning so flashing errors surface synchronously.
        Module::from_bytes(&rnx)?;
        let stop = Arc::new(AtomicBool::new(false));
        let stop2 = stop.clone();
        let app_name = name.to_string();
        let thread_name = app_name.clone();
        let mut builder = std::thread::Builder::new().name(format!("app-{app_name}"));
        // On heap-constrained MCUs whose DRAM fragments after WiFi comes up
        // (ESP32: no contiguous 16 KB block for the default stack), the
        // firmware sets a modest release-build stack via `set_app_stack`.
        // Zero = OS default, which host/debug builds need (bigger frames).
        let stack = APP_STACK_BYTES.load(Ordering::Relaxed);
        if stack != 0 {
            builder = builder.stack_size(stack);
        }
        let handle = builder
            .spawn(move || {
                let module = Module::from_bytes(&rnx).expect("validated above");
                let logger = state.logger.clone();
                let perf = state.perf.clone();
                let host = FirmwareHost::new(state, stop2.clone());
                let mut interp = Interpreter::new(&module, host);
                logger.info("runtime", format!("app '{thread_name}' started"));
                loop {
                    if stop2.load(Ordering::Relaxed) {
                        logger.info("runtime", format!("app '{thread_name}' stopped"));
                        break;
                    }
                    // Apply breakpoint add/remove requests (allowed live).
                    {
                        let mut dbg = debug.lock().unwrap();
                        for (m, off) in dbg.pending_breakpoints.drain(..).collect::<Vec<_>>() {
                            interp.set_breakpoint(m, off);
                        }
                        for (m, off) in dbg.pending_clears.drain(..).collect::<Vec<_>>() {
                            interp.clear_breakpoint(m, off);
                        }
                    }
                    let exit = interp.run(100_000);
                    perf.update(|c| {
                        c.il_instructions = interp.instructions;
                        c.heap_used_bytes = interp.heap.used_bytes();
                        c.gc_collections = interp.heap.collections;
                        c.tasks_alive = 1;
                    });
                    match exit {
                        RunExit::OutOfFuel => continue,
                        RunExit::Completed => {
                            logger.info("runtime", format!("app '{thread_name}' exited"));
                            break;
                        }
                        RunExit::Error(e) => {
                            logger.error("runtime", format!("app '{thread_name}' crashed: {e}"));
                            break;
                        }
                        RunExit::Paused { method, il_offset } => {
                            {
                                let mut dbg = debug.lock().unwrap();
                                dbg.paused_at = Some((method, il_offset));
                                dbg.stack = interp
                                    .stack_trace()
                                    .into_iter()
                                    .map(|(name, off, line)| match line {
                                        Some(l) => format!("{name} @IL_{off:04x} (line {l})"),
                                        None => format!("{name} @IL_{off:04x}"),
                                    })
                                    .collect();
                                dbg.locals = interp.top_locals_display();
                                dbg.resume = false;
                                dbg.step = false;
                            }
                            logger.info(
                                "debug",
                                format!("breakpoint hit at method {method} IL_{il_offset:04x}"),
                            );
                            // Wait for resume (continue or step) or stop.
                            loop {
                                if stop2.load(Ordering::Relaxed) {
                                    break;
                                }
                                let mut dbg = debug.lock().unwrap();
                                if dbg.resume {
                                    // Step: stop again after one instruction.
                                    if dbg.step {
                                        interp.single_step = true;
                                    }
                                    dbg.resume = false;
                                    dbg.step = false;
                                    dbg.paused_at = None;
                                    break;
                                }
                                drop(dbg);
                                std::thread::sleep(std::time::Duration::from_millis(10));
                            }
                        }
                    }
                }
                perf.update(|c| c.tasks_alive = 0);
            })
            .map_err(|e| e.to_string())?;
        Ok(AppRunner { name: name.to_string(), stop, handle: Some(handle) })
    }

    pub fn stop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }

    pub fn is_running(&self) -> bool {
        self.handle.as_ref().map(|h| !h.is_finished()).unwrap_or(false)
    }
}

impl Drop for AppRunner {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(test)]
mod cloud_tests {
    use super::base64_standard;

    #[test]
    fn base64_padding_variants() {
        assert_eq!(base64_standard(b""), "");
        assert_eq!(base64_standard(b"f"), "Zg==");
        assert_eq!(base64_standard(b"fo"), "Zm8=");
        assert_eq!(base64_standard(b"foo"), "Zm9v");
        assert_eq!(base64_standard(b"foobar"), "Zm9vYmFy");
    }

    #[test]
    fn hmac_sha256_base64_building_block() {
        // RFC 4231 case 2 HMAC-SHA256, base64-encoded — the exact bytes the
        // Sha256Base64 intrinsic produces for Azure SAS / GCP JWT signing.
        let mac = rustnet_crypto::hmac_sha256(b"Jefe", b"what do ya want for nothing?");
        assert_eq!(base64_standard(&mac), "W9zBRr9gdU5qBCQmCJV1x1oAPwidJzmDnexYuWTsOEM=");
    }
}
