//! A minimal, bounds-checked FlatBuffers reader — just what reading a
//! `.tflite` model needs: tables through their vtables, vectors of scalars,
//! tables and strings. Every access is checked, so a corrupt model is an
//! error, never a panic or an out-of-bounds read.

use alloc::string::String;
use alloc::vec::Vec;

#[derive(Debug, Clone, PartialEq)]
pub struct FbError(pub &'static str);

pub type Result<T> = core::result::Result<T, FbError>;

#[derive(Clone, Copy)]
pub struct Buf<'a> {
    pub data: &'a [u8],
}

impl<'a> Buf<'a> {
    pub fn u8(&self, pos: usize) -> Result<u8> {
        self.data.get(pos).copied().ok_or(FbError("read past end of model"))
    }

    pub fn u16(&self, pos: usize) -> Result<u16> {
        let b = self.data.get(pos..pos + 2).ok_or(FbError("read past end of model"))?;
        Ok(u16::from_le_bytes([b[0], b[1]]))
    }

    pub fn u32(&self, pos: usize) -> Result<u32> {
        let b = self.data.get(pos..pos + 4).ok_or(FbError("read past end of model"))?;
        Ok(u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }

    pub fn i32(&self, pos: usize) -> Result<i32> {
        Ok(self.u32(pos)? as i32)
    }

    pub fn i64(&self, pos: usize) -> Result<i64> {
        let b = self.data.get(pos..pos + 8).ok_or(FbError("read past end of model"))?;
        let mut a = [0u8; 8];
        a.copy_from_slice(b);
        Ok(i64::from_le_bytes(a))
    }

    pub fn f32(&self, pos: usize) -> Result<f32> {
        Ok(f32::from_bits(self.u32(pos)?))
    }

    /// Follow a u32 offset stored at `pos` (relative to `pos`).
    pub fn deref(&self, pos: usize) -> Result<usize> {
        let off = self.u32(pos)? as usize;
        let target = pos.checked_add(off).ok_or(FbError("offset overflow"))?;
        if target >= self.data.len() {
            return Err(FbError("offset points outside the model"));
        }
        Ok(target)
    }

    /// The root table.
    pub fn root(&self) -> Result<Table<'a>> {
        let pos = self.deref(0)?;
        Table::at(*self, pos)
    }
}

#[derive(Clone, Copy)]
pub struct Table<'a> {
    buf: Buf<'a>,
    pos: usize,
    vtable: usize,
    vt_len: usize,
}

impl<'a> Table<'a> {
    pub fn at(buf: Buf<'a>, pos: usize) -> Result<Self> {
        let soff = buf.i32(pos)? as i64;
        let vtable = pos as i64 - soff;
        if vtable < 0 || vtable as usize >= buf.data.len() {
            return Err(FbError("bad vtable offset"));
        }
        let vtable = vtable as usize;
        let vt_len = buf.u16(vtable)? as usize;
        Ok(Self { buf, pos, vtable, vt_len })
    }

    /// Absolute position of field `index`, or None if absent.
    fn field(&self, index: usize) -> Result<Option<usize>> {
        let entry = 4 + index * 2;
        if entry + 2 > self.vt_len {
            return Ok(None);
        }
        let off = self.buf.u16(self.vtable + entry)? as usize;
        Ok(if off == 0 { None } else { Some(self.pos + off) })
    }

    pub fn u8_or(&self, index: usize, default: u8) -> Result<u8> {
        match self.field(index)? {
            Some(p) => self.buf.u8(p),
            None => Ok(default),
        }
    }

    pub fn i32_or(&self, index: usize, default: i32) -> Result<i32> {
        match self.field(index)? {
            Some(p) => self.buf.i32(p),
            None => Ok(default),
        }
    }

    pub fn u32_or(&self, index: usize, default: u32) -> Result<u32> {
        match self.field(index)? {
            Some(p) => self.buf.u32(p),
            None => Ok(default),
        }
    }

    pub fn f32_or(&self, index: usize, default: f32) -> Result<f32> {
        match self.field(index)? {
            Some(p) => self.buf.f32(p),
            None => Ok(default),
        }
    }

    pub fn bool_or(&self, index: usize, default: bool) -> Result<bool> {
        Ok(self.u8_or(index, default as u8)? != 0)
    }

    pub fn table(&self, index: usize) -> Result<Option<Table<'a>>> {
        match self.field(index)? {
            Some(p) => Ok(Some(Table::at(self.buf, self.buf.deref(p)?)?)),
            None => Ok(None),
        }
    }

    pub fn vector(&self, index: usize) -> Result<Option<Vector<'a>>> {
        match self.field(index)? {
            Some(p) => {
                let start = self.buf.deref(p)?;
                let len = self.buf.u32(start)? as usize;
                Ok(Some(Vector { buf: self.buf, start: start + 4, len }))
            }
            None => Ok(None),
        }
    }

    pub fn string(&self, index: usize) -> Result<String> {
        match self.vector(index)? {
            Some(v) => {
                let bytes = v.bytes()?;
                Ok(String::from_utf8_lossy(bytes).into())
            }
            None => Ok(String::new()),
        }
    }
}

#[derive(Clone, Copy)]
pub struct Vector<'a> {
    buf: Buf<'a>,
    start: usize,
    pub len: usize,
}

impl<'a> Vector<'a> {
    pub fn bytes(&self) -> Result<&'a [u8]> {
        self.buf
            .data
            .get(self.start..self.start + self.len)
            .ok_or(FbError("vector past end of model"))
    }

    pub fn i32(&self, i: usize) -> Result<i32> {
        if i >= self.len {
            return Err(FbError("vector index out of range"));
        }
        self.buf.i32(self.start + i * 4)
    }

    pub fn f32(&self, i: usize) -> Result<f32> {
        if i >= self.len {
            return Err(FbError("vector index out of range"));
        }
        self.buf.f32(self.start + i * 4)
    }

    pub fn i64(&self, i: usize) -> Result<i64> {
        if i >= self.len {
            return Err(FbError("vector index out of range"));
        }
        self.buf.i64(self.start + i * 8)
    }

    pub fn table(&self, i: usize) -> Result<Table<'a>> {
        if i >= self.len {
            return Err(FbError("vector index out of range"));
        }
        let p = self.start + i * 4;
        Table::at(self.buf, self.buf.deref(p)?)
    }

    pub fn i32s(&self) -> Result<Vec<i32>> {
        (0..self.len).map(|i| self.i32(i)).collect()
    }

    pub fn f32s(&self) -> Result<Vec<f32>> {
        (0..self.len).map(|i| self.f32(i)).collect()
    }

    pub fn i64s(&self) -> Result<Vec<i64>> {
        (0..self.len).map(|i| self.i64(i)).collect()
    }
}
