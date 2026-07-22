//! RNDP command handlers — the device side of every tool operation.

use crate::apphost::{AppRunner, DebugState, SharedState};
use crate::chip;
use crate::proto::*;
use rustnet_diag::Level;
use rustnet_ota::{MemSlots, OtaManager, Slot, SlotStorage};
use rustnet_secureboot::{verify, ImageKind};
use std::sync::{Arc, Mutex};

const FW_VERSION: &str = env!("CARGO_PKG_VERSION");

pub struct DeviceService {
    pub state: SharedState,
    /// Set by CMD_REBOOT; real-silicon transports restart the chip after
    /// writing the response (the virtual device just stops the app).
    pub reboot_requested: bool,
    runner: Option<AppRunner>,
    active_app: Option<String>,
    debug: Arc<Mutex<DebugState>>,
    ota: Option<OtaManager<MemSlots>>,
    config_key: [u8; 32],
}

impl DeviceService {
    pub fn new(state: SharedState) -> Self {
        // Device-unique config key; on real chips this comes from eFuse/OTP.
        let config_key = rustnet_crypto::sha256(b"rustnet-device-unique-secret-v1");
        let svc = Self {
            state,
            reboot_requested: false,
            runner: None,
            active_app: None,
            debug: Arc::new(Mutex::new(DebugState::default())),
            ota: None,
            config_key,
        };
        let _ = svc.state.fs.lock().unwrap().mkdir("/apps");
        let _ = svc.state.fs.lock().unwrap().mkdir("/data");
        svc.state
            .logger
            .info("boot", format!("{} (chip {})", chip::board_name(), chip::chip_family().name()));
        svc
    }

    /// Read a stored config value (transport layers use this for WiFi
    /// credentials at boot).
    pub fn read_config(&self, key: &str) -> Option<String> {
        self.config_get(key).ok().flatten()
    }

    fn pub_key(&self) -> Option<Vec<u8>> {
        self.state.fs.lock().unwrap().read("/device.pub").ok()
    }

    fn ota_manager(&mut self) -> &mut OtaManager<MemSlots> {
        if self.ota.is_none() {
            let key = self.pub_key().unwrap_or_default();
            self.ota = Some(OtaManager::new(MemSlots::new(), key, chip::chip_family()));
        }
        self.ota.as_mut().unwrap()
    }

    pub fn handle(&mut self, frame: &Frame) -> Frame {
        match self.dispatch(frame) {
            Ok(payload) => Frame::ok(payload),
            Err(e) => Frame::err(e),
        }
    }

    fn dispatch(&mut self, frame: &Frame) -> Result<Vec<u8>, String> {
        let p = &frame.payload;
        match frame.code {
            CMD_PING => Ok(vec![PROTOCOL_VERSION]),
            CMD_INFO => {
                let perf = self.state.perf.snapshot();
                let apps = self.app_names()?.len();
                let autostart = self.config_get("autostart").ok().flatten().filter(|n| !n.is_empty());
                let json = format!(
                    r#"{{"chip":"{}","board":"{}","version":"{}","protocol":{},"uptime_ms":{},"heap_used":{},"apps":{},"wifi":{},"active_app":{},"running":{},"autostart":{}}}"#,
                    chip::chip_family().name(),
                    chip::board_name(),
                    FW_VERSION,
                    PROTOCOL_VERSION,
                    self.state.epoch.elapsed().as_millis(),
                    perf.heap_used_bytes,
                    apps,
                    chip::has_wifi(),
                    self.active_app.as_deref().map(|a| format!("\"{a}\"")).unwrap_or("null".into()),
                    self.runner.as_ref().map(|r| r.is_running()).unwrap_or(false),
                    autostart.map(|a| format!("\"{a}\"")).unwrap_or("null".into()),
                );
                Ok(json.into_bytes())
            }
            CMD_PROVISION_KEY => {
                if self.pub_key().is_some() {
                    return Err("device already provisioned".into());
                }
                if p.is_empty() {
                    return Err("empty key".into());
                }
                self.state.fs.lock().unwrap().write("/device.pub", p).map_err(|e| e.to_string())?;
                self.state.logger.info("secure", "device public key provisioned");
                Ok(Vec::new())
            }
            CMD_LIST_APPS => {
                let names = self.app_names()?;
                let mut entries = Vec::new();
                for n in names {
                    let size = self
                        .state
                        .fs
                        .lock()
                        .unwrap()
                        .read(&format!("/apps/{n}.rnsb"))
                        .map(|d| d.len())
                        .unwrap_or(0);
                    let active = self.active_app.as_deref() == Some(n.as_str());
                    entries.push(format!(
                        r#"{{"name":"{n}","size":{size},"active":{active}}}"#
                    ));
                }
                Ok(format!("[{}]", entries.join(",")).into_bytes())
            }
            CMD_FLASH_APP => {
                if p.is_empty() {
                    return Err("empty payload".into());
                }
                let name_len = p[0] as usize;
                if p.len() < 1 + name_len {
                    return Err("truncated payload".into());
                }
                let name = String::from_utf8_lossy(&p[1..1 + name_len]).to_string();
                validate_name(&name)?;
                let container = &p[1 + name_len..];
                let key = self.pub_key().ok_or("device not provisioned: flash a key first")?;
                let image = verify(container, &key, chip::chip_family())
                    .map_err(|e| format!("signature check failed: {e}"))?;
                if image.kind != ImageKind::App {
                    return Err("container is not an app image".into());
                }
                // Validate the RNX payload parses before accepting.
                rustnet_core::Module::from_bytes(image.payload)
                    .map_err(|e| format!("invalid RNX: {e}"))?;
                self.state
                    .fs
                    .lock()
                    .unwrap()
                    .write(&format!("/apps/{name}.rnsb"), container)
                    .map_err(|e| e.to_string())?;
                if self.active_app.is_none() {
                    self.active_app = Some(name.clone());
                }
                // A fresh flash means a human is at the controls — clear any
                // autostart crash-loop counter.
                self.reset_autostart_fails();
                self.state.logger.info("flash", format!("app '{name}' flashed ({} bytes)", container.len()));
                Ok(Vec::new())
            }
            CMD_ERASE_APP => {
                let name = String::from_utf8_lossy(p).to_string();
                validate_name(&name)?;
                if self.runner.as_ref().map(|r| r.name == name).unwrap_or(false) {
                    self.stop_app();
                }
                self.state
                    .fs
                    .lock()
                    .unwrap()
                    .delete(&format!("/apps/{name}.rnsb"))
                    .map_err(|_| format!("app '{name}' not found"))?;
                if self.active_app.as_deref() == Some(name.as_str()) {
                    self.active_app = None;
                }
                // Erasing the autostart app disables autostart.
                if self.config_get("autostart").ok().flatten().as_deref() == Some(name.as_str()) {
                    let _ = self.config_set("autostart", "");
                }
                self.state.logger.info("flash", format!("app '{name}' erased"));
                Ok(Vec::new())
            }
            CMD_START_APP => {
                let name = String::from_utf8_lossy(p).to_string();
                validate_name(&name)?;
                self.launch_app(&name)?;
                // An explicit start makes this the autostart app and clears the
                // crash-loop counter (a human just chose to run it).
                let _ = self.config_set("autostart", &name);
                self.reset_autostart_fails();
                Ok(Vec::new())
            }
            CMD_SET_AUTOSTART => {
                if p.is_empty() {
                    self.config_set("autostart", "")?;
                    self.state.logger.info("boot", "autostart disabled");
                } else {
                    let name = String::from_utf8_lossy(p).to_string();
                    validate_name(&name)?;
                    if self.state.fs.lock().unwrap().read(&format!("/apps/{name}.rnsb")).is_err() {
                        return Err(format!("app '{name}' not found"));
                    }
                    self.config_set("autostart", &name)?;
                    self.reset_autostart_fails();
                    self.state.logger.info("boot", format!("autostart set to '{name}'"));
                }
                Ok(Vec::new())
            }
            CMD_STOP_APP => {
                self.stop_app();
                Ok(Vec::new())
            }
            CMD_FLASH_DATA => {
                if p.len() < 2 {
                    return Err("empty payload".into());
                }
                let path_len = u16::from_le_bytes([p[0], p[1]]) as usize;
                if p.len() < 2 + path_len {
                    return Err("truncated payload".into());
                }
                let path = String::from_utf8_lossy(&p[2..2 + path_len]).to_string();
                let data = &p[2 + path_len..];
                let full = format!("/data/{}", path.trim_start_matches('/'));
                self.ensure_parent_dirs(&full);
                self.state.fs.lock().unwrap().write(&full, data).map_err(|e| e.to_string())?;
                self.state.logger.info("data", format!("wrote {full} ({} bytes)", data.len()));
                Ok(Vec::new())
            }
            CMD_READ_DATA => {
                let path = String::from_utf8_lossy(p).to_string();
                let full = format!("/data/{}", path.trim_start_matches('/'));
                self.state.fs.lock().unwrap().read(&full).map_err(|e| e.to_string())
            }
            CMD_SET_CONFIG => {
                let text = String::from_utf8_lossy(p);
                let (key, value) = text.split_once('\n').ok_or("expected key\\nvalue")?;
                self.config_set(key, value)?;
                self.state.logger.info("config", format!("set '{key}'"));
                Ok(Vec::new())
            }
            CMD_GET_CONFIG => {
                let key = String::from_utf8_lossy(p).to_string();
                let value = self.config_get(&key)?.ok_or(format!("config '{key}' not set"))?;
                Ok(value.into_bytes())
            }
            CMD_WIFI_CONFIG => {
                if !chip::has_wifi() {
                    return Err(format!("chip {} has no WiFi", chip::chip_family().name()));
                }
                let text = String::from_utf8_lossy(p);
                let (ssid, psk) = text.split_once('\n').ok_or("expected ssid\\npsk")?;
                self.config_set("wifi.ssid", ssid)?;
                self.config_set("wifi.psk", psk)?;
                let mut wifi = self.state.wifi.lock().unwrap();
                wifi.ssid = Some(ssid.to_string());
                wifi.psk = Some(psk.to_string());
                self.state.logger.info("wifi", format!("configured SSID '{ssid}'"));
                Ok(Vec::new())
            }
            CMD_GET_LOGS => {
                let max = if p.len() >= 4 {
                    u32::from_le_bytes(p[..4].try_into().unwrap()) as usize
                } else {
                    100
                };
                let lines: Vec<String> = self
                    .state
                    .logger
                    .tail(max)
                    .into_iter()
                    .map(|r| format!("[{:>8}] {:5} {}: {}", r.timestamp_ms, r.level.as_str(), r.target, r.message))
                    .collect();
                Ok(lines.join("\n").into_bytes())
            }
            CMD_GET_PERF => {
                let c = self.state.perf.snapshot();
                let json = format!(
                    r#"{{"uptime_ms":{},"heap_used":{},"heap_total":{},"gc_collections":{},"il_instructions":{},"tasks_alive":{}}}"#,
                    self.state.epoch.elapsed().as_millis(),
                    c.heap_used_bytes,
                    c.heap_total_bytes.max(256 * 1024),
                    c.gc_collections,
                    c.il_instructions,
                    c.tasks_alive,
                );
                Ok(json.into_bytes())
            }
            CMD_SET_BOOT_IMAGE => {
                if p.len() < 4 {
                    return Err("expected w:u16 h:u16 rgb565".into());
                }
                let w = u16::from_le_bytes([p[0], p[1]]) as u32;
                let h = u16::from_le_bytes([p[2], p[3]]) as u32;
                if p.len() != 4 + (w * h * 2) as usize {
                    return Err(format!("boot image size mismatch for {w}x{h}"));
                }
                self.state.fs.lock().unwrap().write("/bootimg.bin", p).map_err(|e| e.to_string())?;
                self.state.logger.info("boot", format!("boot image set ({w}x{h})"));
                Ok(Vec::new())
            }
            CMD_GET_BOOT_IMAGE => {
                self.state.fs.lock().unwrap().read("/bootimg.bin").map_err(|_| "no boot image set".to_string())
            }
            CMD_GET_DISPLAY => {
                let display = self.state.display.lock().unwrap();
                let fb = display.as_ref().ok_or("no display initialized")?;
                let mut out = Vec::with_capacity(4 + fb.pixels.len() * 2);
                out.extend_from_slice(&(fb.width as u16).to_le_bytes());
                out.extend_from_slice(&(fb.height as u16).to_le_bytes());
                for px in &fb.pixels {
                    out.extend_from_slice(&px.to_le_bytes());
                }
                Ok(out)
            }
            CMD_IO_STATE => {
                // Snapshot of simulated I/O for the desktop simulator panel.
                let mut board = self.state.board.lock().unwrap();
                let pins: Vec<String> = (0..24u32)
                    .map(|p| {
                        board
                            .gpio(p)
                            .and_then(|g| g.read())
                            .map(|l| if l == rustnet_hal::gpio::Level::High { "1" } else { "0" })
                            .unwrap_or("0")
                            .to_string()
                    })
                    .collect();
                let can_rx: Vec<String> = (0..2u8)
                    .map(|b| board.can(b).map(|c| c.rx_pending()).unwrap_or(0).to_string())
                    .collect();
                let mut netifs = Vec::new();
                for kind in [
                    rustnet_hal::netif::NetIfKind::Wifi,
                    rustnet_hal::netif::NetIfKind::Ethernet,
                    rustnet_hal::netif::NetIfKind::Ppp,
                    rustnet_hal::netif::NetIfKind::Cellular,
                ] {
                    let (up, ip) = board
                        .netif(kind)
                        .and_then(|n| n.status())
                        .map(|s| (s.up, s.ip))
                        .unwrap_or((false, String::new()));
                    netifs.push(format!(
                        r#"{{"kind":"{}","up":{up},"ip":"{ip}"}}"#,
                        kind.as_str()
                    ));
                }
                let wd = board.watchdog();
                let (wd_running, wd_timeout) = (wd.is_running(), wd.timeout_ms());
                drop(board);
                let display = self
                    .state
                    .display
                    .lock()
                    .unwrap()
                    .as_ref()
                    .map(|fb| format!(r#"{{"width":{},"height":{}}}"#, fb.width, fb.height))
                    .unwrap_or("null".into());
                let json = format!(
                    r#"{{"pins":[{}],"can_rx":[{}],"netifs":[{}],"watchdog":{{"running":{wd_running},"timeout_ms":{wd_timeout}}},"display":{display}}}"#,
                    pins.join(","),
                    can_rx.join(","),
                    netifs.join(","),
                );
                Ok(json.into_bytes())
            }
            CMD_OTA_BEGIN => {
                self.ota_manager().begin();
                self.state.logger.info("ota", "update started");
                Ok(Vec::new())
            }
            CMD_OTA_DATA => {
                self.ota_manager().write_chunk(p).map_err(|e| format!("{e:?}"))?;
                Ok(Vec::new())
            }
            CMD_OTA_END => {
                self.ota_manager().finish().map_err(|e| format!("ota verify failed: {e:?}"))?;
                let slot = self.ota_manager().on_boot();
                self.state
                    .logger
                    .info("ota", format!("update verified; rebooted into slot {slot:?}"));
                Ok(format!("{slot:?}").into_bytes())
            }
            CMD_OTA_CONFIRM => {
                self.ota_manager().confirm_boot();
                let active = self.ota_manager().storage.state().active;
                self.state.logger.info("ota", format!("slot {active:?} confirmed"));
                Ok(format!("{active:?}").into_bytes())
            }
            CMD_OTA_ROLLBACK => {
                let slot: Slot = self.ota_manager().rollback().map_err(|e| format!("{e:?}"))?;
                self.state.logger.warn("ota", format!("rolled back to slot {slot:?}"));
                Ok(format!("{slot:?}").into_bytes())
            }
            CMD_DEBUG_SET_BP => {
                if p.len() != 8 {
                    return Err("expected method:u32 offset:u32".into());
                }
                let method = u32::from_le_bytes(p[0..4].try_into().unwrap());
                let offset = u32::from_le_bytes(p[4..8].try_into().unwrap());
                self.debug.lock().unwrap().pending_breakpoints.push((method, offset));
                Ok(Vec::new())
            }
            CMD_DEBUG_CLEAR_BP => {
                if p.len() != 8 {
                    return Err("expected method:u32 offset:u32".into());
                }
                let method = u32::from_le_bytes(p[0..4].try_into().unwrap());
                let offset = u32::from_le_bytes(p[4..8].try_into().unwrap());
                self.debug.lock().unwrap().pending_clears.push((method, offset));
                Ok(Vec::new())
            }
            CMD_DEBUG_CONTINUE => {
                let mut dbg = self.debug.lock().unwrap();
                if dbg.paused_at.is_none() {
                    return Err("app is not paused".into());
                }
                dbg.step = false;
                dbg.resume = true;
                Ok(Vec::new())
            }
            CMD_DEBUG_STEP => {
                let mut dbg = self.debug.lock().unwrap();
                if dbg.paused_at.is_none() {
                    return Err("app is not paused".into());
                }
                dbg.step = true;
                dbg.resume = true;
                Ok(Vec::new())
            }
            CMD_DEBUG_STATE => {
                let dbg = self.debug.lock().unwrap();
                // [paused:u8][method:u32][il:u32] (little-endian); paused=0 -> running.
                let mut out = Vec::with_capacity(9);
                match dbg.paused_at {
                    Some((m, off)) => {
                        out.push(1);
                        out.extend_from_slice(&m.to_le_bytes());
                        out.extend_from_slice(&off.to_le_bytes());
                    }
                    None => out.push(0),
                }
                Ok(out)
            }
            CMD_DEBUG_STACK => {
                let dbg = self.debug.lock().unwrap();
                if dbg.paused_at.is_none() {
                    return Err("app is not paused".into());
                }
                Ok(dbg.stack.join("\n").into_bytes())
            }
            CMD_DEBUG_LOCALS => {
                let dbg = self.debug.lock().unwrap();
                if dbg.paused_at.is_none() {
                    return Err("app is not paused".into());
                }
                Ok(dbg.locals.join("\n").into_bytes())
            }
            CMD_REBOOT => {
                self.stop_app();
                self.reboot_requested = true;
                self.state.logger.log(Level::Warn, "boot", "reboot requested".to_string());
                Ok(Vec::new())
            }
            other => Err(format!("unknown command 0x{other:02X}")),
        }
    }

    fn stop_app(&mut self) {
        if let Some(mut r) = self.runner.take() {
            r.stop();
        }
    }

    /// Load, verify and run a stored app. Shared by the start command and the
    /// boot-time autostart path.
    fn launch_app(&mut self, name: &str) -> Result<(), String> {
        self.stop_app();
        let container = self
            .state
            .fs
            .lock()
            .unwrap()
            .read(&format!("/apps/{name}.rnsb"))
            .map_err(|_| format!("app '{name}' not found"))?;
        let key = self.pub_key().ok_or("device not provisioned")?;
        let image = verify(&container, &key, chip::chip_family())
            .map_err(|e| format!("signature check failed: {e}"))?;
        let runner = AppRunner::start(
            name,
            image.payload.to_vec(),
            self.state.clone(),
            self.debug.clone(),
        )?;
        self.runner = Some(runner);
        self.active_app = Some(name.to_string());
        Ok(())
    }

    /// Guard against an autostart app that crashes the whole device: this counts
    /// consecutive unattended boots. Reset by any explicit flash/start/autostart.
    const MAX_AUTOSTART_FAILS: u32 = 3;

    fn autostart_fails(&self) -> u32 {
        self.config_get("autostart_fails").ok().flatten().and_then(|v| v.parse().ok()).unwrap_or(0)
    }

    fn reset_autostart_fails(&self) {
        let _ = self.config_set("autostart_fails", "0");
    }

    /// Launch the configured autostart app on boot, if any. Called once after
    /// the service is constructed, from every transport's boot sequence.
    /// A crash-loop guard skips autostart after several consecutive boots with
    /// no human intervention (so a bad app never bricks the device).
    pub fn try_autostart(&mut self) {
        let Some(name) = self.config_get("autostart").ok().flatten().filter(|n| !n.is_empty())
        else {
            return;
        };
        let fails = self.autostart_fails();
        if fails >= Self::MAX_AUTOSTART_FAILS {
            self.state.logger.warn(
                "boot",
                format!("autostart '{name}' skipped after {fails} failed boots — flash or start it to re-enable"),
            );
            return;
        }
        // Count this attempt *before* launching; a device reboot loop keeps
        // incrementing until the guard trips. A human command resets it.
        let _ = self.config_set("autostart_fails", &(fails + 1).to_string());
        match self.launch_app(&name) {
            Ok(()) => self.state.logger.info("boot", format!("autostart '{name}' running")),
            Err(e) => self.state.logger.warn("boot", format!("autostart '{name}' failed: {e}")),
        }
    }

    fn app_names(&self) -> Result<Vec<String>, String> {
        let entries = self.state.fs.lock().unwrap().list("/apps").map_err(|e| e.to_string())?;
        Ok(entries
            .into_iter()
            .filter(|e| !e.is_dir)
            .filter_map(|e| e.name.strip_suffix(".rnsb").map(String::from))
            .collect())
    }

    fn ensure_parent_dirs(&self, path: &str) {
        if let Some(idx) = path.rfind('/') {
            let dir = &path[..idx];
            if !dir.is_empty() {
                let _ = self.state.fs.lock().unwrap().mkdir(dir);
            }
        }
    }

    // Encrypted key-value config: stored as AES-CTR encrypted lines.
    fn config_load(&self) -> Vec<(String, String)> {
        let Ok(mut data) = self.state.fs.lock().unwrap().read("/config.bin") else {
            return Vec::new();
        };
        let nonce = rustnet_crypto::sha256(b"rustnet-config-nonce");
        let nonce16: [u8; 16] = nonce[..16].try_into().unwrap();
        if rustnet_crypto::aes_ctr_apply(&self.config_key, &nonce16, &mut data).is_err() {
            return Vec::new();
        }
        String::from_utf8_lossy(&data)
            .lines()
            .filter_map(|l| l.split_once('=').map(|(k, v)| (k.to_string(), v.to_string())))
            .collect()
    }

    fn config_store(&self, entries: &[(String, String)]) -> Result<(), String> {
        let text: String = entries.iter().map(|(k, v)| format!("{k}={v}\n")).collect();
        let mut data = text.into_bytes();
        let nonce = rustnet_crypto::sha256(b"rustnet-config-nonce");
        let nonce16: [u8; 16] = nonce[..16].try_into().unwrap();
        rustnet_crypto::aes_ctr_apply(&self.config_key, &nonce16, &mut data)
            .map_err(|e| e.to_string())?;
        self.state.fs.lock().unwrap().write("/config.bin", &data).map_err(|e| e.to_string())
    }

    fn config_set(&self, key: &str, value: &str) -> Result<(), String> {
        if key.contains('\n') || key.contains('=') {
            return Err("invalid config key".into());
        }
        let mut entries = self.config_load();
        entries.retain(|(k, _)| k != key);
        entries.push((key.to_string(), value.to_string()));
        self.config_store(&entries)
    }

    fn config_get(&self, key: &str) -> Result<Option<String>, String> {
        Ok(self.config_load().into_iter().find(|(k, _)| k == key).map(|(_, v)| v))
    }
}

fn validate_name(name: &str) -> Result<(), String> {
    if name.is_empty() || name.len() > 64 {
        return Err("invalid app name length".into());
    }
    if !name.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_' || c == '.') {
        return Err("app name may contain only [A-Za-z0-9._-]".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use rustnet_core::{Builder, MFLAG_INTERNAL, MFLAG_STATIC};
    use rustnet_fs::MemFs;
    use rustnet_hal_host::HostBoard;
    use rustnet_secureboot::{seal, ChipFamily};

    fn make_service() -> DeviceService {
        let state = SharedState::new(Box::new(HostBoard::new()), Box::new(MemFs::new()));
        DeviceService::new(state)
    }

    /// Build a tiny app: logs via Console, writes GPIO 13 high, writes a file.
    fn demo_rnx() -> Vec<u8> {
        let mut b = Builder::new();
        const SI: u8 = MFLAG_STATIC | MFLAG_INTERNAL;
        const S: u8 = MFLAG_STATIC;
        let wl = b.add_method("System.Console::WriteLine(string)", None, SI, 1, 0, vec![]);
        let gpio = b.add_method("RustNet.Hal.Gpio::SetMode(i4,i4)", None, SI, 2, 0, vec![]);
        let gpio_w = b.add_method("RustNet.Hal.Gpio::Write(i4,bool)", None, SI, 2, 0, vec![]);
        let fs_w = b.add_method(
            "RustNet.IO.FileSystem::WriteAllText(string,string)",
            None,
            SI,
            2,
            0,
            vec![],
        );
        let hello = b.string("hello from managed app");
        let path = b.string("/data/out.txt");
        let content = b.string("written by app");
        let mut code = Vec::new();
        code.push(0x72);
        code.extend_from_slice(&hello.to_le_bytes());
        code.push(0x28);
        code.extend_from_slice(&wl.to_le_bytes());
        // Gpio.SetMode(13, Output=3); Gpio.Write(13, true)
        code.extend_from_slice(&[0x1F, 13, 0x19]);
        code.push(0x28);
        code.extend_from_slice(&gpio.to_le_bytes());
        code.extend_from_slice(&[0x1F, 13, 0x17]);
        code.push(0x28);
        code.extend_from_slice(&gpio_w.to_le_bytes());
        // FileSystem.WriteAllText(path, content)
        code.push(0x72);
        code.extend_from_slice(&path.to_le_bytes());
        code.push(0x72);
        code.extend_from_slice(&content.to_le_bytes());
        code.push(0x28);
        code.extend_from_slice(&fs_w.to_le_bytes());
        code.push(0x2A);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_entry(main);
        b.build().to_bytes()
    }

    struct Keys {
        priv_der: Vec<u8>,
        pub_der: Vec<u8>,
    }

    fn generate_keys() -> &'static Keys {
        use std::sync::OnceLock;
        static KEYS: OnceLock<Keys> = OnceLock::new();
        KEYS.get_or_init(|| {
            let key = rsa::RsaPrivateKey::new(&mut rand::thread_rng(), 2048).unwrap();
            use rsa::pkcs1::{EncodeRsaPrivateKey, EncodeRsaPublicKey};
            Keys {
                priv_der: key.to_pkcs1_der().unwrap().as_bytes().to_vec(),
                pub_der: key.to_public_key().to_pkcs1_der().unwrap().as_bytes().to_vec(),
            }
        })
    }

    fn provision(svc: &mut DeviceService) -> &'static Keys {
        let keys = generate_keys();
        let resp = svc.handle(&Frame::new(CMD_PROVISION_KEY, keys.pub_der.clone()));
        assert_eq!(resp.code, ST_OK, "{}", String::from_utf8_lossy(&resp.payload));
        keys
    }

    fn flash_demo(svc: &mut DeviceService, keys: &Keys, name: &str) {
        let sealed =
            seal(ImageKind::App, ChipFamily::HostSim, &demo_rnx(), &keys.priv_der).unwrap();
        let mut payload = vec![name.len() as u8];
        payload.extend_from_slice(name.as_bytes());
        payload.extend_from_slice(&sealed);
        let resp = svc.handle(&Frame::new(CMD_FLASH_APP, payload));
        assert_eq!(resp.code, ST_OK, "{}", String::from_utf8_lossy(&resp.payload));
    }

    #[test]
    fn ping_and_info() {
        let mut svc = make_service();
        let resp = svc.handle(&Frame::new(CMD_PING, vec![]));
        assert_eq!(resp.code, ST_OK);
        assert_eq!(resp.payload, vec![PROTOCOL_VERSION]);
        let info = svc.handle(&Frame::new(CMD_INFO, vec![]));
        let text = String::from_utf8_lossy(&info.payload).to_string();
        assert!(text.contains("\"chip\":\"host-sim\""), "{text}");
    }

    #[test]
    fn flash_requires_provisioned_key_and_valid_signature() {
        let mut svc = make_service();
        // Unprovisioned flash fails.
        let sealed_garbage = vec![1, 4, b'a', b'p', b'p', b'x'];
        let resp = svc.handle(&Frame::new(CMD_FLASH_APP, sealed_garbage));
        assert_eq!(resp.code, ST_ERR);

        let keys = provision(&mut svc);
        // Tampered container rejected.
        let mut sealed =
            seal(ImageKind::App, ChipFamily::HostSim, &demo_rnx(), &keys.priv_der).unwrap();
        let n = sealed.len();
        sealed[n / 2] ^= 0xAA;
        let mut payload = vec![4u8];
        payload.extend_from_slice(b"evil");
        payload.extend_from_slice(&sealed);
        let resp = svc.handle(&Frame::new(CMD_FLASH_APP, payload));
        assert_eq!(resp.code, ST_ERR);
        let msg = String::from_utf8_lossy(&resp.payload).to_string();
        assert!(msg.contains("signature"), "{msg}");
    }

    #[test]
    fn autostart_resumes_app_after_reboot() {
        let mut svc = make_service();
        let keys = provision(&mut svc);
        flash_demo(&mut svc, keys, "demo");

        // Starting an app marks it as the autostart app.
        assert_eq!(svc.handle(&Frame::new(CMD_START_APP, b"demo".to_vec())).code, ST_OK);
        assert_eq!(svc.config_get("autostart").unwrap().as_deref(), Some("demo"));
        svc.stop_app();

        // Simulate a reboot: a fresh service sharing the same persisted storage.
        let fs = svc.state.fs.clone();
        let mut state2 = SharedState::new(Box::new(HostBoard::new()), Box::new(MemFs::new()));
        state2.fs = fs;
        let mut svc2 = DeviceService::new(state2);
        assert!(svc2.active_app.is_none(), "fresh service has no active app yet");

        svc2.try_autostart();
        assert_eq!(svc2.active_app.as_deref(), Some("demo"), "autostart launched the app");
        assert!(svc2.runner.is_some());
    }

    #[test]
    fn autostart_guard_trips_after_repeated_boots() {
        let mut svc = make_service();
        let keys = provision(&mut svc);
        flash_demo(&mut svc, keys, "demo");
        svc.config_set("autostart", "demo").unwrap();

        // Each unattended boot increments the fail counter; after the limit,
        // autostart is skipped so a crash-looping app cannot brick the device.
        for _ in 0..DeviceService::MAX_AUTOSTART_FAILS {
            svc.stop_app();
            svc.try_autostart();
        }
        assert_eq!(svc.autostart_fails(), DeviceService::MAX_AUTOSTART_FAILS);
        svc.stop_app();
        svc.active_app = None;
        svc.try_autostart();
        assert!(svc.active_app.is_none(), "autostart skipped once the guard trips");

        // An explicit start clears the guard.
        assert_eq!(svc.handle(&Frame::new(CMD_START_APP, b"demo".to_vec())).code, ST_OK);
        assert_eq!(svc.autostart_fails(), 0);
    }

    #[test]
    fn flash_list_start_logs_erase_lifecycle() {
        let mut svc = make_service();
        let keys = provision(&mut svc);
        flash_demo(&mut svc, keys, "blinky");

        let list = svc.handle(&Frame::new(CMD_LIST_APPS, vec![]));
        let text = String::from_utf8_lossy(&list.payload).to_string();
        assert!(text.contains(r#""name":"blinky""#), "{text}");
        assert!(text.contains(r#""active":true"#), "{text}");

        let resp = svc.handle(&Frame::new(CMD_START_APP, b"blinky".to_vec()));
        assert_eq!(resp.code, ST_OK, "{}", String::from_utf8_lossy(&resp.payload));
        // Wait for the app thread to finish.
        for _ in 0..100 {
            if !svc.runner.as_ref().unwrap().is_running() {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        let logs = svc.handle(&Frame::new(CMD_GET_LOGS, vec![]));
        let logs = String::from_utf8_lossy(&logs.payload).to_string();
        assert!(logs.contains("hello from managed app"), "{logs}");
        assert!(logs.contains("exited"), "{logs}");

        // The app really wrote through to the device filesystem.
        let read = svc.handle(&Frame::new(CMD_READ_DATA, b"out.txt".to_vec()));
        assert_eq!(read.code, ST_OK);
        assert_eq!(read.payload, b"written by app");

        // Perf counters ticked.
        let perf = svc.handle(&Frame::new(CMD_GET_PERF, vec![]));
        let perf = String::from_utf8_lossy(&perf.payload).to_string();
        assert!(!perf.contains(r#""il_instructions":0,"#), "{perf}");

        let resp = svc.handle(&Frame::new(CMD_ERASE_APP, b"blinky".to_vec()));
        assert_eq!(resp.code, ST_OK);
        let list = svc.handle(&Frame::new(CMD_LIST_APPS, vec![]));
        assert_eq!(String::from_utf8_lossy(&list.payload), "[]");
    }

    #[test]
    fn config_is_encrypted_at_rest() {
        let mut svc = make_service();
        let resp = svc.handle(&Frame::new(CMD_SET_CONFIG, b"api.key\nsupersecret123".to_vec()));
        assert_eq!(resp.code, ST_OK);
        let got = svc.handle(&Frame::new(CMD_GET_CONFIG, b"api.key".to_vec()));
        assert_eq!(got.payload, b"supersecret123");
        // Raw storage must not contain the plaintext.
        let raw = svc.state.fs.lock().unwrap().read("/config.bin").unwrap();
        let raw_str = String::from_utf8_lossy(&raw).to_string();
        assert!(!raw_str.contains("supersecret123"));
        // Update overwrites.
        svc.handle(&Frame::new(CMD_SET_CONFIG, b"api.key\nrotated".to_vec()));
        let got = svc.handle(&Frame::new(CMD_GET_CONFIG, b"api.key".to_vec()));
        assert_eq!(got.payload, b"rotated");
    }

    #[test]
    fn wifi_config_stored() {
        let mut svc = make_service();
        let resp = svc.handle(&Frame::new(CMD_WIFI_CONFIG, b"MyNetwork\npassword1".to_vec()));
        assert_eq!(resp.code, ST_OK);
        let ssid = svc.handle(&Frame::new(CMD_GET_CONFIG, b"wifi.ssid".to_vec()));
        assert_eq!(ssid.payload, b"MyNetwork");
    }

    #[test]
    fn boot_image_roundtrip() {
        let mut svc = make_service();
        let (w, h) = (4u16, 2u16);
        let mut payload = Vec::new();
        payload.extend_from_slice(&w.to_le_bytes());
        payload.extend_from_slice(&h.to_le_bytes());
        payload.extend(std::iter::repeat(0xF8).take((w * h * 2) as usize));
        let resp = svc.handle(&Frame::new(CMD_SET_BOOT_IMAGE, payload.clone()));
        assert_eq!(resp.code, ST_OK, "{}", String::from_utf8_lossy(&resp.payload));
        let got = svc.handle(&Frame::new(CMD_GET_BOOT_IMAGE, vec![]));
        assert_eq!(got.payload, payload);
        // Wrong size rejected.
        let resp = svc.handle(&Frame::new(CMD_SET_BOOT_IMAGE, vec![4, 0, 2, 0, 1, 2, 3]));
        assert_eq!(resp.code, ST_ERR);
    }

    #[test]
    fn ota_full_flow() {
        let mut svc = make_service();
        let keys = provision(&mut svc);
        let firmware =
            seal(ImageKind::Firmware, ChipFamily::HostSim, b"fw v2 bytes", &keys.priv_der).unwrap();
        assert_eq!(svc.handle(&Frame::new(CMD_OTA_BEGIN, vec![])).code, ST_OK);
        for chunk in firmware.chunks(16) {
            assert_eq!(svc.handle(&Frame::new(CMD_OTA_DATA, chunk.to_vec())).code, ST_OK);
        }
        let end = svc.handle(&Frame::new(CMD_OTA_END, vec![]));
        assert_eq!(end.code, ST_OK, "{}", String::from_utf8_lossy(&end.payload));
        assert_eq!(end.payload, b"B");
        let confirm = svc.handle(&Frame::new(CMD_OTA_CONFIRM, vec![]));
        assert_eq!(confirm.payload, b"B");
    }

    #[test]
    fn unknown_command_errors() {
        let mut svc = make_service();
        let resp = svc.handle(&Frame::new(0xEE, vec![]));
        assert_eq!(resp.code, ST_ERR);
    }
}
