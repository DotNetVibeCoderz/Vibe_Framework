//! Filesystem layer with a `System.IO`-shaped API surface.
//!
//! - [`Vfs`] — the trait everything programs against (also the shape of the
//!   C# `RustNet.IO.FileSystem` intrinsics).
//! - [`MemFs`] — RAM-backed FS for host/dev and small devices.
//! - [`FatVolume`] — FAT12/16/32 over any block medium (SD card, SPI
//!   flash, or a backing file on the host) via the `fatfs` crate.
//! - [`EncryptedFs`] — transparent AES-CTR encryption overlay for any Vfs.

use std::collections::BTreeMap;
use std::io::{Read, Seek, Write};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FsError {
    NotFound(String),
    AlreadyExists(String),
    NotADirectory(String),
    Io(String),
}

impl core::fmt::Display for FsError {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            FsError::NotFound(p) => write!(f, "not found: {p}"),
            FsError::AlreadyExists(p) => write!(f, "already exists: {p}"),
            FsError::NotADirectory(p) => write!(f, "not a directory: {p}"),
            FsError::Io(m) => write!(f, "io error: {m}"),
        }
    }
}

impl std::error::Error for FsError {}

pub type FsResult<T> = Result<T, FsError>;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DirEntry {
    pub name: String,
    pub is_dir: bool,
    pub size: u64,
}

/// Minimal virtual filesystem contract (paths use `/`).
pub trait Vfs: Send {
    fn read(&mut self, path: &str) -> FsResult<Vec<u8>>;
    fn write(&mut self, path: &str, data: &[u8]) -> FsResult<()>;
    fn append(&mut self, path: &str, data: &[u8]) -> FsResult<()>;
    fn delete(&mut self, path: &str) -> FsResult<()>;
    fn exists(&mut self, path: &str) -> bool;
    fn list(&mut self, path: &str) -> FsResult<Vec<DirEntry>>;
    fn mkdir(&mut self, path: &str) -> FsResult<()>;
}

fn normalize(path: &str) -> String {
    let mut out = String::from("/");
    for part in path.split('/').filter(|p| !p.is_empty() && *p != ".") {
        if !out.ends_with('/') {
            out.push('/');
        }
        out.push_str(part);
    }
    out
}

// ------------------------------------------------------------------
// MemFs
// ------------------------------------------------------------------

#[derive(Default)]
pub struct MemFs {
    files: BTreeMap<String, Vec<u8>>,
    dirs: std::collections::BTreeSet<String>,
}

impl MemFs {
    pub fn new() -> Self {
        let mut fs = Self::default();
        fs.dirs.insert("/".into());
        fs
    }

    fn parent_exists(&self, path: &str) -> bool {
        match path.rfind('/') {
            Some(0) | None => true,
            Some(i) => self.dirs.contains(&path[..i]),
        }
    }
}

impl Vfs for MemFs {
    fn read(&mut self, path: &str) -> FsResult<Vec<u8>> {
        let p = normalize(path);
        self.files.get(&p).cloned().ok_or(FsError::NotFound(p))
    }

    fn write(&mut self, path: &str, data: &[u8]) -> FsResult<()> {
        let p = normalize(path);
        if !self.parent_exists(&p) {
            return Err(FsError::NotFound(format!("parent of {p}")));
        }
        self.files.insert(p, data.to_vec());
        Ok(())
    }

    fn append(&mut self, path: &str, data: &[u8]) -> FsResult<()> {
        let p = normalize(path);
        if !self.parent_exists(&p) {
            return Err(FsError::NotFound(format!("parent of {p}")));
        }
        self.files.entry(p).or_default().extend_from_slice(data);
        Ok(())
    }

    fn delete(&mut self, path: &str) -> FsResult<()> {
        let p = normalize(path);
        if self.files.remove(&p).is_some() {
            return Ok(());
        }
        if self.dirs.remove(&p) {
            let prefix = format!("{p}/");
            self.files.retain(|k, _| !k.starts_with(&prefix));
            self.dirs.retain(|k| !k.starts_with(&prefix));
            return Ok(());
        }
        Err(FsError::NotFound(p))
    }

    fn exists(&mut self, path: &str) -> bool {
        let p = normalize(path);
        self.files.contains_key(&p) || self.dirs.contains(&p)
    }

    fn list(&mut self, path: &str) -> FsResult<Vec<DirEntry>> {
        let p = normalize(path);
        if !self.dirs.contains(&p) {
            return Err(FsError::NotADirectory(p));
        }
        let prefix = if p == "/" { "/".to_string() } else { format!("{p}/") };
        let mut out = Vec::new();
        for (name, data) in &self.files {
            if let Some(rest) = name.strip_prefix(&prefix) {
                if !rest.contains('/') {
                    out.push(DirEntry { name: rest.to_string(), is_dir: false, size: data.len() as u64 });
                }
            }
        }
        for dir in &self.dirs {
            if let Some(rest) = dir.strip_prefix(&prefix) {
                if !rest.is_empty() && !rest.contains('/') {
                    out.push(DirEntry { name: rest.to_string(), is_dir: true, size: 0 });
                }
            }
        }
        Ok(out)
    }

    fn mkdir(&mut self, path: &str) -> FsResult<()> {
        let p = normalize(path);
        if self.dirs.contains(&p) {
            return Err(FsError::AlreadyExists(p));
        }
        // mkdir -p semantics: create intermediate directories.
        let mut cur = String::new();
        for part in p.split('/').filter(|s| !s.is_empty()) {
            cur.push('/');
            cur.push_str(part);
            self.dirs.insert(cur.clone());
        }
        Ok(())
    }
}

// ------------------------------------------------------------------
// FAT volume over any Read+Write+Seek medium
// ------------------------------------------------------------------

pub struct FatVolume<T: Read + Write + Seek + Send> {
    fs: fatfs::FileSystem<T>,
}

// SAFETY: the non-Send members of fatfs::FileSystem are its default
// stateless code-page converter and time provider; the IO medium itself
// is constrained to Send.
unsafe impl<T: Read + Write + Seek + Send> Send for FatVolume<T> {}

impl<T: Read + Write + Seek + Send> FatVolume<T> {
    /// Mount an existing FAT filesystem.
    pub fn mount(medium: T) -> FsResult<Self> {
        let fs = fatfs::FileSystem::new(medium, fatfs::FsOptions::new())
            .map_err(|e| FsError::Io(format!("mount: {e:?}")))?;
        Ok(Self { fs })
    }

    pub fn volume_label(&self) -> String {
        self.fs.volume_label()
    }
}

/// Format a medium as FAT (helper for tests/tools; SD cards come preformatted).
pub fn format_fat<T: Read + Write + Seek>(medium: &mut T) -> FsResult<()> {
    fatfs::format_volume(medium, fatfs::FormatVolumeOptions::new())
        .map_err(|e| FsError::Io(format!("format: {e:?}")))
}

impl<T: Read + Write + Seek + Send> Vfs for FatVolume<T> {
    fn read(&mut self, path: &str) -> FsResult<Vec<u8>> {
        let root = self.fs.root_dir();
        let mut file = root
            .open_file(path.trim_start_matches('/'))
            .map_err(|_| FsError::NotFound(path.into()))?;
        let mut buf = Vec::new();
        file.read_to_end(&mut buf).map_err(|e| FsError::Io(e.to_string()))?;
        Ok(buf)
    }

    fn write(&mut self, path: &str, data: &[u8]) -> FsResult<()> {
        let root = self.fs.root_dir();
        let mut file = root
            .create_file(path.trim_start_matches('/'))
            .map_err(|e| FsError::Io(format!("create: {e:?}")))?;
        file.truncate().map_err(|e| FsError::Io(e.to_string()))?;
        file.write_all(data).map_err(|e| FsError::Io(e.to_string()))?;
        file.flush().map_err(|e| FsError::Io(e.to_string()))?;
        Ok(())
    }

    fn append(&mut self, path: &str, data: &[u8]) -> FsResult<()> {
        let root = self.fs.root_dir();
        let mut file = root
            .create_file(path.trim_start_matches('/'))
            .map_err(|e| FsError::Io(format!("open: {e:?}")))?;
        file.seek(std::io::SeekFrom::End(0)).map_err(|e| FsError::Io(e.to_string()))?;
        file.write_all(data).map_err(|e| FsError::Io(e.to_string()))?;
        file.flush().map_err(|e| FsError::Io(e.to_string()))?;
        Ok(())
    }

    fn delete(&mut self, path: &str) -> FsResult<()> {
        self.fs
            .root_dir()
            .remove(path.trim_start_matches('/'))
            .map_err(|_| FsError::NotFound(path.into()))
    }

    fn exists(&mut self, path: &str) -> bool {
        self.fs.root_dir().open_file(path.trim_start_matches('/')).is_ok()
            || self.fs.root_dir().open_dir(path.trim_start_matches('/')).is_ok()
    }

    fn list(&mut self, path: &str) -> FsResult<Vec<DirEntry>> {
        let root = self.fs.root_dir();
        let trimmed = path.trim_start_matches('/');
        let dir = if trimmed.is_empty() {
            root
        } else {
            root.open_dir(trimmed).map_err(|_| FsError::NotADirectory(path.into()))?
        };
        let mut out = Vec::new();
        for entry in dir.iter() {
            let entry = entry.map_err(|e| FsError::Io(e.to_string()))?;
            let name = entry.file_name();
            if name == "." || name == ".." {
                continue;
            }
            out.push(DirEntry { name, is_dir: entry.is_dir(), size: entry.len() });
        }
        Ok(out)
    }

    fn mkdir(&mut self, path: &str) -> FsResult<()> {
        self.fs
            .root_dir()
            .create_dir(path.trim_start_matches('/'))
            .map(|_| ())
            .map_err(|e| FsError::Io(format!("mkdir: {e:?}")))
    }
}

// ------------------------------------------------------------------
// Encrypted overlay
// ------------------------------------------------------------------

/// Transparent AES-CTR encryption for any inner filesystem. The per-file
/// nonce derives from the path so content-equal files still encrypt
/// differently at different locations.
pub struct EncryptedFs<F: Vfs> {
    inner: F,
    key: Vec<u8>,
}

impl<F: Vfs> EncryptedFs<F> {
    pub fn new(inner: F, key: Vec<u8>) -> Self {
        Self { inner, key }
    }

    fn nonce_for(path: &str) -> [u8; 16] {
        let h = rustnet_crypto::sha256(path.as_bytes());
        h[..16].try_into().unwrap()
    }

    fn apply(&self, path: &str, data: &mut [u8]) -> FsResult<()> {
        rustnet_crypto::aes_ctr_apply(&self.key, &Self::nonce_for(path), data)
            .map_err(|e| FsError::Io(e.to_string()))
    }
}

impl<F: Vfs> Vfs for EncryptedFs<F> {
    fn read(&mut self, path: &str) -> FsResult<Vec<u8>> {
        let mut data = self.inner.read(path)?;
        self.apply(path, &mut data)?;
        Ok(data)
    }

    fn write(&mut self, path: &str, data: &[u8]) -> FsResult<()> {
        let mut enc = data.to_vec();
        self.apply(path, &mut enc)?;
        self.inner.write(path, &enc)
    }

    fn append(&mut self, path: &str, data: &[u8]) -> FsResult<()> {
        // CTR keystream position depends on offset: re-encrypt whole file.
        let mut current = match self.read(path) {
            Ok(d) => d,
            Err(FsError::NotFound(_)) => Vec::new(),
            Err(e) => return Err(e),
        };
        current.extend_from_slice(data);
        self.write(path, &current)
    }

    fn delete(&mut self, path: &str) -> FsResult<()> {
        self.inner.delete(path)
    }
    fn exists(&mut self, path: &str) -> bool {
        self.inner.exists(path)
    }
    fn list(&mut self, path: &str) -> FsResult<Vec<DirEntry>> {
        self.inner.list(path)
    }
    fn mkdir(&mut self, path: &str) -> FsResult<()> {
        self.inner.mkdir(path)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn memfs_crud_and_listing() {
        let mut fs = MemFs::new();
        fs.mkdir("/data").unwrap();
        fs.write("/data/config.json", b"{}").unwrap();
        fs.append("/data/log.txt", b"line1\n").unwrap();
        fs.append("/data/log.txt", b"line2\n").unwrap();
        assert_eq!(fs.read("/data/log.txt").unwrap(), b"line1\nline2\n");
        assert!(fs.exists("/data/config.json"));
        let mut names: Vec<String> = fs.list("/data").unwrap().into_iter().map(|e| e.name).collect();
        names.sort();
        assert_eq!(names, vec!["config.json", "log.txt"]);
        fs.delete("/data/config.json").unwrap();
        assert!(!fs.exists("/data/config.json"));
        assert_eq!(fs.read("/missing"), Err(FsError::NotFound("/missing".into())));
    }

    #[test]
    fn memfs_recursive_dir_delete() {
        let mut fs = MemFs::new();
        fs.mkdir("/a/b").unwrap();
        fs.write("/a/b/f.txt", b"x").unwrap();
        fs.delete("/a").unwrap();
        assert!(!fs.exists("/a/b/f.txt"));
        assert!(!fs.exists("/a/b"));
    }

    #[test]
    fn fat_volume_roundtrip() {
        // 1 MiB in-memory "SD card"
        let mut medium = Cursor::new(vec![0u8; 1024 * 1024]);
        format_fat(&mut medium).unwrap();
        let mut vol = FatVolume::mount(medium).unwrap();
        vol.write("/hello.txt", b"hello from FAT").unwrap();
        vol.mkdir("/logs").unwrap();
        vol.write("/logs/day1.txt", b"log line").unwrap();
        assert_eq!(vol.read("/hello.txt").unwrap(), b"hello from FAT");
        assert!(vol.exists("/logs/day1.txt"));
        let names: Vec<String> = vol.list("/").unwrap().into_iter().map(|e| e.name).collect();
        assert!(names.contains(&"hello.txt".to_string()));
        assert!(names.contains(&"logs".to_string()));
        vol.delete("/hello.txt").unwrap();
        assert!(!vol.exists("/hello.txt"));
    }

    #[test]
    fn encrypted_fs_roundtrip_and_ciphertext_differs() {
        let key = vec![0x42u8; 32];
        let mut fs = EncryptedFs::new(MemFs::new(), key.clone());
        fs.write("/secret.txt", b"api-key-12345").unwrap();
        assert_eq!(fs.read("/secret.txt").unwrap(), b"api-key-12345");
        // Underlying bytes must NOT be plaintext.
        let mut raw = MemFs::new();
        std::mem::swap(&mut raw, {
            // reach inside: rebuild a plain view by decrypt-disabled read
            &mut fs.inner
        });
        let stored = raw.read("/secret.txt").unwrap();
        assert_ne!(stored, b"api-key-12345".to_vec());
        // Appending stays consistent.
        let mut fs2 = EncryptedFs::new(raw, key);
        fs2.append("/secret.txt", b"-more").unwrap();
        assert_eq!(fs2.read("/secret.txt").unwrap(), b"api-key-12345-more");
    }
}
