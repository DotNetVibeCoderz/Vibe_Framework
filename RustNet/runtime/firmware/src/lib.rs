//! RustNet firmware: everything that runs on the device.
//!
//! - [`proto`] — RNDP (RustNet Device Protocol) framing shared by USB/UART
//!   and the host TCP transport.
//! - [`service`] — command handlers: app flash/erase/list, data upload,
//!   secure config, WiFi, boot image, logs, profiler, OTA, debugger.
//! - [`apphost`] — runs a managed (.rnx) app on the interpreter and binds
//!   `RustNet.*` internal calls to the HAL/services.
//! - [`chip`] — chip variant selection (feature `chip-*`).

pub mod apphost;
pub mod dirfs;
pub mod chip;
pub mod proto;
pub mod service;
pub mod vnc;
