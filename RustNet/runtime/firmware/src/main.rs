//! RustNet virtual device firmware entry point.
//!
//! Runs the same service stack a real MCU runs, with the RNDP protocol
//! served over TCP — or over stdin/stdout with `--stdio`, the same
//! byte-pipe shape a USB-CDC/UART transport has on real silicon. Tools
//! (CLI, Workbench, VSCode) connect to it exactly as they would to
//! hardware.
//!
//! Usage: rustnet-firmware [--port 7878] [--home <dir>] [--ephemeral] [--stdio]

use rustnet_firmware::apphost::SharedState;
use rustnet_firmware::chip;
use rustnet_firmware::proto::Frame;
use rustnet_firmware::service::DeviceService;
use rustnet_firmware::dirfs::DirFs;
use rustnet_fs::{MemFs, Vfs};
use rustnet_hal_host::HostBoard;
use std::io::{Read, Write};
use std::net::TcpListener;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let mut port = 7878u16;
    let mut home: Option<PathBuf> = None;
    let mut ephemeral = false;
    let mut stdio = false;
    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "--port" => {
                i += 1;
                port = args.get(i).and_then(|v| v.parse().ok()).unwrap_or(7878);
            }
            "--home" => {
                i += 1;
                home = args.get(i).map(PathBuf::from);
            }
            "--ephemeral" => ephemeral = true,
            "--stdio" => stdio = true,
            "--help" | "-h" => {
                println!("rustnet-firmware [--port 7878] [--home <dir>] [--ephemeral] [--stdio]");
                return;
            }
            other => {
                eprintln!("unknown argument: {other}");
                std::process::exit(2);
            }
        }
        i += 1;
    }

    let fs: Box<dyn Vfs> = if ephemeral {
        Box::new(MemFs::new())
    } else {
        let root = home.unwrap_or_else(|| PathBuf::from(".rustnet-device"));
        Box::new(DirFs::new(root.clone()).unwrap_or_else(|e| {
            eprintln!("cannot open device home {root:?}: {e}");
            std::process::exit(1);
        }))
    };

    let state = SharedState::new(chip::make_board(), fs);
    let logger = state.logger.clone();
    let service = Arc::new(Mutex::new(DeviceService::new(state)));

    if stdio {
        // USB-CDC/UART-shaped transport: RNDP frames over stdin/stdout.
        // The banner goes to stderr — stdout belongs to the protocol.
        eprintln!(
            "RustNet virtual device '{}' (chip {}) serving RNDP on stdio",
            chip::board_name(),
            chip::chip_family().name(),
        );
        logger.info("boot", "RNDP server ready (stdio)");
        serve_pipe(StdioPipe, &service);
        return;
    }

    let listener = TcpListener::bind(("127.0.0.1", port)).unwrap_or_else(|e| {
        eprintln!("cannot bind port {port}: {e}");
        std::process::exit(1);
    });
    println!(
        "RustNet virtual device '{}' (chip {}) listening on 127.0.0.1:{}",
        chip::board_name(),
        chip::chip_family().name(),
        listener.local_addr().map(|a| a.port()).unwrap_or(port)
    );
    logger.info("boot", "RNDP server ready");

    for stream in listener.incoming() {
        let Ok(stream) = stream else { continue };
        let service = service.clone();
        std::thread::spawn(move || serve_pipe(stream, &service));
    }
}

/// Serve one RNDP session over any byte pipe (TCP socket, stdio, or —
/// on real silicon — a USB-CDC/UART driver exposing Read + Write).
fn serve_pipe<S: Read + Write>(mut pipe: S, service: &Arc<Mutex<DeviceService>>) {
    let mut buf: Vec<u8> = Vec::new();
    let mut chunk = [0u8; 4096];
    loop {
        match pipe.read(&mut chunk) {
            Ok(0) | Err(_) => return,
            Ok(n) => buf.extend_from_slice(&chunk[..n]),
        }
        loop {
            match Frame::decode(&buf) {
                Ok(Some((frame, used))) => {
                    buf.drain(..used);
                    let response = service.lock().unwrap().handle(&frame);
                    if pipe.write_all(&response.encode()).is_err() || pipe.flush().is_err() {
                        return;
                    }
                }
                Ok(None) => break,
                Err(_) => return, // desync: drop connection
            }
        }
    }
}

/// stdin/stdout as a byte pipe.
struct StdioPipe;

impl Read for StdioPipe {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        std::io::stdin().lock().read(buf)
    }
}

impl Write for StdioPipe {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        std::io::stdout().lock().write(buf)
    }
    fn flush(&mut self) -> std::io::Result<()> {
        std::io::stdout().lock().flush()
    }
}
