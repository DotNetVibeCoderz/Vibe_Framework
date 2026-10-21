//! NPC on the Meadow: native procedures loaded at run time into SRAM.
//!
//! A module arrives as an RNSB container of kind `Native` (pushed with
//! `rustnet native push`, stored in QSPI under `native/<name>.rnsb`), is
//! verified against the provisioned key when an app loads it, and its RNPC
//! payload (`rustnet native build --target thumbv7em-none-eabihf`) is placed in
//! heap memory by `rustnet-npc` and called through the NPC ABI.
//!
//! The heap sits in SRAM1/SRAM2 on the AXI bus (see memory.x), which the core
//! can execute from; DTCM, which it cannot, is deliberately not in the heap.
//! The data cache is off on this port, so a barrier after the copy is all the
//! core needs before jumping into fresh code — enabling the caches would mean
//! cleaning the D-cache and invalidating the I-cache over the module here.

use alloc::format;
use alloc::string::String;
use alloc::vec;
use alloc::vec::Vec;

use rustnet_npc::{Arch, Module};
use rustnet_secureboot::{verify, ChipFamily, ImageKind};

type NpcProc = unsafe extern "C" fn(*const u8, u32, *mut u8, u32) -> i32;

/// Largest result one call may return.
const MAX_OUTPUT: usize = 64 * 1024;

struct Placed {
    name: String,
    /// The module's code and data, word-aligned.
    memory: Vec<u32>,
    exports: Vec<(String, u32)>,
}

#[derive(Default)]
pub struct NpcTable {
    modules: Vec<Placed>,
    procs: Vec<u32>,
}

impl NpcTable {
    /// Verify `container`, place its module (once per name) and resolve `symbol`.
    pub fn load(&mut self, name: &str, container: &[u8], key: &[u8], symbol: &str) -> Result<i32, String> {
        let index = match self.modules.iter().position(|m| m.name == name) {
            Some(i) => i,
            None => {
                let image = verify(container, key, ChipFamily::Stm32)
                    .map_err(|e| format!("native module '{name}': signature check failed: {e}"))?;
                if image.kind != ImageKind::Native {
                    return Err(format!("'{name}' is not a native module image"));
                }
                let module = Module::parse(image.payload, Arch::ThumbV7EM)
                    .map_err(|e| format!("native module '{name}': {e}"))?;
                if module.align > 4 {
                    return Err(format!("native module '{name}' asks for {}-byte alignment", module.align));
                }
                let words = module.load_size().div_ceil(4);
                let mut memory = Vec::new();
                memory
                    .try_reserve_exact(words)
                    .map_err(|_| format!("native module '{name}': no {} bytes of heap free", words * 4))?;
                memory.resize(words, 0u32);
                let base = memory.as_mut_ptr() as u32;
                // SAFETY: a u32 buffer viewed as bytes, same length in bytes.
                let bytes = unsafe { core::slice::from_raw_parts_mut(memory.as_mut_ptr().cast::<u8>(), memory.len() * 4) };
                module.load_into(bytes, base).map_err(|e| format!("native module '{name}': {e}"))?;
                cortex_m::asm::dsb();
                cortex_m::asm::isb();
                self.modules.push(Placed { name: String::from(name), memory, exports: module.exports });
                self.modules.len() - 1
            }
        };
        let placed = &self.modules[index];
        let offset = placed
            .exports
            .iter()
            .find(|(n, _)| n == symbol)
            .map(|(_, off)| *off)
            .ok_or_else(|| format!("native module '{name}' has no procedure '{symbol}'"))?;
        self.procs.push(placed.memory.as_ptr() as u32 + offset);
        Ok(self.procs.len() as i32 - 1)
    }

    pub fn invoke(&mut self, handle: i32, args: &[u8], out_cap: usize) -> Result<Vec<u8>, String> {
        let addr = *usize::try_from(handle)
            .ok()
            .and_then(|h| self.procs.get(h))
            .ok_or_else(|| String::from("invalid NPC handle"))?;
        let cap = out_cap.min(MAX_OUTPUT);
        let mut out = vec![0u8; cap];
        // SAFETY: `addr` is a Thumb entry point inside a verified, loaded
        // module (the export carries the Thumb bit); the buffers outlive the
        // call and their lengths go with them, as the NPC ABI requires.
        let n = unsafe {
            let f: NpcProc = core::mem::transmute(addr as usize);
            f(args.as_ptr(), args.len() as u32, out.as_mut_ptr(), cap as u32)
        };
        if n < 0 {
            return Err(format!("native procedure failed with code {n}"));
        }
        if n as usize > cap {
            return Err(format!("native procedure reported {n} bytes but the buffer holds {cap}"));
        }
        out.truncate(n as usize);
        Ok(out)
    }
}
