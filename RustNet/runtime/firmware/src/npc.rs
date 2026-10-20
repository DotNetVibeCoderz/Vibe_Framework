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
//! Executing loaded code is chip-gated. This build (feature `npc`, on for the
//! host/virtual device) loads the payload as a dynamic library. Chips without
//! a loader report `IsSupported() == false` and `Load` fails cleanly.
//! Signing gates *who* may load a module, not *what* it does: native code runs
//! unsandboxed and can fault the device.

#[cfg(feature = "npc")]
use rustnet_secureboot::{verify, ChipFamily, ImageKind};

/// Signature of every NPC procedure.
#[cfg(feature = "npc")]
type NpcProc = unsafe extern "C" fn(*const u8, u32, *mut u8, u32) -> i32;

/// Native modules and procedures loaded by the running app. Dropped (and the
/// libraries unloaded) when the app stops.
#[derive(Default)]
pub struct NpcTable {
    #[cfg(feature = "npc")]
    libs: Vec<(String, libloading::Library, std::path::PathBuf)>,
    #[cfg(feature = "npc")]
    procs: Vec<NpcProc>,
}

/// Largest result a procedure may return in one call.
pub const MAX_OUTPUT: usize = 1 << 20;

impl NpcTable {
    pub fn is_supported() -> bool {
        cfg!(feature = "npc")
    }

    /// Verify `container` (an RNSB image) against `key`, load it, and resolve
    /// `symbol`. Returns a handle for [`NpcTable::invoke`].
    #[cfg(feature = "npc")]
    pub fn load(
        &mut self,
        module: &str,
        container: &[u8],
        key: &[u8],
        chip: ChipFamily,
        symbol: &str,
    ) -> Result<i32, String> {
        let lib_index = match self.libs.iter().position(|(m, _, _)| m == module) {
            Some(i) => i,
            None => {
                let image = verify(container, key, chip)
                    .map_err(|e| format!("native module '{module}': signature check failed: {e}"))?;
                if image.kind != ImageKind::Native {
                    return Err(format!("'{module}' is not a native module image"));
                }
                // The OS loader needs a file. The name carries the process id
                // and module so concurrent virtual devices never collide.
                let ext = std::env::consts::DLL_EXTENSION;
                let path = std::env::temp_dir().join(format!(
                    "rustnet-npc-{}-{}-{}.{ext}",
                    std::process::id(),
                    self.libs.len(),
                    sanitize(module)
                ));
                std::fs::write(&path, image.payload).map_err(|e| e.to_string())?;
                // SAFETY: the image was signed by the device owner's key; running
                // its initialisers is exactly what loading it is for.
                let lib = unsafe { libloading::Library::new(&path) }
                    .map_err(|e| format!("native module '{module}': {e}"))?;
                self.libs.push((module.to_string(), lib, path));
                self.libs.len() - 1
            }
        };
        let lib = &self.libs[lib_index].1;
        let mut name = symbol.as_bytes().to_vec();
        name.push(0);
        // SAFETY: the NPC ABI fixes the procedure signature; a module that
        // exports a symbol with another shape is the module's bug, as with RLP.
        let proc: NpcProc = unsafe {
            *lib.get::<NpcProc>(&name)
                .map_err(|_| format!("native module '{module}' has no procedure '{symbol}'"))?
        };
        self.procs.push(proc);
        Ok(self.procs.len() as i32 - 1)
    }

    #[cfg(not(feature = "npc"))]
    pub fn load(
        &mut self,
        _module: &str,
        _container: &[u8],
        _key: &[u8],
        _chip: rustnet_secureboot::ChipFamily,
        _symbol: &str,
    ) -> Result<i32, String> {
        Err("NotSupported: this chip cannot execute loaded native code".into())
    }

    /// Call a loaded procedure with `args`; `out_cap` bounds the result.
    #[cfg(feature = "npc")]
    pub fn invoke(&mut self, handle: i32, args: &[u8], out_cap: usize) -> Result<Vec<u8>, String> {
        let proc = *self
            .procs
            .get(usize::try_from(handle).map_err(|_| "invalid NPC handle".to_string())?)
            .ok_or("invalid NPC handle")?;
        let cap = out_cap.min(MAX_OUTPUT);
        let mut out = vec![0u8; cap];
        // SAFETY: both buffers outlive the call and their lengths are passed
        // alongside them; the procedure is bound by the ABI not to exceed them.
        let n = unsafe { proc(args.as_ptr(), args.len() as u32, out.as_mut_ptr(), cap as u32) };
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

    #[cfg(not(feature = "npc"))]
    pub fn invoke(&mut self, _handle: i32, _args: &[u8], _out_cap: usize) -> Result<Vec<u8>, String> {
        Err("NotSupported: this chip cannot execute loaded native code".into())
    }
}

#[cfg(feature = "npc")]
impl Drop for NpcTable {
    fn drop(&mut self) {
        self.procs.clear();
        for (_, lib, path) in self.libs.drain(..) {
            drop(lib);
            let _ = std::fs::remove_file(path);
        }
    }
}

#[cfg(feature = "npc")]
fn sanitize(name: &str) -> String {
    name.chars().map(|c| if c.is_ascii_alphanumeric() { c } else { '_' }).collect()
}
