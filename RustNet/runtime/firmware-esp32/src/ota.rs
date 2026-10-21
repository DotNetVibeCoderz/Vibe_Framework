//! OTA A/B on the ESP32 through ESP-IDF's own OTA machinery.
//!
//! The RNSB container streams in over RNDP and is verified as it goes
//! (`StreamVerifier`): the payload — an ESP app image — is written straight
//! into the next `ota_x` partition, so a 2.7 MB firmware never has to fit in
//! RAM. Only when the signature checks out is the partition handed to
//! `esp_ota_end` (which validates the app image itself) and made the boot
//! partition. The switch and the rollback are the ESP-IDF bootloader's: with
//! `CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE` a new image boots "pending verify"
//! and, unless `rustnet ota confirm` marks it valid, the next reset returns to
//! the previous one.
//!
//! `rustnet ota rollback` is the one place ESP-IDF's model needs help: it
//! boots *any* newly selected slot pending verification, including the
//! known-good one a rollback returns to — so an unconfirmed reset after a
//! rollback would go forward again to the image just abandoned. A rollback
//! therefore leaves a note in NVS naming its target, and the first boot that
//! finds itself running that target marks it valid ([`EspOta::boot_report`]).
//!
//! Needs a partition table with two OTA slots — `partitions-ota-16mb.csv`, for
//! the 16 MB M5Stack boards. A table without them makes `begin` fail with a
//! message saying so; nothing else changes.

use std::ffi::CStr;

use esp_idf_svc::sys;
use rustnet_firmware::ota::{OtaArmed, OtaBackend};
use rustnet_secureboot::{ImageKind, StreamVerifier};

/// `OTA_WITH_SEQUENTIAL_WRITES`: erase each sector as the write reaches it,
/// rather than erasing all 3 MB up front (which stalls RNDP for seconds).
const OTA_WITH_SEQUENTIAL_WRITES: usize = 0xFFFF_FFFE;

struct Session {
    handle: sys::esp_ota_handle_t,
    partition: *const sys::esp_partition_t,
    verifier: StreamVerifier,
    key: Vec<u8>,
    written: usize,
}

// The partition pointer refers to ESP-IDF's static partition table.
unsafe impl Send for Session {}

#[derive(Default)]
pub struct EspOta {
    session: Option<Session>,
}

fn label(p: *const sys::esp_partition_t) -> String {
    if p.is_null() {
        return "?".into();
    }
    // SAFETY: a non-null partition from ESP-IDF's table; label is NUL-terminated.
    unsafe { CStr::from_ptr((*p).label.as_ptr()) }.to_string_lossy().into_owned()
}

fn check(code: sys::esp_err_t, what: &str) -> Result<(), String> {
    if code == sys::ESP_OK {
        Ok(())
    } else {
        // SAFETY: esp_err_to_name returns a static string.
        let name = unsafe { CStr::from_ptr(sys::esp_err_to_name(code)) }.to_string_lossy();
        Err(format!("{what}: {name}"))
    }
}

/// NVS namespace and key of the rollback note: the target slot's subtype.
const NVS_NAMESPACE: &CStr = c"rustnet";
const NVS_ROLLBACK_KEY: &CStr = c"ota_rollback";

/// Open the rustnet NVS namespace, run `f` on it, commit and close.
fn with_nvs<T>(f: impl FnOnce(sys::nvs_handle_t) -> T) -> Result<T, String> {
    // SAFETY: plain ESP-IDF calls; the handle is closed before returning.
    // `nvs_flash_init` on an initialised partition returns ESP_OK, so this
    // coexists with the WiFi stack taking the default partition later.
    unsafe {
        check(sys::nvs_flash_init(), "nvs_flash_init")?;
        let mut handle: sys::nvs_handle_t = 0;
        check(
            sys::nvs_open(NVS_NAMESPACE.as_ptr(), sys::nvs_open_mode_t_NVS_READWRITE, &mut handle),
            "nvs_open",
        )?;
        let out = f(handle);
        sys::nvs_commit(handle);
        sys::nvs_close(handle);
        Ok(out)
    }
}

impl EspOta {
    pub fn new() -> Self {
        Self::default()
    }

    fn abort(&mut self) {
        if let Some(s) = self.session.take() {
            // SAFETY: the handle came from esp_ota_begin and was not ended.
            unsafe { sys::esp_ota_abort(s.handle) };
        }
    }

    fn write_inner(&mut self, chunk: &[u8]) -> Result<(), String> {
        let s = self.session.as_mut().ok_or("no OTA update in progress")?;
        let payload = s.verifier.feed(chunk).map_err(|e| format!("ota image rejected: {e}"))?;
        if s.verifier.kind().is_some_and(|k| k != ImageKind::Firmware) {
            return Err("ota image is not a firmware image".into());
        }
        if payload.is_empty() {
            return Ok(());
        }
        // SAFETY: partition is from the static table; size is a plain field.
        let capacity = unsafe { (*s.partition).size } as usize;
        if s.written + payload.len() > capacity {
            return Err(format!("firmware is larger than the {capacity}-byte OTA slot"));
        }
        // SAFETY: payload outlives the call; the handle is live.
        check(unsafe { sys::esp_ota_write(s.handle, payload.as_ptr().cast(), payload.len()) }, "esp_ota_write")?;
        s.written += payload.len();
        Ok(())
    }

    /// One line for the boot log: which slot is running and whether it still
    /// awaits `rustnet ota confirm`.
    ///
    /// Also settles a rollback: if this boot is the slot a `rustnet ota
    /// rollback` chose, it is the known-good image and is marked valid here.
    pub fn boot_report() -> String {
        // SAFETY: plain ESP-IDF queries.
        unsafe {
            let running = sys::esp_ota_get_running_partition();
            let mut state: sys::esp_ota_img_states_t = 0;
            let mut pending = sys::esp_ota_get_state_partition(running, &mut state) == sys::ESP_OK
                && state == sys::esp_ota_img_states_t_ESP_OTA_IMG_PENDING_VERIFY;
            let mut target: u8 = 0xFF;
            let noted = with_nvs(|h| {
                let found = sys::nvs_get_u8(h, NVS_ROLLBACK_KEY.as_ptr(), &mut target) == sys::ESP_OK;
                if found {
                    sys::nvs_erase_key(h, NVS_ROLLBACK_KEY.as_ptr());
                }
                found
            })
            .unwrap_or(false);
            let rolled_back = noted && target as u32 == (*running).subtype;
            if rolled_back && pending && sys::esp_ota_mark_app_valid_cancel_rollback() == sys::ESP_OK {
                pending = false;
            }
            format!(
                "running from {}{}",
                label(running),
                if pending {
                    " — new image, pending `rustnet ota confirm` (the next reset rolls back)"
                } else if rolled_back {
                    " (rolled back; confirmed)"
                } else {
                    ""
                }
            )
        }
    }
}

impl OtaBackend for EspOta {
    fn begin(&mut self, pub_key: &[u8]) -> Result<(), String> {
        self.abort();
        // SAFETY: plain ESP-IDF calls; the out-pointer is a local.
        unsafe {
            // An update on top of an unconfirmed image has no good image to
            // fall back to: the bootloader's rollback target would be the
            // slot this update is about to overwrite. Seen on a Core2 — the
            // new image came back on the old slot. Settle the current one first.
            let running = sys::esp_ota_get_running_partition();
            let mut state: sys::esp_ota_img_states_t = 0;
            if sys::esp_ota_get_state_partition(running, &mut state) == sys::ESP_OK
                && state == sys::esp_ota_img_states_t_ESP_OTA_IMG_PENDING_VERIFY
            {
                return Err(format!(
                    "{} is still pending: run `rustnet ota confirm` (or `ota rollback`) before another update",
                    label(running)
                ));
            }
            let partition = sys::esp_ota_get_next_update_partition(core::ptr::null());
            if partition.is_null() {
                return Err("this partition table has no OTA slots: flash with \
                            runtime/firmware-esp32/partitions-ota-16mb.csv (16 MB boards)"
                    .into());
            }
            let mut handle: sys::esp_ota_handle_t = 0;
            check(sys::esp_ota_begin(partition, OTA_WITH_SEQUENTIAL_WRITES, &mut handle), "esp_ota_begin")?;
            self.session = Some(Session {
                handle,
                partition,
                verifier: StreamVerifier::new(rustnet_firmware::chip::chip_family()),
                key: pub_key.to_vec(),
                written: 0,
            });
        }
        Ok(())
    }

    fn write(&mut self, chunk: &[u8]) -> Result<(), String> {
        let r = self.write_inner(chunk);
        if r.is_err() {
            self.abort();
        }
        r
    }

    fn end(&mut self) -> Result<OtaArmed, String> {
        let s = self.session.take().ok_or("no OTA update in progress")?;
        let partition = s.partition;
        if let Err(e) = s.verifier.finish(&s.key) {
            // SAFETY: handle still open.
            unsafe { sys::esp_ota_abort(s.handle) };
            return Err(format!("ota verify failed: {e}"));
        }
        // SAFETY: handle open, partition valid; esp_ota_end closes the handle
        // whether or not it succeeds.
        unsafe {
            check(sys::esp_ota_end(s.handle), "esp_ota_end (app image invalid)")?;
            check(sys::esp_ota_set_boot_partition(partition), "esp_ota_set_boot_partition")?;
        }
        Ok(OtaArmed { slot: label(partition), reboot: true })
    }

    fn confirm(&mut self) -> Result<String, String> {
        // SAFETY: plain ESP-IDF calls. Marking an already-valid image is a no-op.
        unsafe {
            check(sys::esp_ota_mark_app_valid_cancel_rollback(), "esp_ota_mark_app_valid_cancel_rollback")?;
            Ok(label(sys::esp_ota_get_running_partition()))
        }
    }

    fn rollback(&mut self) -> Result<OtaArmed, String> {
        self.abort();
        // SAFETY: plain ESP-IDF calls on the static partition table.
        unsafe {
            let running = sys::esp_ota_get_running_partition();
            let other = sys::esp_ota_get_next_update_partition(running);
            if other.is_null() {
                return Err("NothingToRollback: no other OTA slot".into());
            }
            let mut desc: sys::esp_app_desc_t = core::mem::zeroed();
            if sys::esp_ota_get_partition_description(other, &mut desc) != sys::ESP_OK {
                return Err(format!("NothingToRollback: {} holds no valid image", label(other)));
            }
            let mut state: sys::esp_ota_img_states_t = 0;
            if sys::esp_ota_get_state_partition(other, &mut state) == sys::ESP_OK
                && (state == sys::esp_ota_img_states_t_ESP_OTA_IMG_INVALID
                    || state == sys::esp_ota_img_states_t_ESP_OTA_IMG_ABORTED)
            {
                return Err(format!("NothingToRollback: {} was marked invalid", label(other)));
            }
            // The note first: a reboot between the two leaves the old boot
            // slot selected and a stale note, which the next boot discards
            // because it is not running the slot the note names.
            let subtype = (*other).subtype as u8;
            with_nvs(|h| check(sys::nvs_set_u8(h, NVS_ROLLBACK_KEY.as_ptr(), subtype), "nvs_set_u8"))??;
            check(sys::esp_ota_set_boot_partition(other), "esp_ota_set_boot_partition")?;
            Ok(OtaArmed { slot: label(other), reboot: true })
        }
    }
}
