//! Directory-backed `Vfs`: the virtual device persists under a host
//! directory; the ESP32 firmware reuses it over a mounted FAT partition.

use rustnet_fs::{DirEntry, FsError, FsResult, Vfs};
use std::path::PathBuf;

/// Directory-backed Vfs so the virtual device persists across restarts.
pub struct DirFs {
    root: PathBuf,
}

impl DirFs {
    pub fn new(root: PathBuf) -> std::io::Result<Self> {
        std::fs::create_dir_all(&root)?;
        Ok(Self { root })
    }

    fn resolve(&self, path: &str) -> PathBuf {
        let mut p = self.root.clone();
        for part in path.split('/').filter(|s| !s.is_empty() && *s != "..") {
            p.push(part);
        }
        p
    }
}

impl Vfs for DirFs {
    fn read(&mut self, path: &str) -> FsResult<Vec<u8>> {
        std::fs::read(self.resolve(path)).map_err(|_| FsError::NotFound(path.into()))
    }

    fn write(&mut self, path: &str, data: &[u8]) -> FsResult<()> {
        let p = self.resolve(path);
        if let Some(parent) = p.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        std::fs::write(p, data).map_err(|e| FsError::Io(e.to_string()))
    }

    fn append(&mut self, path: &str, data: &[u8]) -> FsResult<()> {
        use std::io::Write as _;
        let p = self.resolve(path);
        if let Some(parent) = p.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(p)
            .and_then(|mut f| f.write_all(data))
            .map_err(|e| FsError::Io(e.to_string()))
    }

    fn delete(&mut self, path: &str) -> FsResult<()> {
        let p = self.resolve(path);
        if p.is_dir() {
            std::fs::remove_dir_all(p).map_err(|_| FsError::NotFound(path.into()))
        } else {
            std::fs::remove_file(p).map_err(|_| FsError::NotFound(path.into()))
        }
    }

    fn exists(&mut self, path: &str) -> bool {
        self.resolve(path).exists()
    }

    fn list(&mut self, path: &str) -> FsResult<Vec<DirEntry>> {
        let dir = self.resolve(path);
        let entries = std::fs::read_dir(dir).map_err(|_| FsError::NotADirectory(path.into()))?;
        let mut out = Vec::new();
        for e in entries.flatten() {
            let meta = e.metadata().map_err(|e| FsError::Io(e.to_string()))?;
            out.push(DirEntry {
                name: e.file_name().to_string_lossy().to_string(),
                is_dir: meta.is_dir(),
                size: meta.len(),
            });
        }
        Ok(out)
    }

    fn mkdir(&mut self, path: &str) -> FsResult<()> {
        std::fs::create_dir_all(self.resolve(path)).map_err(|e| FsError::Io(e.to_string()))
    }
}
