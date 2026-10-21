//! RNDP over Bluetooth LE: a NimBLE GATT server (feature `ble`).
//!
//! Nordic-UART-shaped: the host writes RNDP bytes to the RX characteristic and
//! the device answers with notifications on TX, each cut to the connection's
//! ATT MTU minus 3. The packet reassembly and the chunking are radio-free and
//! live (with their tests) in `rustnet_firmware::ble`; what is here is only the
//! NimBLE wiring.
//!
//! ## Threads
//!
//! NimBLE delivers writes on its own host task, and nothing slow may run there:
//! an app upload ends in an RSA verification, and a host task that is busy
//! cannot free the mbufs our notifications need — so the response to a request
//! would wait for buffers only the waiting task could release. The write
//! callback therefore only queues the bytes; an `rndp-ble` thread decodes,
//! handles and notifies, exactly as the UART loop does, and is serialised
//! against UART and TCP by the same `Mutex<DeviceService>`.
//!
//! ## Why notifications are sent by hand
//!
//! `BLECharacteristic::notify_with` hands NimBLE whatever
//! `ble_hs_mbuf_from_flat` returned. When the mbuf pool is exhausted that is
//! null, and `ble_gatts_notify_custom` reads a null buffer as "send the
//! attribute's stored value" — so a burst that outruns the radio would put
//! stale bytes into the middle of a frame instead of failing. Here a null
//! buffer means "wait and retry", which is what a full pool actually asks for.

use esp32_nimble::utilities::BleUuid;
use esp32_nimble::{BLEAdvertisementData, BLEDevice, NimbleProperties};
use esp_idf_svc::sys;
use rustnet_firmware::ble::{self as rndp_ble, RndpReassembler};
use rustnet_firmware::service::DeviceService;
use std::sync::atomic::{AtomicU16, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::time::Duration;

/// NimBLE's "no connection" handle.
const NO_CONN: u16 = sys::BLE_HS_CONN_HANDLE_NONE as u16;

/// The connection RNDP is being served to, or [`NO_CONN`].
static CONN: AtomicU16 = AtomicU16::new(NO_CONN);

/// Largest MTU we ask for in the exchange. NimBLE caps it at its own
/// `BLE_ATT_MTU_MAX`; Windows typically settles on 247 or 517.
const PREFERRED_MTU: u16 = 517;

/// How long one notification may wait for a free mbuf before the response is
/// abandoned (the host times out and retries the request).
const NOTIFY_GIVE_UP: Duration = Duration::from_secs(3);

enum Event {
    /// A connection opened or closed: whatever was half-received is void.
    Reset,
    /// Bytes written to RX.
    Data(Vec<u8>),
}

fn uuid(s: &str) -> BleUuid {
    BleUuid::from_uuid128_string(s).expect("valid UUID constant")
}

/// Bring up the GATT server and start advertising. Blocks until the NimBLE
/// host has synced with the controller, so call it from its own thread.
pub fn start(
    service: Arc<Mutex<DeviceService>>,
    logger: rustnet_diag::Logger,
) -> Result<(), String> {
    let device = BLEDevice::take();
    let mac = device
        .get_addr()
        .map(|a| a.as_be_bytes())
        .unwrap_or_else(|_| read_bt_mac());
    let name = rndp_ble::advertised_name(mac);
    BLEDevice::set_device_name(&name).map_err(|e| format!("device name: {e:?}"))?;
    if let Err(e) = device.set_preferred_mtu(PREFERRED_MTU) {
        logger.warn("ble", format!("preferred MTU {PREFERRED_MTU} refused: {e:?}"));
    }

    let (events, inbox) = mpsc::channel::<Event>();
    let server = device.get_server();
    {
        let events = events.clone();
        server.on_connect(move |_server, desc| {
            CONN.store(desc.conn_handle(), Ordering::Release);
            let _ = events.send(Event::Reset);
        });
    }
    {
        let events = events.clone();
        server.on_disconnect(move |_desc, _reason| {
            CONN.store(NO_CONN, Ordering::Release);
            let _ = events.send(Event::Reset);
        });
    }
    // Back on the air as soon as the host goes away (the crate's default,
    // stated here because the device is unreachable over BLE without it).
    server.advertise_on_disconnect(true);

    let gatt = server.create_service(uuid(rndp_ble::SERVICE_UUID));
    let rx = gatt.lock().create_characteristic(
        uuid(rndp_ble::RX_CHAR_UUID),
        NimbleProperties::WRITE | NimbleProperties::WRITE_NO_RSP,
    );
    rx.lock().on_write(move |args| {
        let _ = events.send(Event::Data(args.recv_data().to_vec()));
    });
    gatt.lock()
        .create_characteristic(uuid(rndp_ble::TX_CHAR_UUID), NimbleProperties::NOTIFY);

    // Name + 128-bit UUID exceed the 31-byte advertisement; the crate moves the
    // name into the scan response, which active scanners (Windows, phones)
    // collect.
    let advertising = device.get_advertising();
    advertising
        .lock()
        .set_data(
            BLEAdvertisementData::new()
                .name(&name)
                .add_service_uuid(uuid(rndp_ble::SERVICE_UUID)),
        )
        .map_err(|e| format!("advertising data: {e:?}"))?;
    advertising
        .lock()
        .start()
        .map_err(|e| format!("advertising start: {e:?}"))?;

    let tx_handle = find_tx_handle().ok_or("TX characteristic not registered")?;
    logger.info("ble", format!("RNDP server ready (ble, advertising as {name})"));

    std::thread::Builder::new()
        .name("rndp-ble".into())
        .stack_size(20 * 1024)
        .spawn(move || session(inbox, tx_handle, service))
        .map_err(|e| format!("rndp-ble thread: {e}"))?;
    Ok(())
}

/// The RNDP session loop — the UART loop in `main`, fed by GATT writes.
fn session(inbox: mpsc::Receiver<Event>, tx_handle: u16, service: Arc<Mutex<DeviceService>>) {
    let mut stream = RndpReassembler::new();
    while let Ok(event) = inbox.recv() {
        match event {
            Event::Reset => stream.reset(),
            Event::Data(bytes) => {
                stream.push(&bytes);
                while let Some(frame) = stream.next_frame() {
                    let (response, reboot) = {
                        let mut svc = service.lock().unwrap();
                        let r = svc.handle(&frame);
                        (r, svc.reboot_requested)
                    };
                    send(tx_handle, &response.encode());
                    if reboot {
                        std::thread::sleep(Duration::from_millis(200));
                        unsafe { sys::esp_restart() };
                    }
                }
            }
        }
    }
}

/// Notify `data` to the connected host in MTU-sized pieces. Returns early,
/// dropping the rest, if the host disconnects or the stack stays out of
/// buffers — RNDP is request/response and the host retries.
fn send(tx_handle: u16, data: &[u8]) {
    let conn = CONN.load(Ordering::Acquire);
    if conn == NO_CONN {
        return;
    }
    let mtu = unsafe { sys::ble_att_mtu(conn) };
    for piece in rndp_ble::chunks(data, mtu) {
        let started = std::time::Instant::now();
        loop {
            if CONN.load(Ordering::Acquire) != conn {
                return;
            }
            let om = unsafe {
                sys::ble_hs_mbuf_from_flat(piece.as_ptr() as *const _, piece.len() as u16)
            };
            // NimBLE consumes the mbuf whether or not the notify succeeds.
            let rc = if om.is_null() {
                sys::BLE_HS_ENOMEM as i32
            } else {
                unsafe { sys::ble_gatts_notify_custom(conn, tx_handle, om) }
            };
            if rc == 0 {
                break;
            }
            if rc == sys::BLE_HS_ENOTCONN as i32 || started.elapsed() > NOTIFY_GIVE_UP {
                return;
            }
            std::thread::sleep(Duration::from_millis(5));
        }
    }
}

/// The TX characteristic's value handle, looked up from NimBLE's registered
/// GATT table (the crate keeps the handle private).
fn find_tx_handle() -> Option<u16> {
    let svc: sys::ble_uuid_any_t = uuid(rndp_ble::SERVICE_UUID).into();
    let chr: sys::ble_uuid_any_t = uuid(rndp_ble::TX_CHAR_UUID).into();
    let mut handle: u16 = 0;
    let rc = unsafe {
        sys::ble_gatts_find_chr(&svc.u, &chr.u, std::ptr::null_mut(), &mut handle)
    };
    (rc == 0).then_some(handle)
}

/// The Bluetooth MAC from eFuse, most significant byte first — the fallback
/// for naming when the host stack cannot report its identity address.
fn read_bt_mac() -> [u8; 6] {
    let mut mac = [0u8; 6];
    unsafe {
        sys::esp_read_mac(mac.as_mut_ptr(), sys::esp_mac_type_t_ESP_MAC_BT);
    }
    mac
}
