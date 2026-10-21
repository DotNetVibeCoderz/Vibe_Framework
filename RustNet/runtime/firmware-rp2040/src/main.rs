//! RustNet on the Raspberry Pi RP2040 — bare metal, Cortex-M0+.
//!
//! Fourth bare-metal port in this repository, after the STM32F4, the K210 and
//! the ESP32, and the smallest of them: 264 KB of SRAM and no external RAM at
//! all. The IL interpreter runs on-chip, in a fraction of the heap the K210
//! port gives it.
//!
//! ## What is different about this chip
//!
//! **Everything starts held in reset.** There is no clock-enable register to
//! forget; there is a `RESETS` block containing every peripheral, and a write
//! to a held peripheral is discarded with no fault and no flag. Configure a
//! UART before releasing it and it reads back as zeros.
//!
//! **It boots on a ring oscillator, not the crystal.** Roughly 6 MHz, and
//! "roughly" because it is an on-die RC. Nothing that depends on a known
//! frequency is true until [`rustnet_hal_rp2040::clocks::init`] has run.
//!
//! **The timer needs the watchdog.** The microsecond counter is fed by a tick
//! the watchdog block generates, and that tick is off at reset. Without it
//! every delay returns instantly and every timeout fires at once — which looks
//! like a hung peripheral somewhere else entirely.
//!
//! **There is no boot ROM to fall back on.** The chip loads 256 bytes from
//! flash offset 0, checks a CRC, and runs them; that second stage sets up
//! execute-in-place. If it is missing or wrong the board simply re-enumerates
//! as BOOTSEL, which is at least a recoverable failure — the reason a Pico is
//! nearly impossible to brick.
//!
//! ## Console
//!
//! The Pico's own USB, as a CDC-ACM port carrying the log and RNDP; UART0 on
//! GP0/GP1 at 115200 mirrors the log for anyone with an adapter.
//!
//! ## What C# can reach
//!
//! `Gpio`, `Adc` (GP26–29, 4 = temperature), `Pwm` (channel = GPIO), `I2c`
//! (0 = GP4/5, 1 = GP6/7), `Spi` (0 = GP16/18/19, 1 = GP10/11/12), `Uart`
//! (port 1 = GP8/9; port 0 is the console), `Signal` (any GPIO, via PIO) and
//! `RustNet.IO.FileSystem` over the flashfs window, with `/.sys` kept from
//! applications. The pin table and limits are in the port's README.

#![no_std]
#![no_main]

extern crate alloc;

use core::fmt::Write as _;
use core::mem::MaybeUninit;

// Linked for its `critical-section` implementation, which the allocator needs
// and nothing here calls directly — without the reference the crate is dropped
// and the section symbols come out undefined at link time.
use cortex_m as _;
use cortex_m_rt::entry;
use embedded_alloc::LlffHeap;
use alloc::string::String;
use alloc::vec::Vec;
use rustnet_core::{HostValue, Interpreter, Module, RunExit, RuntimeHost};
use rustnet_hal::gpio::{Level, PinMode};
use rustnet_hal::spi::SpiMode;
use rustnet_hal::uart::UartConfig;
use rustnet_hal::Board as _;
use rp2040_hal::usb::UsbBus;
use rp2040_hal::Clock as _;
use rustnet_hal_rp2040::{clocks, Rp2040Board};
use usb_device::class_prelude::UsbBusAllocator;

mod crash;
mod npc;
mod ota;
mod rndp;
mod storage;
mod usb;

/// The USB bus allocator, which the device borrows for its whole life.
static mut USB_ALLOCATOR: Option<UsbBusAllocator<UsbBus>> = None;

/// Board facts for a Raspberry Pi Pico.
mod board {
    pub const NAME: &str = "Raspberry Pi Pico";

    /// The on-board LED. GP25 on a Pico; a Pico W drives its LED through the
    /// wireless chip instead, so this port shows nothing on one.
    pub const USER_LED: u32 = 25;

    /// UART0 on GP0 (TX) and GP1 (RX), the default pins in every RP2040
    /// pinout diagram. `FUNC_UART` is 2 in IO_BANK0's function list.
    pub const CONSOLE_TX: u32 = 0;
    pub const CONSOLE_RX: u32 = 1;
    pub const FUNC_UART: u32 = 2;
    pub const CONSOLE: u8 = 0;

    /// 125 MHz: the rate every RP2040 board is specified at, and the one the
    /// PLL hits exactly from a 12 MHz crystal.
    pub const SYS_HZ: u32 = 125_000_000;

    /// Heap for the interpreter.
    ///
    /// 128 KB of the chip's 264 KB. Half the SRAM sounds generous until you
    /// notice what it is for: an RNX module, its string table, and every
    /// object a C# program allocates. The rest is stack, statics, and room to
    /// grow — this part has no external RAM to fall back on, so running out
    /// here is a hard stop rather than a slowdown.
    pub const HEAP_SIZE: usize = 128 * 1024;

    /// What `rustnet info` calls the embedded application. There is no
    /// storage on this port yet, so the demo is linked into the image rather
    /// than flashed — and reporting a name for it is more honest than
    /// reporting none, because something *is* running.
    pub const APP_NAME: &str = "blink (embedded)";
}

/// The demo, compiled from C# by the MetadataProcessor.
///
/// Deliberately the *same file* the STM32 and K210 ports carry rather than a
/// copy: three ports running one demo is the point of having a demo, and a
/// duplicated binary is a duplicate that can drift.
static APP_RNX: &[u8] = include_bytes!("../../firmware-stm32/demo/Blink.rnx");

#[global_allocator]
static HEAP: LlffHeap = LlffHeap::empty();

pub(crate) fn heap_used() -> usize {
    HEAP.used()
}

pub(crate) fn heap_free() -> usize {
    HEAP.free()
}

/// The host the interpreter calls into.
/// How many console lines to keep for `rustnet logs`. Small: this port has
/// 264 KB of SRAM and the interpreter's heap is half of it.
const LOG_LINES: usize = 48;

/// Named rather than written as escapes, so no editing pass can turn them
/// into the literal bytes they stand for.
const LF: char = '\n';
const CR: char = '\r';

struct FirmwareHost {
    board: Rp2040Board,
    /// Completed console lines, oldest first, for `rustnet logs`.
    logs: alloc::vec::Vec<alloc::string::String>,
    /// The line being assembled; the interpreter writes in fragments.
    partial: alloc::string::String,
    /// A megabyte of the QSPI flash above the image, holding the filesystem.
    flash: storage::QspiFlash,
    /// The RNDP service lives here so `sleep_ms` can keep serving it.
    ///
    /// Fuel is counted in instructions, but an application spends its
    /// wall-clock time asleep — the blink demo is asleep between every edge —
    /// so a service polled only between fuel slices is a service polled once
    /// per sleep. The endpoint fills with the first frame and the tool's next
    /// write blocks forever.
    rndp: rndp::Rndp,
    /// The board's own USB, so it needs no serial adapter. `None` when the
    /// clock tree did not come up — there is no bus to build one on. Console
    /// output goes to both channels: the UART for anyone with an adapter, USB
    /// for everyone else.
    usb: Option<usb::UsbCdc>,
    /// Native modules the running app loaded (docs/npc.md); replaced, and
    /// their memory freed, whenever another app starts.
    npc: npc::NpcTable,
}

impl core::fmt::Write for FirmwareHost {
    fn write_str(&mut self, s: &str) -> core::fmt::Result {
        if let Ok(uart) = self.board.uart(board::CONSOLE) {
            let _ = uart.write(s.as_bytes());
        }
        if let Some(usb) = self.usb.as_mut() {
            usb.write(s.as_bytes());
        }
        // Kept for `rustnet logs` as well as sent. The banner is printed long
        // before anyone can open the port, so a device that only streams has
        // nothing to say to a tool that connects later.
        for ch in s.chars() {
            if ch == LF {
                let line = core::mem::take(&mut self.partial);
                if self.logs.len() == LOG_LINES {
                    self.logs.remove(0);
                }
                self.logs.push(line);
            } else if ch != CR {
                self.partial.push(ch);
            }
        }
        Ok(())
    }
}

impl FirmwareHost {
    fn board_uptime_ms(&mut self) -> u64 {
        self.board.delay().now_us() / 1000
    }

    fn cpu_hz(&mut self) -> u32 {
        self.board.clocks().sys_hz
    }

    /// Answer any pending RNDP traffic.
    ///
    /// Takes the service out of `self` for the call so it can borrow the rest
    /// of the host; the move is a couple of pointers, not an allocation. The
    /// same shape the K210 port uses, for the same reason.
    pub(crate) fn poll_rndp(&mut self) {
        let mut service = core::mem::take(&mut self.rndp);
        service.poll(self);
        let reboot = service.reboot_requested;
        let to_bootloader = service.reboot_to_bootloader;
        self.rndp = service;
        if reboot {
            // The reply is queued, not sent: USB only moves it when the host
            // polls the endpoint, so keep servicing the bus for a moment.
            // Resetting at once dropped the OTA_END reply on a Pico and the
            // tool reported a failed update that had in fact succeeded.
            let start = self.board.delay().now_us();
            while self.board.delay().now_us().wrapping_sub(start) < 100_000 {
                if let Some(usb) = self.usb.as_mut() {
                    usb.poll();
                }
            }
            if to_bootloader {
                // Never returns: the ROM takes over and re-enumerates as the
                // mass-storage device that accepts a UF2.
                rp2040_hal::rom_data::reset_to_usb_boot(0, 0);
            }
            cortex_m::peripheral::SCB::sys_reset();
        }
    }

    /// The last `max` log lines, newest last.
    fn tail_logs(&self, max: usize) -> alloc::string::String {
        let start = self.logs.len().saturating_sub(max);
        self.logs[start..].join("
")
    }
}

impl RuntimeHost for FirmwareHost {
    fn console_write(&mut self, text: &str) {
        let _ = self.write_str(text);
    }

    fn now_ms(&mut self) -> u64 {
        self.board.delay().now_us() / 1000
    }

    fn sleep_ms(&mut self, ms: u64) {
        // Serviced, not slept through.
        //
        // An application spends nearly all its wall-clock time here — the
        // blink demo is asleep between every edge — and USB is not a stream
        // that waits. Opening a COM port sends control requests that have to
        // be answered within a timeout, and a device that is inside a plain
        // delay answers none of them: Windows fails the open with "the
        // semaphore timeout period has expired", which reads as a driver
        // problem rather than as a firmware that is busy waiting.
        //
        // This is the same fault that stopped enumeration earlier, in a
        // different place. Every wait on this port has to keep the bus alive.
        for _ in 0..ms {
            if let Some(usb) = self.usb.as_mut() {
                usb.poll();
            }
            // And the protocol, not just the bus. Servicing USB alone keeps
            // the port enumerated while leaving every frame unread, so the
            // endpoint fills and the tool's next write blocks.
            self.poll_rndp();
            self.board.delay().delay_ms(1);
        }
    }

    /// The managed API, by canonical name. The names match
    /// `runtime/firmware/src/apphost.rs` character for character: the same
    /// compiled module runs against every host, and the interpreter
    /// dispatches on the string.
    fn invoke(&mut self, name: &str, args: alloc::vec::Vec<HostValue>) -> Result<HostValue, alloc::string::String> {
        // Files take their arguments by value: a byte array is already one copy
        // in the managed heap, and a second made just to read the first is not
        // free on a 128 KB heap.
        if name.starts_with("RustNet.IO.FileSystem::") {
            return self.file_call(name, args);
        }

        // Widened exactly as apphost.rs does. Numeric arguments arrive in
        // whatever form the evaluation stack held, and a C# `bool` arrives as
        // an int — matching only `HostValue::Bool` here is how `Gpio.Write(pin,
        // true)` once silently wrote low.
        let int = |n: usize| arg_int(&args, n, name);
        let flag = |n: usize| int(n).map(|v| v != 0);
        let hal = |e: rustnet_hal::HalError| alloc::format!("{name}: {e}");

        match name {
            // The board knows where its LED is, so one compiled module runs on
            // every board rather than one module per LED position.
            n if n.ends_with("Board::UserLed()") => Ok(HostValue::I32(board::USER_LED as i32)),

            // --- GPIO -----------------------------------------------------
            "RustNet.Hal.Gpio::SetMode(i4,i4)" => {
                let pin = int(0)? as u32;
                let mode = match int(1)? {
                    0 => PinMode::Input,
                    1 => PinMode::InputPullUp,
                    2 => PinMode::InputPullDown,
                    3 => PinMode::Output,
                    _ => PinMode::OutputOpenDrain,
                };
                self.board.gpio(pin).and_then(|p| p.set_mode(mode)).map_err(hal)?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Gpio::Write(i4,bool)" => {
                let pin = int(0)? as u32;
                let level = if flag(1)? { Level::High } else { Level::Low };
                self.board.gpio(pin).and_then(|p| p.write(level)).map_err(hal)?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Gpio::Read(i4)" => {
                let pin = int(0)? as u32;
                let level = self.board.gpio(pin).and_then(|p| p.read()).map_err(hal)?;
                Ok(HostValue::Bool(level == Level::High))
            }
            "RustNet.Hal.Gpio::Toggle(i4)" => {
                let pin = int(0)? as u32;
                self.board.gpio(pin).and_then(|p| p.toggle()).map_err(hal)?;
                Ok(HostValue::Void)
            }

            // --- ADC: 0..3 = GP26..GP29, 4 = temperature sensor ------------
            "RustNet.Hal.Adc::ReadRaw(i4)" => {
                let ch = channel(int(0)?, name)?;
                let raw = self.board.adc(ch).and_then(|a| a.read_raw()).map_err(hal)?;
                Ok(HostValue::I32(raw as i32))
            }
            "RustNet.Hal.Adc::ReadMillivolts(i4)" => {
                let ch = channel(int(0)?, name)?;
                let mv = self.board.adc(ch).and_then(|a| a.read_millivolts()).map_err(hal)?;
                Ok(HostValue::I32(mv as i32))
            }

            // --- PWM: the channel is the GPIO number ----------------------
            "RustNet.Hal.Pwm::Configure(i4,i4,i4)" => {
                let ch = channel(int(0)?, name)?;
                let hz = int(1)?;
                let duty = int(2)?;
                if hz <= 0 {
                    return Err(alloc::format!("{name}: frequency must be positive, got {hz}"));
                }
                if !(0..=10_000).contains(&duty) {
                    return Err(alloc::format!("{name}: duty is 0..=10000, got {duty}"));
                }
                let p = self.board.pwm(ch).map_err(hal)?;
                p.set_frequency(hz as u32)
                    .and_then(|_| p.set_duty(duty as u16))
                    .and_then(|_| p.enable())
                    .map_err(hal)?;
                Ok(HostValue::Void)
            }

            // --- I2C: 0 = GP4/GP5, 1 = GP6/GP7 ----------------------------
            "RustNet.Hal.I2c::Write(i4,i4,u1[])" => {
                let bus = channel(int(0)?, name)?;
                let addr = address(int(1)?, name)?;
                let data = arg_bytes(&args, 2, name)?;
                self.board.i2c(bus).and_then(|b| b.write(addr, &data)).map_err(hal)?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.I2c::Read(i4,i4,i4)" => {
                let bus = channel(int(0)?, name)?;
                let addr = address(int(1)?, name)?;
                let mut buf = buffer(int(2)?, name)?;
                self.board.i2c(bus).and_then(|b| b.read(addr, &mut buf)).map_err(hal)?;
                Ok(HostValue::Bytes(buf))
            }

            // --- SPI: 0 = GP16/18/19, 1 = GP10/11/12; CS by Gpio ----------
            "RustNet.Hal.Spi::Configure(i4,i4,i4)" => {
                let bus = channel(int(0)?, name)?;
                let hz = int(1)?.max(1) as u32;
                let mode = match int(2)? {
                    0 => SpiMode::Mode0,
                    1 => SpiMode::Mode1,
                    2 => SpiMode::Mode2,
                    3 => SpiMode::Mode3,
                    m => return Err(alloc::format!("{name}: SPI mode must be 0..3, got {m}")),
                };
                self.board.spi(bus).and_then(|b| b.configure(hz, mode)).map_err(hal)?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Spi::Transfer(i4,u1[])" | "RustNet.Hal.Spi::Write(i4,u1[])" => {
                let bus = channel(int(0)?, name)?;
                let data = arg_bytes(&args, 1, name)?;
                let mut rx = buffer(data.len() as i32, name)?;
                self.board.spi(bus).and_then(|b| b.transfer(&data, &mut rx)).map_err(hal)?;
                if name.ends_with("::Transfer(i4,u1[])") {
                    Ok(HostValue::Bytes(rx))
                } else {
                    Ok(HostValue::Void)
                }
            }
            "RustNet.Hal.Spi::Read(i4,i4)" => {
                let bus = channel(int(0)?, name)?;
                let mut rx = buffer(int(1)?, name)?;
                self.board.spi(bus).and_then(|b| b.read(&mut rx)).map_err(hal)?;
                Ok(HostValue::Bytes(rx))
            }

            // --- UART: 1 = GP8 TX / GP9 RX --------------------------------
            "RustNet.Hal.Uart::Configure(i4,i4)"
            | "RustNet.Hal.Uart::Write(i4,u1[])"
            | "RustNet.Hal.Uart::Read(i4,i4)"
            | "RustNet.Hal.Uart::Available(i4)" => {
                let port = channel(int(0)?, name)?;
                if port == board::CONSOLE {
                    // Reconfiguring the console's UART from an app would cut
                    // the log off mid-line for anyone with an adapter on it.
                    return Err(alloc::format!(
                        "{name}: UART0 is the console on this board; use port 1 (GP8 TX, GP9 RX)"
                    ));
                }
                let uart = self.board.uart(port).map_err(hal)?;
                match name {
                    "RustNet.Hal.Uart::Configure(i4,i4)" => {
                        let baud = int(1)?.max(0) as u32;
                        uart.configure(UartConfig { baud, ..UartConfig::default() }).map_err(hal)?;
                        Ok(HostValue::Void)
                    }
                    "RustNet.Hal.Uart::Write(i4,u1[])" => {
                        let data = arg_bytes(&args, 1, name)?;
                        uart.write(&data).map_err(hal)?;
                        Ok(HostValue::Void)
                    }
                    "RustNet.Hal.Uart::Read(i4,i4)" => {
                        let mut buf = buffer(int(1)?, name)?;
                        let n = uart.read(&mut buf).map_err(hal)?;
                        buf.truncate(n);
                        Ok(HostValue::Bytes(buf))
                    }
                    _ => Ok(HostValue::I32(uart.bytes_available().map_err(hal)? as i32)),
                }
            }

            // --- Signal: any GPIO, through PIO0; timings as u32 LE --------
            "RustNet.Hal.Signal::GenerateRaw(i4,bool,u1[])" => {
                let pin = int(0)?.max(0) as u32;
                let initial = flag(1)?;
                let bytes = arg_bytes(&args, 2, name)?;
                let timings: Vec<u32> = bytes
                    .chunks_exact(4)
                    .map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]]))
                    .collect();
                self.board.signal(pin).and_then(|s| s.generate(initial, &timings)).map_err(hal)?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Signal::CaptureRaw(i4,i4,i4)" => {
                let pin = int(0)?.max(0) as u32;
                let max_edges = int(1)?.max(0) as usize;
                let timeout = int(2)?.max(0) as u32;
                let widths = self
                    .board
                    .signal(pin)
                    .and_then(|s| s.capture(max_edges, timeout))
                    .map_err(hal)?;
                let mut out = Vec::with_capacity(widths.len() * 4);
                for w in widths {
                    out.extend_from_slice(&w.to_le_bytes());
                }
                Ok(HostValue::Bytes(out))
            }
            "RustNet.Hal.Signal::PulseFeedback(i4,bool,i4,i4)" => {
                let pin = int(0)?.max(0) as u32;
                let high = flag(1)?;
                let pulse = int(2)?.max(0) as u32;
                let timeout = int(3)?.max(0) as u32;
                let echo = self
                    .board
                    .signal(pin)
                    .and_then(|s| s.pulse_feedback(high, pulse, timeout))
                    .map_err(hal)?;
                Ok(HostValue::I32(echo as i32))
            }

            "RustNet.Sys.Uptime::Ms()" => {
                Ok(HostValue::I64((self.board.delay().now_us() / 1000) as i64))
            }

            // --- NPC: native procedures loaded at run time (docs/npc.md) ---
            "RustNet.Native.Npc::IsSupported()" => Ok(HostValue::Bool(true)),
            "RustNet.Native.Npc::Load(string,string)" => {
                let text = |n: usize| match args.get(n) {
                    Some(HostValue::Str(s)) => Ok(s.clone()),
                    _ => Err(alloc::format!("{name}: argument {n} is not a string")),
                };
                let module = text(0)?;
                let symbol = text(1)?;
                if module.is_empty() || module.contains(['/', '\\']) || module.contains("..") {
                    return Err(alloc::format!("invalid native module name '{module}'"));
                }
                let key = self.rndp.pub_key.clone().ok_or_else(|| String::from("device not provisioned"))?;
                let container = rustnet_flashfs::read(&mut self.flash, &alloc::format!("native/{module}.rnsb"))
                    .map_err(|_| alloc::format!("native module '{module}' not found (rustnet native push)"))?;
                Ok(HostValue::I32(self.npc.load(&module, &container, &key, &symbol)?))
            }
            "RustNet.Native.Npc::Invoke(i4,u1[],i4)" => {
                let handle = int(0)?;
                let bytes = match args.get(1) {
                    Some(HostValue::Bytes(b)) => b.as_slice(),
                    _ => &[][..],
                };
                let cap = int(2)?.max(0) as usize;
                Ok(HostValue::Bytes(self.npc.invoke(handle, bytes, cap)?))
            }
            other => Err(alloc::format!("{other} is not available on this board")),
        }
    }
}

impl FirmwareHost {
    /// `RustNet.IO.FileSystem::*`, over the flashfs window, with `/.sys` kept
    /// out of the application's reach — see `storage.rs`.
    fn file_call(&mut self, name: &str, args: Vec<HostValue>) -> Result<HostValue, String> {
        let mut args = args.into_iter();
        let mut text = |n: usize| match args.next() {
            Some(HostValue::Str(s)) => Ok(s),
            other => Err(alloc::format!("{name}: argument {n} is not a string: {other:?}")),
        };
        let path = text(0)?;
        let flash = &mut self.flash;

        match name {
            "RustNet.IO.FileSystem::WriteAllText(string,string)" => {
                storage::app_write(flash, &path, text(1)?.as_bytes(), false)?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::AppendText(string,string)" => {
                storage::app_write(flash, &path, text(1)?.as_bytes(), true)?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::ReadAllText(string)" => {
                let bytes = storage::app_read(flash, &path)?;
                let text = String::from_utf8(bytes)
                    .map_err(|_| alloc::format!("{path} is not UTF-8; read it with ReadAllBytes"))?;
                Ok(HostValue::Str(text))
            }
            "RustNet.IO.FileSystem::WriteAllBytes(string,u1[])"
            | "RustNet.IO.FileSystem::AppendBytes(string,u1[])" => {
                let bytes = match args.next() {
                    Some(HostValue::Bytes(b)) => b,
                    Some(HostValue::Null) => Vec::new(),
                    other => {
                        return Err(alloc::format!("{name}: argument 1 is not a byte array: {other:?}"))
                    }
                };
                let append = name.contains("::AppendBytes(");
                storage::app_write(flash, &path, &bytes, append)?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::ReadAllBytes(string)" => {
                Ok(HostValue::Bytes(storage::app_read(flash, &path)?))
            }
            "RustNet.IO.FileSystem::Exists(string)" => {
                Ok(HostValue::Bool(storage::app_exists(flash, &path)?))
            }
            "RustNet.IO.FileSystem::Delete(string)" => {
                storage::app_delete(flash, &path)?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::CreateDirectory(string)" => {
                storage::app_create_directory(flash, &path)?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::List(string)" => {
                Ok(HostValue::Str(storage::app_list(flash, &path)?.join("\n")))
            }
            other => Err(alloc::format!("{other} is not available on this board")),
        }
    }
}

/// An int argument by position, widened the way apphost.rs widens it.
fn arg_int(args: &[HostValue], n: usize, name: &str) -> Result<i32, String> {
    match args.get(n) {
        Some(HostValue::I32(v)) => Ok(*v),
        Some(HostValue::I64(v)) => Ok(*v as i32),
        Some(HostValue::F64(v)) => Ok(*v as i32),
        Some(HostValue::Bool(v)) => Ok(i32::from(*v)),
        other => Err(alloc::format!("{name}: argument {n} is not an int: {other:?}")),
    }
}

/// A byte-array argument by position; a C# `null` is an empty array.
fn arg_bytes(args: &[HostValue], n: usize, name: &str) -> Result<Vec<u8>, String> {
    match args.get(n) {
        Some(HostValue::Bytes(b)) => Ok(b.clone()),
        Some(HostValue::Null) => Ok(Vec::new()),
        other => Err(alloc::format!("{name}: argument {n} is not a byte array: {other:?}")),
    }
}

/// A bus, port or channel number: small and non-negative.
fn channel(v: i32, name: &str) -> Result<u8, String> {
    u8::try_from(v).map_err(|_| alloc::format!("{name}: {v} is not a bus or channel number"))
}

fn address(v: i32, name: &str) -> Result<u8, String> {
    if (0..=0x7F).contains(&v) {
        Ok(v as u8)
    } else {
        Err(alloc::format!("{name}: I2C address {v:#x} is not 7-bit"))
    }
}

/// Largest single transfer an app may ask for: a fraction of the 128 KB heap,
/// so a typo'd length is an error rather than an allocation failure.
const MAX_TRANSFER: i32 = 16 * 1024;

/// A zeroed receive buffer of `len` bytes, refused rather than allocated when
/// it is negative or unreasonable.
fn buffer(len: i32, name: &str) -> Result<Vec<u8>, String> {
    if !(0..=MAX_TRANSFER).contains(&len) {
        return Err(alloc::format!("{name}: length {len} is outside 0..={MAX_TRANSFER}"));
    }
    let mut buf = Vec::new();
    buf.try_reserve_exact(len as usize)
        .map_err(|_| alloc::format!("{name}: not enough RAM for {len} bytes"))?;
    buf.resize(len as usize, 0);
    Ok(buf)
}

/// Blink the LED `count` times, then pause — a boot stage, countable by eye.
///
/// This is the port's only channel until USB enumerates, and it is
/// deliberately built out of the two things least likely to be broken: a
/// GPIO write and a busy loop. It does not use the timer, because a timer
/// held in reset is exactly the kind of failure this is here to find, and it
/// does not use the console, because there is not one yet.
///
/// The count says how far boot got:
///
/// | Blinks | Reached |
/// |---|---|
/// | 1 | `main`, before the clock tree is touched |
/// | 2 | the system PLL locked and `clk_sys` switched to it |
/// | 3 | the timer is out of reset and counting |
/// | 4 | the USB PLL locked and `clk_usb` is running |
/// | 5 | the USB device controller started |
/// | continuous fast | the interpreter is running the application |
fn signal(count: u32) {
    // Cycle counts, not milliseconds. At 125 MHz these are roughly 150 ms on
    // and off; on the 6 MHz ring oscillator they are twenty times longer,
    // which is itself a useful reading — a very slow blink means the PLL
    // never came up.
    const ON: u32 = 2_000_000;
    const GAP: u32 = 6_000_000;

    for _ in 0..count {
        raw_led(true);
        spin(ON);
        raw_led(false);
        spin(ON);
    }
    spin(GAP);
}

/// Drive GP25 without going through the HAL, so a signal works even if the
/// board object is not built yet.
fn raw_led(on: bool) {
    const SIO: usize = 0xD000_0000;
    const GPIO_OE_SET: usize = SIO + 0x24;
    const GPIO_OUT_SET: usize = SIO + 0x14;
    const GPIO_OUT_CLR: usize = SIO + 0x18;
    const PADS_BANK0: usize = 0x4001_C000;
    const IO_BANK0: usize = 0x4001_4000;
    let bit = 1u32 << board::USER_LED;

    // The pad and the function mux, every time: cheap, and it means a signal
    // works no matter what ran before it.
    rustnet_hal_rp2040::reg::set_bits(PADS_BANK0 + 0x04 + 4 * board::USER_LED as usize, 1 << 6);
    rustnet_hal_rp2040::reg::clear_bits(PADS_BANK0 + 0x04 + 4 * board::USER_LED as usize, 1 << 7);
    rustnet_hal_rp2040::reg::write(IO_BANK0 + 0x04 + 8 * board::USER_LED as usize, 5);
    rustnet_hal_rp2040::reg::write(GPIO_OE_SET, bit);
    rustnet_hal_rp2040::reg::write(if on { GPIO_OUT_SET } else { GPIO_OUT_CLR }, bit);
}

fn spin(cycles: u32) {
    for _ in 0..cycles {
        core::hint::spin_loop();
    }
}

#[entry]
fn main() -> ! {
    // The heap first: everything below allocates, including the error paths.
    {
        static mut HEAP_MEM: [MaybeUninit<u8>; board::HEAP_SIZE] =
            [MaybeUninit::uninit(); board::HEAP_SIZE];
        unsafe { HEAP.init(core::ptr::addr_of_mut!(HEAP_MEM) as usize, board::HEAP_SIZE) }
    }

    // The GPIO blocks come out of reset first, so a signal can be given
    // before anything else is set up.
    rustnet_hal_rp2040::gpio::init();
    signal(1);

    // The clock tree comes from rp2040-hal, for one reason: its USB bus needs
    // a token proving clk_usb is running at 48 MHz, and that token can only be
    // produced by the same code that configured it. Hand-configuring the PLLs
    // and then asserting the result would be exactly the unchecked claim this
    // port already got wrong once.
    let mut pac = rp2040_hal::pac::Peripherals::take().unwrap();
    let mut watchdog = rp2040_hal::Watchdog::new(pac.WATCHDOG);
    let hal_clocks = rp2040_hal::clocks::init_clocks_and_plls(
        clocks::XOSC_HZ,
        pac.XOSC,
        pac.CLOCKS,
        pac.PLL_SYS,
        pac.PLL_USB,
        &mut pac.RESETS,
        &mut watchdog,
    )
    .ok();

    let clocks = match &hal_clocks {
        Some(c) => clocks::Clocks {
            xosc_hz: clocks::XOSC_HZ,
            sys_hz: c.system_clock.freq().to_Hz(),
            peri_hz: c.peripheral_clock.freq().to_Hz(),
            usb_hz: c.usb_clock.freq().to_Hz(),
        },
        None => clocks::Clocks { xosc_hz: clocks::XOSC_HZ, sys_hz: 0, peri_hz: 0, usb_hz: 0 },
    };
    if clocks.sys_hz != 0 {
        signal(2);
    }

    let mut hw = Rp2040Board::new(board::NAME, clocks);
    let time_running = hw.init();
    if time_running {
        signal(3);
    }

    // The console's pins belong to UART0, not to SIO.
    rustnet_hal_rp2040::gpio::set_function(board::CONSOLE_TX, board::FUNC_UART);
    rustnet_hal_rp2040::gpio::set_function(board::CONSOLE_RX, board::FUNC_UART);

    // The allocator outlives the device that borrows it, which is what the
    // static is for: there is no scope in `main` long enough to own it.
    let usb = hal_clocks.map(|c| {
        signal(4);
        // SAFETY: single-threaded, and this runs once during boot before
        // anything else can reach the static. Core 1 is never started.
        let allocator: &'static _ = unsafe {
            let slot = &mut *core::ptr::addr_of_mut!(USB_ALLOCATOR);
            slot.insert(UsbBusAllocator::new(UsbBus::new(
                pac.USBCTRL_REGS,
                pac.USBCTRL_DPRAM,
                c.usb_clock,
                true,
                &mut pac.RESETS,
            )))
        };
        let mut cdc = usb::UsbCdc::new(allocator);

        // Enumerate before doing anything slow.
        //
        // A host starts asking for descriptors within milliseconds of the
        // pull-up appearing and gives up after a couple of seconds. Everything
        // below this point — the banner over a 115200 UART, parsing the RNX
        // module, the LED signals — takes hundreds of milliseconds with no
        // chance to answer, and an unanswered enumeration is reported as
        // *Device Descriptor Request Failed*: the same message a corrupt
        // descriptor gives, which is what made this expensive to find. Two
        // independent USB stacks failed here identically, which is what
        // finally pointed at the schedule rather than at either of them.
        for _ in 0..300_000 {
            cdc.poll();
            if cdc.is_configured() {
                break;
            }
        }
        cdc
    });

    let mut host = FirmwareHost {
        board: hw,
        logs: alloc::vec::Vec::new(),
        partial: alloc::string::String::new(),
        rndp: rndp::Rndp::new(),
        flash: storage::QspiFlash::new(storage::STORAGE_OFFSET, storage::STORAGE_LEN),
        usb,
        npc: npc::NpcTable::default(),
    };
    let _ = host
        .board
        .uart(board::CONSOLE)
        .and_then(|u| u.configure(UartConfig { baud: 115_200, ..UartConfig::default() }));

    let _ = writeln!(
        host,
        "\r\nRustNet on {} @ {} MHz (peri {} MHz), heap {} KB",
        board::NAME,
        clocks.sys_hz / 1_000_000,
        clocks.peri_hz / 1_000_000,
        board::HEAP_SIZE / 1024
    );
    // Which slot the picker chose, and the end of an update it gave up on.
    ota::boot_report(&mut host);
    // A crash in the last run (crash.rs): say where, and do not start the app
    // that may well crash again — the board must stay reachable.
    let safe_mode = crash::take_report(&mut host);

    // A clock that never started is the failure this port is most likely to
    // hit first, and it is silent everywhere else: `sys_hz` of zero means the
    // PLL never locked and the chip is still on its ring oscillator.
    if clocks.sys_hz == 0 {
        let _ = writeln!(host, "[clocks] PLL did not lock; still on the ring oscillator");
    }
    // The failure this port shipped once: the TIMER block is in RESETS, and a
    // held timer reads zero forever. Every delay then waits for a deadline
    // that cannot arrive, which is a board that looks hung rather than one
    // whose timing is wrong.
    if !time_running {
        let _ = writeln!(host, "[timer] not counting; delays will not be accurate");
    }

    // Two blips on the LED before the interpreter starts: proof that the pin
    // path works, independent of anything the application does with it.
    if let Ok(led) = host.board.gpio(board::USER_LED) {
        let _ = led.set_mode(PinMode::Output);
    }
    for _ in 0..2 {
        blink(&mut host, 120);
    }

    // What runs is whatever was flashed, if anything, and the compiled-in
    // demo otherwise — a board with nothing loaded is a board that looks
    // broken, and this one has no other way to say hello.
    host.rndp.pub_key = rustnet_flashfs::read(&mut host.flash, storage::sys::PUB_KEY).ok();
    if host.rndp.pub_key.is_some() {
        let _ = writeln!(host, "[sec] provisioned");
    }
    let stored = rustnet_flashfs::read(&mut host.flash, storage::sys::APP).ok();
    host.rndp.app_name = match rustnet_flashfs::read(&mut host.flash, storage::sys::APP_NAME) {
        Ok(name) if stored.is_some() => String::from_utf8_lossy(&name).into_owned(),
        _ => String::from(board::APP_NAME),
    };
    let mut app: Vec<u8> = match stored {
        Some(bytes) => {
            let name = host.rndp.app_name.clone();
            let _ = writeln!(host, "[app] {name} from flash ({} bytes)", bytes.len());
            bytes
        }
        None => APP_RNX.to_vec(),
    };
    host.rndp.app_size = app.len();
    // A flashed app runs on power-up only if it was told to. The compiled-in
    // one always does: it is the demo, and there is nothing to protect.
    host.rndp.app_running = match rustnet_flashfs::read(&mut host.flash, storage::sys::AUTOSTART) {
        Ok(_) => true,
        Err(_) => rustnet_flashfs::read(&mut host.flash, storage::sys::APP).is_err(),
    };
    if safe_mode {
        host.rndp.app_running = false;
    } else if !host.rndp.app_running {
        let _ = writeln!(host, "[app] loaded but not started (no autostart)");
    }

    // Outer loop: `rustnet flash` replaces the running application without a
    // reboot, and the module borrows the bytes, so the borrow has to end
    // before the bytes can be replaced.
    loop {
        let (returned, next) = run_app(&app, host);
        host = returned;
        match next {
            Some(replacement) => {
                let name = host.rndp.app_name.clone();
                let _ = writeln!(host, "[app] switching to {name}");
                app = replacement;
            }
            // `run_app` only returns without a replacement if it has nothing
            // left to do, and there is no operating system to return to.
            None => fail_forever(&mut host),
        }
    }
}

/// Run one application until a new one is flashed. Returns the host and, if
/// `rustnet flash` delivered one, the module to run next.
fn run_app(app: &[u8], host: FirmwareHost) -> (FirmwareHost, Option<Vec<u8>>) {
    let mut host = host;
    // Procedures the last app loaded belong to it; their memory goes with it.
    host.npc = npc::NpcTable::default();

    let module = match Module::from_bytes(app) {
        Ok(m) => m,
        Err(e) => {
            // Only reachable for the compiled-in app: an uploaded one is
            // parsed before it is accepted.
            let _ = writeln!(host, "[app] will not load: {e}");
            fail_forever(&mut host);
        }
    };
    let _ = writeln!(
        host,
        "app: {} methods, {} types, {} strings",
        module.methods.len(),
        module.types.len(),
        module.strings.len()
    );

    let mut interp = Interpreter::new(&module, host);
    // Fuel slices rather than one long run: the same cooperative shape the
    // other bare-metal ports use, so the service loop has somewhere to sit.
    loop {
        // Service USB between slices. Full-speed frames are 1 ms and a slice
        // is far shorter, so polling here is ample and costs no interrupt.
        if let Some(usb) = interp.host.usb.as_mut() {
            usb.poll();
        }
        // Answer the tools between slices, the same cooperative shape the
        // other bare-metal ports use.
        interp.host.poll_rndp();

        if interp.host.rndp.pending_app.is_some() {
            break;
        }
        if !interp.host.rndp.app_running {
            continue;
        }

        match interp.run(2_000) {
            RunExit::OutOfFuel => {}
            // An application that ends, faults or pauses must not take the
            // firmware with it: stay reachable and report it, so a bad app can
            // be replaced over the wire rather than needing a reflash.
            RunExit::Completed => {
                let _ = writeln!(interp.host, "[app] returned");
                interp.host.rndp.app_running = false;
            }
            RunExit::Paused { method, il_offset } => {
                let _ = writeln!(interp.host, "[app] paused at method {method} il {il_offset}");
                interp.host.rndp.app_running = false;
            }
            RunExit::Error(e) => {
                let _ = writeln!(interp.host, "[app] fault: {e}");
                interp.host.rndp.app_running = false;
            }
        }
    }

    let mut host = interp.host;
    let next = host.rndp.pending_app.take();
    if next.is_some() {
        host.rndp.app_running = true;
    }
    (host, next)
}

fn blink(host: &mut FirmwareHost, ms: u64) {
    if let Ok(led) = host.board.gpio(board::USER_LED) {
        let _ = led.write(Level::High);
    }
    serviced_delay(host, ms);
    if let Ok(led) = host.board.gpio(board::USER_LED) {
        let _ = led.write(Level::Low);
    }
    serviced_delay(host, ms);
}

/// Wait, while still answering the host.
///
/// A plain delay is a hole in the USB schedule, and a hole long enough is an
/// enumeration the host abandons. Every wait on the boot path goes through
/// here for that reason.
fn serviced_delay(host: &mut FirmwareHost, ms: u64) {
    for _ in 0..ms {
        if let Some(usb) = host.usb.as_mut() {
            usb.poll();
        }
        host.board.delay().delay_ms(1);
    }
}

/// Blink fast, forever. A stopped board that is dark is indistinguishable from
/// one that never started; a stopped board that is blinking has at least told
/// you it got as far as the interpreter.
fn fail_forever(host: &mut FirmwareHost) -> ! {
    loop {
        blink(host, 60);
    }
}

#[panic_handler]
fn panic(info: &core::panic::PanicInfo) -> ! {
    // Noted first (crash.rs), in case the reporting below faults too.
    let (line, file) = info.location().map(|l| (l.line(), crash::file_tag(l.file()))).unwrap_or((0, 0));
    let reset = crash::record(true, line, file);
    // The console is the only channel this board has, and a panic that says
    // nothing is a board that appears to hang.
    let mut hw = Rp2040Board::new(board::NAME, clocks::Clocks {
        xosc_hz: clocks::XOSC_HZ,
        sys_hz: board::SYS_HZ,
        peri_hz: board::SYS_HZ,
        usb_hz: clocks::USB_HZ,
    });
    let mut host = FirmwareHost {
        board: hw_init(&mut hw),
        logs: alloc::vec::Vec::new(),
        partial: alloc::string::String::new(),
        rndp: rndp::Rndp::new(),
        flash: storage::QspiFlash::new(storage::STORAGE_OFFSET, storage::STORAGE_LEN),
        usb: None,
        npc: npc::NpcTable::default(),
    };
    let _ = writeln!(host, "\r\n[panic] {info}");
    // Over USB nobody sees the UART line above: reset into safe mode, where
    // the next boot reports the panic and RNDP answers.
    if reset {
        cortex_m::peripheral::SCB::sys_reset();
    }
    loop {
        blink(&mut host, 40);
    }
}

/// Borrow-checker plumbing for the panic handler: it needs a board by value
/// and `init` takes `&mut self`.
fn hw_init(hw: &mut Rp2040Board) -> Rp2040Board {
    let _ = hw.init();
    Rp2040Board::new(board::NAME, hw.clocks())
}
