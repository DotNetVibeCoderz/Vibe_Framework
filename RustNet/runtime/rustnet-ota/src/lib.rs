//! OTA update engine with A/B slots and automatic rollback.
//!
//! Flow: tooling streams a sealed image (`rustnet-secureboot` container)
//! into the inactive slot chunk by chunk → `finish` verifies the signature
//! and marks the slot pending → device reboots into it → the new firmware
//! calls `confirm_boot` once healthy. If the device reboots `MAX_ATTEMPTS`
//! times without confirming, `on_boot` rolls back to the previous slot.

use rustnet_secureboot::{verify, BootError, ChipFamily};

pub const MAX_ATTEMPTS: u8 = 3;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Slot {
    A,
    B,
}

impl Slot {
    pub fn other(self) -> Slot {
        match self {
            Slot::A => Slot::B,
            Slot::B => Slot::A,
        }
    }
    pub fn index(self) -> usize {
        match self {
            Slot::A => 0,
            Slot::B => 1,
        }
    }
}

/// Persistent state the bootloader reads. On MCUs this lives in a flash
/// sector; the trait lets firmware/host provide the medium.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OtaState {
    pub active: Slot,
    /// Slot that was just flashed and awaits its first successful boot.
    pub pending: Option<Slot>,
    pub boot_attempts: u8,
}

impl Default for OtaState {
    fn default() -> Self {
        Self { active: Slot::A, pending: None, boot_attempts: 0 }
    }
}

pub trait SlotStorage {
    fn read(&self, slot: Slot) -> Vec<u8>;
    fn write(&mut self, slot: Slot, data: &[u8]);
    fn append(&mut self, slot: Slot, chunk: &[u8]);
    fn state(&self) -> OtaState;
    fn set_state(&mut self, state: OtaState);
}

/// In-memory storage (host firmware + tests).
#[derive(Default)]
pub struct MemSlots {
    slots: [Vec<u8>; 2],
    state: OtaState,
}

impl MemSlots {
    pub fn new() -> Self {
        Self::default()
    }
}

impl SlotStorage for MemSlots {
    fn read(&self, slot: Slot) -> Vec<u8> {
        self.slots[slot.index()].clone()
    }
    fn write(&mut self, slot: Slot, data: &[u8]) {
        self.slots[slot.index()] = data.to_vec();
    }
    fn append(&mut self, slot: Slot, chunk: &[u8]) {
        self.slots[slot.index()].extend_from_slice(chunk);
    }
    fn state(&self) -> OtaState {
        self.state
    }
    fn set_state(&mut self, state: OtaState) {
        self.state = state;
    }
}

#[derive(Debug, PartialEq)]
pub enum OtaError {
    UpdateInProgressRequired,
    Verify(BootError),
    NothingToRollback,
}

pub struct OtaManager<S: SlotStorage> {
    pub storage: S,
    pub_key_der: Vec<u8>,
    chip: ChipFamily,
    receiving: bool,
}

impl<S: SlotStorage> OtaManager<S> {
    pub fn new(storage: S, pub_key_der: Vec<u8>, chip: ChipFamily) -> Self {
        Self { storage, pub_key_der, chip, receiving: false }
    }

    pub fn target_slot(&self) -> Slot {
        self.storage.state().active.other()
    }

    /// Start receiving an update into the inactive slot.
    pub fn begin(&mut self) {
        let target = self.target_slot();
        self.storage.write(target, &[]);
        self.receiving = true;
    }

    pub fn write_chunk(&mut self, chunk: &[u8]) -> Result<(), OtaError> {
        if !self.receiving {
            return Err(OtaError::UpdateInProgressRequired);
        }
        self.storage.append(self.target_slot(), chunk);
        Ok(())
    }

    /// Verify the received image and mark the slot pending activation.
    pub fn finish(&mut self) -> Result<(), OtaError> {
        if !self.receiving {
            return Err(OtaError::UpdateInProgressRequired);
        }
        self.receiving = false;
        let target = self.target_slot();
        let image = self.storage.read(target);
        verify(&image, &self.pub_key_der, self.chip).map_err(OtaError::Verify)?;
        let mut st = self.storage.state();
        st.pending = Some(target);
        st.boot_attempts = 0;
        self.storage.set_state(st);
        Ok(())
    }

    /// Bootloader entry: decides which slot to boot and handles rollback.
    /// Returns the slot to boot from.
    pub fn on_boot(&mut self) -> Slot {
        let mut st = self.storage.state();
        if let Some(pending) = st.pending {
            if st.boot_attempts >= MAX_ATTEMPTS {
                // New image never confirmed: roll back.
                st.pending = None;
                st.boot_attempts = 0;
                self.storage.set_state(st);
                return st.active;
            }
            st.boot_attempts += 1;
            self.storage.set_state(st);
            return pending;
        }
        st.active
    }

    /// Called by the new firmware once it considers itself healthy.
    pub fn confirm_boot(&mut self) {
        let mut st = self.storage.state();
        if let Some(pending) = st.pending.take() {
            st.active = pending;
            st.boot_attempts = 0;
            self.storage.set_state(st);
        }
    }

    /// Explicit rollback (user-requested).
    pub fn rollback(&mut self) -> Result<Slot, OtaError> {
        let mut st = self.storage.state();
        if st.pending.is_some() {
            st.pending = None;
            st.boot_attempts = 0;
            self.storage.set_state(st);
            return Ok(st.active);
        }
        Err(OtaError::NothingToRollback)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use rsa::pkcs1::{EncodeRsaPrivateKey, EncodeRsaPublicKey};
    use rsa::RsaPrivateKey;
    use rustnet_secureboot::{seal, ImageKind};

    fn setup() -> (OtaManager<MemSlots>, Vec<u8>) {
        let key = RsaPrivateKey::new(&mut rand::thread_rng(), 2048).unwrap();
        let priv_der = key.to_pkcs1_der().unwrap().as_bytes().to_vec();
        let pub_der = key.to_public_key().to_pkcs1_der().unwrap().as_bytes().to_vec();
        (OtaManager::new(MemSlots::new(), pub_der, ChipFamily::HostSim), priv_der)
    }

    fn sealed(priv_der: &[u8], content: &[u8]) -> Vec<u8> {
        seal(ImageKind::Firmware, ChipFamily::HostSim, content, priv_der).unwrap()
    }

    #[test]
    fn full_update_flow_with_confirmation() {
        let (mut ota, priv_der) = setup();
        let image = sealed(&priv_der, b"firmware v2");
        ota.begin();
        for chunk in image.chunks(7) {
            ota.write_chunk(chunk).unwrap();
        }
        ota.finish().unwrap();
        // Reboot boots the pending slot B.
        assert_eq!(ota.on_boot(), Slot::B);
        ota.confirm_boot();
        assert_eq!(ota.storage.state().active, Slot::B);
        assert_eq!(ota.storage.state().pending, None);
        // Subsequent boots stay on B.
        assert_eq!(ota.on_boot(), Slot::B);
    }

    #[test]
    fn corrupted_update_rejected() {
        let (mut ota, priv_der) = setup();
        let mut image = sealed(&priv_der, b"firmware v2");
        let n = image.len();
        image[n / 2] ^= 0x55;
        ota.begin();
        ota.write_chunk(&image).unwrap();
        assert!(matches!(ota.finish(), Err(OtaError::Verify(_))));
        // Active slot unchanged; no pending.
        assert_eq!(ota.storage.state().active, Slot::A);
        assert_eq!(ota.storage.state().pending, None);
    }

    #[test]
    fn unconfirmed_boot_rolls_back() {
        let (mut ota, priv_der) = setup();
        let image = sealed(&priv_der, b"broken firmware");
        ota.begin();
        ota.write_chunk(&image).unwrap();
        ota.finish().unwrap();
        // Device keeps crashing before confirm_boot.
        for _ in 0..MAX_ATTEMPTS {
            assert_eq!(ota.on_boot(), Slot::B);
        }
        // Attempts exhausted: back to A.
        assert_eq!(ota.on_boot(), Slot::A);
        assert_eq!(ota.storage.state().active, Slot::A);
        assert_eq!(ota.storage.state().pending, None);
    }

    #[test]
    fn chunk_without_begin_errors() {
        let (mut ota, _) = setup();
        assert_eq!(ota.write_chunk(b"x"), Err(OtaError::UpdateInProgressRequired));
    }
}
