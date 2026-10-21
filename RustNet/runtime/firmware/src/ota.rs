//! Firmware-over-the-air, behind one interface so each chip can bring its own.
//!
//! The RNDP commands are the same everywhere — begin, stream data, end
//! (verify + arm the new image), confirm, rollback. What "arm" means is the
//! chip's business: the virtual device keeps both slots in RAM
//! ([`SimOta`]); an ESP32 streams the image into an `ota_x` partition and
//! lets the ESP-IDF bootloader do the A/B switch and the rollback.

use rustnet_ota::{MemSlots, OtaManager, Slot, SlotStorage};
use rustnet_secureboot::ChipFamily;

/// What [`OtaBackend::end`] did.
pub struct OtaArmed {
    /// Human-readable target ("B", "ota_1"), echoed to the tools.
    pub slot: String,
    /// The device must restart to run the new image (the transport reboots
    /// after sending the response).
    pub reboot: bool,
}

pub trait OtaBackend: Send {
    /// Start receiving an update. `pub_key` is the provisioned key the
    /// image must be signed with.
    fn begin(&mut self, pub_key: &[u8]) -> Result<(), String>;
    fn write(&mut self, chunk: &[u8]) -> Result<(), String>;
    /// Verify everything received and make it the next image to boot.
    fn end(&mut self) -> Result<OtaArmed, String>;
    /// The running image is healthy: stop any pending rollback.
    fn confirm(&mut self) -> Result<String, String>;
    /// Go back to the previous image.
    fn rollback(&mut self) -> Result<OtaArmed, String>;
}

/// The virtual device's OTA: both slots in RAM, the A/B state machine and
/// boot-attempt rollback from `rustnet-ota`. Nothing reboots.
pub struct SimOta {
    manager: Option<OtaManager<MemSlots>>,
    chip: ChipFamily,
}

impl SimOta {
    pub fn new(chip: ChipFamily) -> Self {
        Self { manager: None, chip }
    }

    fn manager(&mut self) -> Result<&mut OtaManager<MemSlots>, String> {
        // Confirm/rollback before any update are answered from the default
        // state (slot A active, nothing pending), as they always were.
        let chip = self.chip;
        Ok(self.manager.get_or_insert_with(|| OtaManager::new(MemSlots::new(), Vec::new(), chip)))
    }
}

impl OtaBackend for SimOta {
    fn begin(&mut self, pub_key: &[u8]) -> Result<(), String> {
        // Keep the slots across updates, but take the key current now.
        let storage = self.manager.take().map(|m| m.storage).unwrap_or_default();
        let mut m = OtaManager::new(storage, pub_key.to_vec(), self.chip);
        m.begin();
        self.manager = Some(m);
        Ok(())
    }

    fn write(&mut self, chunk: &[u8]) -> Result<(), String> {
        self.manager()?.write_chunk(chunk).map_err(|e| format!("{e:?}"))
    }

    fn end(&mut self) -> Result<OtaArmed, String> {
        let m = self.manager()?;
        m.finish().map_err(|e| format!("ota verify failed: {e:?}"))?;
        // Simulate the reboot into the new slot.
        let slot = m.on_boot();
        Ok(OtaArmed { slot: format!("{slot:?}"), reboot: false })
    }

    fn confirm(&mut self) -> Result<String, String> {
        let m = self.manager()?;
        m.confirm_boot();
        let active: Slot = m.storage.state().active;
        Ok(format!("{active:?}"))
    }

    fn rollback(&mut self) -> Result<OtaArmed, String> {
        let slot = self.manager()?.rollback().map_err(|e| format!("{e:?}"))?;
        Ok(OtaArmed { slot: format!("{slot:?}"), reboot: false })
    }
}
