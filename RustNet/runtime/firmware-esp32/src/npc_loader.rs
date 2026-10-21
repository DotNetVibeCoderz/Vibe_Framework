//! NPC on the ESP32: RNPC version-2 modules (`rustnet native build --target
//! xtensa-esp32-none-elf`) placed with their code in IRAM and their data in
//! DRAM.
//!
//! Two facts of the ESP32 shape this. Instruction RAM is executable but takes
//! only 32-bit loads and stores — a byte store faults — so the code is
//! relocated in a DRAM staging buffer and copied across a word at a time; and
//! for the same reason constant tables the code reads a byte at a time have to
//! stay in DRAM, which is why the module format keeps code and data apart.
//! The memory comes from ESP-IDF's capability allocator (`MALLOC_CAP_EXEC`).

use esp_idf_svc::sys;
use rustnet_firmware::npc::{LoadedModule, PlatformLoader};
use rustnet_npc::split::{SplitArch, SplitModule};

/// IRAM handed out by `heap_caps_malloc(MALLOC_CAP_EXEC)`, freed on drop.
struct Iram(*mut u32);

// The pointer is owned exclusively by the module it backs.
unsafe impl Send for Iram {}

impl Drop for Iram {
    fn drop(&mut self) {
        // SAFETY: allocated by heap_caps_malloc, freed once.
        unsafe { sys::heap_caps_free(self.0.cast()) };
    }
}

pub struct EspNpcLoader;

impl PlatformLoader for EspNpcLoader {
    fn load(&self, payload: &[u8]) -> Result<LoadedModule, String> {
        let m = SplitModule::parse(payload, SplitArch::XtensaEsp32).map_err(|e| e.to_string())?;
        let text_words = m.text.len().div_ceil(4).max(1);
        // SAFETY: plain allocation; checked for null below.
        let iram = unsafe {
            sys::heap_caps_malloc(text_words * 4, sys::MALLOC_CAP_EXEC | sys::MALLOC_CAP_32BIT)
        }
        .cast::<u32>();
        if iram.is_null() {
            return Err(format!("no {} bytes of executable IRAM free", text_words * 4));
        }
        let iram = Iram(iram);
        let mut data = vec![0u32; m.data_size().div_ceil(4).max(1)];
        let mut staging = vec![0u8; text_words * 4];
        let text_base = iram.0 as u32;
        let data_base = data.as_ptr() as u32;
        // SAFETY: a u32 buffer viewed as bytes of the same length.
        let data_bytes = unsafe { core::slice::from_raw_parts_mut(data.as_mut_ptr().cast::<u8>(), data.len() * 4) };
        m.load_into(&mut staging, text_base, data_bytes, data_base).map_err(|e| e.to_string())?;
        for (i, w) in staging.chunks_exact(4).enumerate() {
            // SAFETY: inside the allocation; IRAM takes aligned word stores only.
            unsafe { core::ptr::write_volatile(iram.0.add(i), u32::from_le_bytes([w[0], w[1], w[2], w[3]])) };
        }
        // Make the stores visible to instruction fetch before anything jumps
        // there (a full fence is `memw` on Xtensa; inline asm is not stable here).
        core::sync::atomic::fence(core::sync::atomic::Ordering::SeqCst);
        let procedures = m.exports.iter().map(|(n, off)| (n.clone(), text_base as usize + *off as usize)).collect();
        Ok(LoadedModule { procedures, memory: Box::new((iram, data)) })
    }
}
