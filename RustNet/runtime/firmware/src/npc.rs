//! NPC — the Native Procedure Caller (RustNet's analogue of TinyCLR's RLP).
//!
//! A C# app calls native code that was loaded onto the device at run time,
//! with no firmware rebuild. A native module travels as an RNSB container of
//! kind [`ImageKind::Native`], pushed to `/data/native/<module>.rnsb`; it is
//! verified against the provisioned key **when it is loaded**, not when it is
//! uploaded, so the data channel that carries it needs no trust of its own.
//!
//! The ABI is one C-callable shape for every exported procedure:
//!
//! ```text
//! extern "C" fn(args: *const u8, args_len: u32, out: *mut u8, out_cap: u32) -> i32
//! ```
//!
//! returning the number of bytes written to `out`, or a negative error code.
//! It matches the byte-array channel every other host call already uses.
//!
//! How a module becomes callable code is the platform's business:
//! - the host/virtual device (feature `npc`) loads it as a dynamic library;
//! - a chip without a dynamic loader installs a [`PlatformLoader`] at boot
//!   that places an RNPC module (`runtime/rustnet-npc`) in executable RAM —
//!   the ESP32 firmware does, into IRAM;
//! - anything else answers `IsSupported() == false` and `Load` fails cleanly.
//!
//! Signing gates *who* may load a module, not *what* it does: native code
//! runs unsandboxed and can fault the device.

use rustnet_secureboot::{verify, ChipFamily, ImageKind};
use std::sync::OnceLock;

/// Signature of every NPC procedure.
type NpcProc = unsafe extern "C" fn(*const u8, u32, *mut u8, u32) -> i32;

/// A module placed in executable memory by a [`PlatformLoader`].
pub struct LoadedModule {
    /// Each exported procedure's entry address.
    pub procedures: Vec<(String, usize)>,
    /// Whatever owns the module's memory; dropped when the app stops.
    pub memory: Box<dyn std::any::Any + Send>,
}

/// A chip's loader for verified RNPC payloads.
pub trait PlatformLoader: Send + Sync {
    fn load(&self, payload: &[u8]) -> Result<LoadedModule, String>;
}

static PLATFORM: OnceLock<Box<dyn PlatformLoader>> = OnceLock::new();

/// Install this chip's loader (once, at boot).
pub fn set_platform_loader(loader: Box<dyn PlatformLoader>) {
    let _ = PLATFORM.set(loader);
}

enum Loaded {
    #[cfg(feature = "npc")]
    Library(libloading::Library, std::path::PathBuf),
    // The host loads libraries; only chips without a dynamic loader place modules.
    #[cfg_attr(feature = "npc", allow(dead_code))]
    Platform(LoadedModule),
}

/// Native modules and procedures loaded by the running app. Dropped (and the
/// modules unloaded) when the app stops.
#[derive(Default)]
pub struct NpcTable {
    modules: Vec<(String, Loaded)>,
    procs: Vec<usize>,
}

/// Largest result a procedure may return in one call.
pub const MAX_OUTPUT: usize = 1 << 20;

impl NpcTable {
    pub fn is_supported() -> bool {
        cfg!(feature = "npc") || PLATFORM.get().is_some()
    }

    /// Verify `container` (an RNSB image) against `key`, load it, and resolve
    /// `symbol`. Returns a handle for [`NpcTable::invoke`].
    pub fn load(
        &mut self,
        module: &str,
        container: &[u8],
        key: &[u8],
        chip: ChipFamily,
        symbol: &str,
    ) -> Result<i32, String> {
        if !Self::is_supported() {
            return Err("NotSupported: this chip cannot execute loaded native code".into());
        }
        let index = match self.modules.iter().position(|(m, _)| m == module) {
            Some(i) => i,
            None => {
                let image = verify(container, key, chip)
                    .map_err(|e| format!("native module '{module}': signature check failed: {e}"))?;
                if image.kind != ImageKind::Native {
                    return Err(format!("'{module}' is not a native module image"));
                }
                let loaded = Self::place(module, image.payload, self.modules.len())?;
                self.modules.push((module.to_string(), loaded));
                self.modules.len() - 1
            }
        };
        let addr = match &self.modules[index].1 {
            #[cfg(feature = "npc")]
            Loaded::Library(lib, _) => {
                let mut name = symbol.as_bytes().to_vec();
                name.push(0);
                // SAFETY: the NPC ABI fixes the procedure signature; a module
                // that exports a symbol with another shape is the module's bug.
                let proc: NpcProc = unsafe {
                    *lib.get::<NpcProc>(&name)
                        .map_err(|_| format!("native module '{module}' has no procedure '{symbol}'"))?
                };
                proc as usize
            }
            Loaded::Platform(m) => m
                .procedures
                .iter()
                .find(|(n, _)| n == symbol)
                .map(|(_, a)| *a)
                .ok_or_else(|| format!("native module '{module}' has no procedure '{symbol}'"))?,
        };
        self.procs.push(addr);
        Ok(self.procs.len() as i32 - 1)
    }

    #[cfg(feature = "npc")]
    fn place(module: &str, payload: &[u8], n: usize) -> Result<Loaded, String> {
        // The OS loader needs a file. The name carries the process id and
        // module so concurrent virtual devices never collide.
        let ext = std::env::consts::DLL_EXTENSION;
        let path = std::env::temp_dir().join(format!(
            "rustnet-npc-{}-{}-{}.{ext}",
            std::process::id(),
            n,
            sanitize(module)
        ));
        std::fs::write(&path, payload).map_err(|e| e.to_string())?;
        // SAFETY: the image was signed by the device owner's key; running its
        // initialisers is exactly what loading it is for.
        let lib = unsafe { libloading::Library::new(&path) }.map_err(|e| format!("native module '{module}': {e}"))?;
        Ok(Loaded::Library(lib, path))
    }

    #[cfg(not(feature = "npc"))]
    fn place(module: &str, payload: &[u8], _n: usize) -> Result<Loaded, String> {
        let loader = PLATFORM.get().ok_or("NotSupported: this chip cannot execute loaded native code")?;
        let m = loader.load(payload).map_err(|e| format!("native module '{module}': {e}"))?;
        Ok(Loaded::Platform(m))
    }

    /// Call a loaded procedure with `args`; `out_cap` bounds the result.
    pub fn invoke(&mut self, handle: i32, args: &[u8], out_cap: usize) -> Result<Vec<u8>, String> {
        let addr = *self
            .procs
            .get(usize::try_from(handle).map_err(|_| "invalid NPC handle".to_string())?)
            .ok_or("invalid NPC handle")?;
        let cap = out_cap.min(MAX_OUTPUT);
        let mut out = vec![0u8; cap];
        // SAFETY: `addr` is an entry point of a verified, loaded module; both
        // buffers outlive the call and their lengths travel with them, as the
        // NPC ABI requires.
        let n = unsafe {
            let proc: NpcProc = std::mem::transmute::<usize, NpcProc>(addr);
            proc(args.as_ptr(), args.len() as u32, out.as_mut_ptr(), cap as u32)
        };
        if n < 0 {
            return Err(format!("native procedure failed with code {n}"));
        }
        let n = n as usize;
        if n > cap {
            return Err(format!("native procedure reported {n} bytes but the buffer holds {cap}"));
        }
        out.truncate(n);
        Ok(out)
    }
}

impl Drop for NpcTable {
    fn drop(&mut self) {
        self.procs.clear();
        for (_, loaded) in self.modules.drain(..) {
            match loaded {
                #[cfg(feature = "npc")]
                Loaded::Library(lib, path) => {
                    drop(lib);
                    let _ = std::fs::remove_file(path);
                }
                Loaded::Platform(m) => drop(m),
            }
        }
    }
}

#[cfg(feature = "npc")]
fn sanitize(name: &str) -> String {
    name.chars().map(|c| if c.is_ascii_alphanumeric() { c } else { '_' }).collect()
}
