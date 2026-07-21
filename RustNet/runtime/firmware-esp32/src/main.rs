//! RustNet firmware for ESP32 (Xtensa) on ESP-IDF.
//!
//! The same `DeviceService` the virtual device runs, with:
//! - RNDP over UART0 (raw driver — binary safe), plus over TCP :7878
//!   when WiFi credentials are configured (`rustnet wifi set`)
//! - a wear-levelled FAT partition mounted at /spiflash backing the
//!   device filesystem, so provisioning and apps survive reboots
//! - real chip reboot on CMD_REBOOT

mod board;

use esp_idf_svc::sys;
use rustnet_firmware::apphost::SharedState;
use rustnet_firmware::dirfs::DirFs;
use rustnet_firmware::proto::Frame;
use rustnet_firmware::service::DeviceService;
use std::sync::{Arc, Mutex};

fn main() {
    sys::link_patches();

    // RNDP is binary: talk to UART0 through the driver API directly —
    // the console VFS would CR/LF-translate and corrupt frames.
    unsafe {
        sys::uart_driver_install(0, 4096, 4096, 0, std::ptr::null_mut(), 0);
    }

    // Persistent storage: wear-levelled FAT on the "storage" partition.
    let fs: Box<dyn rustnet_fs::Vfs> = match mount_fat() {
        Ok(()) => Box::new(
            DirFs::new(std::path::PathBuf::from("/spiflash"))
                .expect("FAT mounted but unusable"),
        ),
        Err(e) => {
            // Storage failure must not brick the device: fall back to RAM.
            uart0_write(format!("[rustnet] FAT mount failed ({e}), using MemFs\r\n").as_bytes());
            Box::new(rustnet_fs::MemFs::new())
        }
    };

    // App threads: a 12 KB release-build stack fits the WiFi-fragmented
    // heap where the 16 KB default cannot find a contiguous block.
    rustnet_firmware::apphost::set_app_stack(12 * 1024);

    let board = Box::new(board::Esp32IdfBoard::new());
    let state = SharedState::new(board, fs);
    let logger = state.logger.clone();

    // A panic in an app thread must not reboot the device: log it to the
    // ring buffer (readable via `rustnet logs`) and park the thread.
    let panic_log = logger.clone();
    std::panic::set_hook(Box::new(move |info| {
        panic_log.error("panic", format!("{info}"));
        loop {
            std::thread::sleep(std::time::Duration::from_secs(3600));
        }
    }));

    logger.info("boot", "RustNet on ESP32-WROOM-32 (ESP-IDF)");
    let free = unsafe { sys::esp_get_free_heap_size() };
    logger.info("boot", format!("free heap: {free} bytes"));

    let service = Arc::new(Mutex::new(DeviceService::new(state)));

    // WiFi + TCP RNDP when credentials are stored (rustnet wifi set).
    let (ssid, psk) = {
        let svc = service.lock().unwrap();
        (svc.read_config("wifi.ssid"), svc.read_config("wifi.psk"))
    };
    if let Some(ssid) = ssid.filter(|s| !s.is_empty()) {
        let psk = psk.unwrap_or_default();
        match wifi::start(&ssid, &psk, &logger) {
            Ok(wifi_handle) => {
                // Keep the driver alive for the lifetime of the device.
                std::mem::forget(wifi_handle);
                // Real time for the RTC: SNTP sets the system clock.
                match esp_idf_svc::sntp::EspSntp::new_default() {
                    Ok(sntp) => {
                        std::mem::forget(sntp);
                        logger.info("time", "SNTP started (pool.ntp.org)");
                    }
                    Err(e) => logger.warn("time", format!("SNTP failed: {e}")),
                }
                logger.info("boot", format!("free heap after wifi: {} bytes", unsafe { sys::esp_get_free_heap_size() }));
                let svc = service.clone();
                let log = logger.clone();
                std::thread::Builder::new()
                    .name("rndp-tcp".into())
                    .stack_size(20 * 1024)
                    .spawn(move || tcp_server(svc, log))
                    .ok();
            }
            Err(e) => logger.warn("wifi", format!("connect failed: {e}")),
        }
    } else {
        logger.info("wifi", "no credentials stored; serial-only (rustnet wifi set)");
    }

    uart0_write(b"RustNet ESP32 firmware ready; RNDP on UART0\r\n");
    logger.info("boot", "RNDP server ready (uart0)");

    // Same session loop as the virtual device's serve_pipe, over the raw
    // UART driver (binary-safe in both directions).
    let mut buf: Vec<u8> = Vec::new();
    let mut chunk = [0u8; 1024];
    loop {
        let n = unsafe {
            sys::uart_read_bytes(
                0,
                chunk.as_mut_ptr() as *mut core::ffi::c_void,
                chunk.len() as u32,
                5, // FreeRTOS ticks (~50 ms @ 100 Hz)
            )
        };
        if n > 0 {
            buf.extend_from_slice(&chunk[..n as usize]);
        }
        loop {
            match Frame::decode(&buf) {
                Ok(Some((frame, used))) => {
                    buf.drain(..used);
                    let (response, reboot) = {
                        let mut svc = service.lock().unwrap();
                        let r = svc.handle(&frame);
                        (r, svc.reboot_requested)
                    };
                    uart0_write(&response.encode());
                    if reboot {
                        std::thread::sleep(std::time::Duration::from_millis(200));
                        unsafe { sys::esp_restart() };
                    }
                }
                Ok(None) => break,
                Err(_) => {
                    // Desync on a byte pipe: drop garbage, keep serving.
                    buf.clear();
                    break;
                }
            }
        }
    }
}

fn mount_fat() -> Result<(), String> {
    let base = c"/spiflash";
    let label = c"storage";
    let cfg = sys::esp_vfs_fat_mount_config_t {
        format_if_mount_failed: true,
        max_files: 8,
        allocation_unit_size: 4096,
        ..Default::default()
    };
    let mut wl: sys::wl_handle_t = sys::WL_INVALID_HANDLE;
    let err = unsafe {
        sys::esp_vfs_fat_spiflash_mount_rw_wl(base.as_ptr(), label.as_ptr(), &cfg, &mut wl)
    };
    if err != 0 {
        return Err(format!("esp_err {err}"));
    }
    Ok(())
}

fn tcp_server(service: Arc<Mutex<DeviceService>>, logger: rustnet_diag::Logger) {
    let listener = match std::net::TcpListener::bind("0.0.0.0:7878") {
        Ok(l) => l,
        Err(e) => {
            logger.warn("net", format!("tcp bind failed: {e}"));
            return;
        }
    };
    logger.info("net", "RNDP server ready (tcp :7878)");
    for stream in listener.incoming() {
        let Ok(mut stream) = stream else { continue };
        let mut buf: Vec<u8> = Vec::new();
        let mut chunk = [0u8; 2048];
        loop {
            use std::io::{Read, Write};
            match stream.read(&mut chunk) {
                Ok(0) | Err(_) => break,
                Ok(n) => buf.extend_from_slice(&chunk[..n]),
            }
            loop {
                match Frame::decode(&buf) {
                    Ok(Some((frame, used))) => {
                        buf.drain(..used);
                        let (response, reboot) = {
                            let mut svc = service.lock().unwrap();
                            let r = svc.handle(&frame);
                            (r, svc.reboot_requested)
                        };
                        if stream.write_all(&response.encode()).is_err() {
                            break;
                        }
                        if reboot {
                            std::thread::sleep(std::time::Duration::from_millis(200));
                            unsafe { sys::esp_restart() };
                        }
                    }
                    Ok(None) => break,
                    Err(_) => {
                        buf.clear();
                        break;
                    }
                }
            }
        }
    }
}

mod wifi {
    use esp_idf_svc::eventloop::EspSystemEventLoop;
    use esp_idf_svc::hal::peripherals::Peripherals;
    use esp_idf_svc::nvs::EspDefaultNvsPartition;
    use esp_idf_svc::wifi::{BlockingWifi, ClientConfiguration, Configuration, EspWifi};

    /// Bring up STA WiFi; returns the live driver (must be kept alive).
    pub fn start(
        ssid: &str,
        psk: &str,
        logger: &rustnet_diag::Logger,
    ) -> Result<Box<BlockingWifi<EspWifi<'static>>>, String> {
        let peripherals = Peripherals::take().map_err(|e| e.to_string())?;
        let sysloop = EspSystemEventLoop::take().map_err(|e| e.to_string())?;
        let nvs = EspDefaultNvsPartition::take().map_err(|e| e.to_string())?;
        let wifi = EspWifi::new(peripherals.modem, sysloop.clone(), Some(nvs))
            .map_err(|e| e.to_string())?;
        let mut wifi = BlockingWifi::wrap(wifi, sysloop).map_err(|e| e.to_string())?;
        let config = Configuration::Client(ClientConfiguration {
            ssid: ssid.try_into().map_err(|_| "ssid too long")?,
            password: psk.try_into().map_err(|_| "password too long")?,
            ..Default::default()
        });
        wifi.set_configuration(&config).map_err(|e| e.to_string())?;
        wifi.start().map_err(|e| e.to_string())?;
        // Warm-boot joins are flaky on some APs: retry a few times.
        let mut last_err = String::new();
        let mut joined = false;
        for attempt in 1..=3 {
            logger.info("wifi", format!("connecting to '{ssid}' (attempt {attempt})..."));
            match wifi.connect().and_then(|_| wifi.wait_netif_up()) {
                Ok(()) => {
                    joined = true;
                    break;
                }
                Err(e) => {
                    last_err = e.to_string();
                    let _ = wifi.disconnect();
                    std::thread::sleep(std::time::Duration::from_millis(1500));
                }
            }
        }
        if !joined {
            return Err(last_err);
        }
        let ip = wifi
            .wifi()
            .sta_netif()
            .get_ip_info()
            .map(|i| i.ip.to_string())
            .unwrap_or_default();
        logger.info("wifi", format!("connected, ip {ip} (RNDP tcp:{ip}:7878)"));
        Ok(Box::new(wifi))
    }
}

fn uart0_write(data: &[u8]) {
    unsafe {
        sys::uart_write_bytes(0, data.as_ptr() as *const core::ffi::c_void, data.len());
        sys::uart_wait_tx_done(0, 100);
    }
}
