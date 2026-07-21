//! RNX ("RustNet eXecutable") — the compact module format the interpreter
//! loads. The C# MetadataProcessor converts a compiled .NET assembly into
//! RNX by rewriting metadata tokens into direct table indices:
//!
//! ```text
//! magic "RNX1" | u16 version | u16 flags | u32 static_slot_count
//! u32 string_count  { u32 len, utf8 }*
//! u32 type_count    { u32 name_str, u16 field_count, u16 static_field_count,
//!                     ... (v3 parent/interfaces/overrides),
//!                     u16 field_desc_count { u32 name_str, u8 flags, u32 slot }* (v5),
//!                     u16 attr_count { u32 ctor, u16 fixed { arg }, u16 named
//!                       { u8 kind, u32 name_str, arg } }* (v6);
//!                       arg = u8 tag + payload (0 null,1 i32,2 i64,3 f64,4 str,5 bool) }*
//! u32 method_count  { u32 name_str, u16 owner_type(0xFFFF=none), u8 flags,
//!                     u8 param_count, u16 local_count, u16 max_stack,
//!                     u32 code_len, code }*
//! u32 entry_method (0xFFFFFFFF = library, no entry)
//! u32 debug_method_count { u32 method_idx, u32 point_count { u32 il_off, u32 line }* }*
//! ```
//!
//! Inline tokens inside IL code are rewritten to: string index (ldstr),
//! method index (call/callvirt/newobj/ldftn), field slot (ldfld/stfld),
//! global static slot (ldsfld/stsfld), type index (castclass/isinst),
//! element-type code (newarr/box/unbox.any/ldelem/stelem).

#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};

pub const MAGIC: &[u8; 4] = b"RNX1";
pub const VERSION: u16 = 6;

/// Field flags for the RNX v5 field-descriptor section (reflection).
pub const FFLAG_STATIC: u8 = 0x01;
pub const FFLAG_PUBLIC: u8 = 0x02;

/// A constant custom-attribute argument value (RNX v6).
#[derive(Debug, Clone)]
pub enum AttrArg {
    Null,
    I32(i32),
    I64(i64),
    F64(f64),
    /// String-table index.
    Str(u32),
    Bool(bool),
}

/// A custom attribute applied to a type (RNX v6). Named args set a field
/// (`kind == 0`) or property (`kind == 1`) after construction.
#[derive(Debug, Clone)]
pub struct AttrDesc {
    pub ctor: u32,
    pub fixed: Vec<AttrArg>,
    pub named: Vec<(u8, u32, AttrArg)>,
}

pub const MFLAG_STATIC: u8 = 0x01;
pub const MFLAG_INTERNAL: u8 = 0x02;
pub const MFLAG_CTOR: u8 = 0x04;
/// The method returns a value (non-`void`). Lets reflection `Invoke` know
/// whether a callee leaves a result on the stack.
pub const MFLAG_HASRET: u8 = 0x08;

pub const EH_CATCH: u8 = 0;
pub const EH_FINALLY: u8 = 1;
/// `catch when (...)`: filter code runs first (exception on the stack,
/// `endfilter` yields 0/1), then the handler on a match.
pub const EH_FILTER: u8 = 2;

/// No base class / no owner sentinel.
pub const NO_TYPE: u32 = 0xFFFF_FFFF;

/// Exception-handling clause (RNX v2+). Ranges are IL byte offsets.
#[derive(Debug, Clone)]
pub struct EhClause {
    pub kind: u8,
    pub try_start: u32,
    pub try_end: u32,
    pub handler_start: u32,
    pub handler_end: u32,
    /// Filter code start (kind == EH_FILTER); the filter region runs from
    /// here to `handler_start`.
    pub filter_start: u32,
}

impl EhClause {
    pub fn covers(&self, ip: u32) -> bool {
        ip >= self.try_start && ip < self.try_end
    }
    pub fn in_handler(&self, ip: u32) -> bool {
        ip >= self.handler_start && ip < self.handler_end
    }
}

#[derive(Debug, Clone)]
pub struct TypeDef {
    pub name: u32,
    /// TOTAL instance field slots (own + inherited).
    pub field_count: u16,
    pub static_field_count: u16,
    /// Base class type index, or `NO_TYPE` (RNX v3).
    pub parent: u32,
    /// Implemented interface type indices, ancestors flattened in (v3).
    pub interfaces: Vec<u32>,
    /// Virtual dispatch: (root slot method idx -> impl method idx) (v3).
    pub overrides: Vec<(u32, u32)>,
    /// This type's own declared fields, for reflection (`GetFields`) (v5).
    pub fields: Vec<FieldDesc>,
    /// User-defined custom attributes applied to this type (v6).
    pub attrs: Vec<AttrDesc>,
}

/// A declared field's reflection descriptor (RNX v5).
#[derive(Debug, Clone)]
pub struct FieldDesc {
    /// Simple field name (string index).
    pub name: u32,
    /// `FFLAG_STATIC` | `FFLAG_PUBLIC`.
    pub flags: u8,
    /// Instance-layout slot (own + inherited offset) or global static slot.
    pub slot: u32,
}

impl FieldDesc {
    pub fn is_static(&self) -> bool {
        self.flags & FFLAG_STATIC != 0
    }
    pub fn is_public(&self) -> bool {
        self.flags & FFLAG_PUBLIC != 0
    }
}

#[derive(Debug, Clone)]
pub struct MethodDef {
    pub name: u32,
    pub owner_type: u16,
    pub flags: u8,
    pub param_count: u8,
    pub local_count: u16,
    pub max_stack: u16,
    /// Root virtual slot this method occupies (its own index when it is
    /// the original declaration or not virtual) (RNX v3).
    pub slot: u32,
    pub code: Vec<u8>,
    pub eh: Vec<EhClause>,
}

impl MethodDef {
    pub fn is_static(&self) -> bool {
        self.flags & MFLAG_STATIC != 0
    }
    pub fn is_internal(&self) -> bool {
        self.flags & MFLAG_INTERNAL != 0
    }
    /// True when the method returns a value (non-`void`).
    pub fn has_return(&self) -> bool {
        self.flags & MFLAG_HASRET != 0
    }
    /// Arg slot count = params plus `this` for instance methods.
    pub fn arg_count(&self) -> usize {
        self.param_count as usize + if self.is_static() { 0 } else { 1 }
    }
}

#[derive(Debug, Clone, Default)]
pub struct SequencePoint {
    pub il_offset: u32,
    pub line: u32,
}

#[derive(Debug, Clone)]
pub struct Module {
    pub strings: Vec<String>,
    pub types: Vec<TypeDef>,
    pub methods: Vec<MethodDef>,
    pub entry_method: Option<u32>,
    pub static_slot_count: u32,
    pub debug: Vec<(u32, Vec<SequencePoint>)>,
    /// Embedded assets (name -> bytes) carried in the module (RNX v4).
    /// Managed apps read them via `RustNet.Resources`.
    pub resources: Vec<(String, Vec<u8>)>,
}

impl Module {
    /// Bytes of an embedded resource by name, if present.
    pub fn resource(&self, name: &str) -> Option<&[u8]> {
        self.resources.iter().find(|(n, _)| n == name).map(|(_, b)| b.as_slice())
    }
}

impl Module {
    pub fn method_name(&self, idx: u32) -> &str {
        self.methods
            .get(idx as usize)
            .and_then(|m| self.strings.get(m.name as usize))
            .map(|s| s.as_str())
            .unwrap_or("<unknown>")
    }

    pub fn type_name(&self, idx: u16) -> &str {
        self.types
            .get(idx as usize)
            .and_then(|t| self.strings.get(t.name as usize))
            .map(|s| s.as_str())
            .unwrap_or("<unknown>")
    }

    pub fn find_method(&self, full_name: &str) -> Option<u32> {
        self.methods
            .iter()
            .position(|m| self.strings.get(m.name as usize).map(|s| s == full_name).unwrap_or(false))
            .map(|i| i as u32)
    }

    /// Nearest source line for an IL offset, if debug info is present.
    pub fn line_for(&self, method: u32, il_offset: u32) -> Option<u32> {
        let points = &self.debug.iter().find(|(m, _)| *m == method)?.1;
        points
            .iter()
            .filter(|p| p.il_offset <= il_offset)
            .max_by_key(|p| p.il_offset)
            .map(|p| p.line)
    }

    // ---- binary serialization ----

    pub fn to_bytes(&self) -> Vec<u8> {
        let mut out = Vec::new();
        out.extend_from_slice(MAGIC);
        out.extend_from_slice(&VERSION.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&self.static_slot_count.to_le_bytes());
        out.extend_from_slice(&(self.strings.len() as u32).to_le_bytes());
        for s in &self.strings {
            let b = s.as_bytes();
            out.extend_from_slice(&(b.len() as u32).to_le_bytes());
            out.extend_from_slice(b);
        }
        out.extend_from_slice(&(self.types.len() as u32).to_le_bytes());
        for t in &self.types {
            out.extend_from_slice(&t.name.to_le_bytes());
            out.extend_from_slice(&t.field_count.to_le_bytes());
            out.extend_from_slice(&t.static_field_count.to_le_bytes());
            out.extend_from_slice(&t.parent.to_le_bytes());
            out.extend_from_slice(&(t.interfaces.len() as u16).to_le_bytes());
            for i in &t.interfaces {
                out.extend_from_slice(&i.to_le_bytes());
            }
            out.extend_from_slice(&(t.overrides.len() as u16).to_le_bytes());
            for (slot, imp) in &t.overrides {
                out.extend_from_slice(&slot.to_le_bytes());
                out.extend_from_slice(&imp.to_le_bytes());
            }
            // Field descriptors (RNX v5).
            out.extend_from_slice(&(t.fields.len() as u16).to_le_bytes());
            for fd in &t.fields {
                out.extend_from_slice(&fd.name.to_le_bytes());
                out.push(fd.flags);
                out.extend_from_slice(&fd.slot.to_le_bytes());
            }
            // Custom attributes (RNX v6).
            out.extend_from_slice(&(t.attrs.len() as u16).to_le_bytes());
            for a in &t.attrs {
                out.extend_from_slice(&a.ctor.to_le_bytes());
                out.extend_from_slice(&(a.fixed.len() as u16).to_le_bytes());
                for arg in &a.fixed {
                    write_attr_arg(&mut out, arg);
                }
                out.extend_from_slice(&(a.named.len() as u16).to_le_bytes());
                for (kind, name, arg) in &a.named {
                    out.push(*kind);
                    out.extend_from_slice(&name.to_le_bytes());
                    write_attr_arg(&mut out, arg);
                }
            }
        }
        out.extend_from_slice(&(self.methods.len() as u32).to_le_bytes());
        for m in &self.methods {
            out.extend_from_slice(&m.name.to_le_bytes());
            out.extend_from_slice(&m.owner_type.to_le_bytes());
            out.push(m.flags);
            out.push(m.param_count);
            out.extend_from_slice(&m.local_count.to_le_bytes());
            out.extend_from_slice(&m.max_stack.to_le_bytes());
            out.extend_from_slice(&m.slot.to_le_bytes());
            out.extend_from_slice(&(m.code.len() as u32).to_le_bytes());
            out.extend_from_slice(&m.code);
            out.extend_from_slice(&(m.eh.len() as u32).to_le_bytes());
            for eh in &m.eh {
                out.push(eh.kind);
                out.extend_from_slice(&eh.try_start.to_le_bytes());
                out.extend_from_slice(&eh.try_end.to_le_bytes());
                out.extend_from_slice(&eh.handler_start.to_le_bytes());
                out.extend_from_slice(&eh.handler_end.to_le_bytes());
                out.extend_from_slice(&eh.filter_start.to_le_bytes());
            }
        }
        out.extend_from_slice(&self.entry_method.unwrap_or(0xFFFF_FFFF).to_le_bytes());
        out.extend_from_slice(&(self.debug.len() as u32).to_le_bytes());
        for (mi, points) in &self.debug {
            out.extend_from_slice(&mi.to_le_bytes());
            out.extend_from_slice(&(points.len() as u32).to_le_bytes());
            for p in points {
                out.extend_from_slice(&p.il_offset.to_le_bytes());
                out.extend_from_slice(&p.line.to_le_bytes());
            }
        }
        // Resources section (RNX v4): appended after debug.
        out.extend_from_slice(&(self.resources.len() as u32).to_le_bytes());
        for (name, data) in &self.resources {
            let nb = name.as_bytes();
            out.extend_from_slice(&(nb.len() as u32).to_le_bytes());
            out.extend_from_slice(nb);
            out.extend_from_slice(&(data.len() as u32).to_le_bytes());
            out.extend_from_slice(data);
        }
        out
    }

    pub fn from_bytes(data: &[u8]) -> Result<Module, String> {
        let mut r = Reader { data, pos: 0 };
        let magic = r.bytes(4)?;
        if magic != MAGIC {
            return Err("not an RNX file (bad magic)".into());
        }
        let version = r.u16()?;
        if !(1..=6).contains(&version) {
            return Err(format!("unsupported RNX version {version}"));
        }
        let _flags = r.u16()?;
        let static_slot_count = r.u32()?;
        let string_count = r.u32()? as usize;
        let mut strings = Vec::with_capacity(string_count);
        for _ in 0..string_count {
            let len = r.u32()? as usize;
            let bytes = r.bytes(len)?;
            strings.push(String::from_utf8(bytes.to_vec()).map_err(|e| e.to_string())?);
        }
        let type_count = r.u32()? as usize;
        let mut types = Vec::with_capacity(type_count);
        for _ in 0..type_count {
            let name = r.u32()?;
            let field_count = r.u16()?;
            let static_field_count = r.u16()?;
            let (parent, interfaces, overrides) = if version >= 3 {
                let parent = r.u32()?;
                let iface_count = r.u16()? as usize;
                let mut interfaces = Vec::with_capacity(iface_count);
                for _ in 0..iface_count {
                    interfaces.push(r.u32()?);
                }
                let override_count = r.u16()? as usize;
                let mut overrides = Vec::with_capacity(override_count);
                for _ in 0..override_count {
                    overrides.push((r.u32()?, r.u32()?));
                }
                (parent, interfaces, overrides)
            } else {
                (NO_TYPE, Vec::new(), Vec::new())
            };
            let mut fields = Vec::new();
            if version >= 5 {
                let field_desc_count = r.u16()? as usize;
                for _ in 0..field_desc_count {
                    let name = r.u32()?;
                    let flags = r.u8()?;
                    let slot = r.u32()?;
                    fields.push(FieldDesc { name, flags, slot });
                }
            }
            let mut attrs = Vec::new();
            if version >= 6 {
                let attr_count = r.u16()? as usize;
                for _ in 0..attr_count {
                    let ctor = r.u32()?;
                    let fixed_count = r.u16()? as usize;
                    let mut fixed = Vec::with_capacity(fixed_count);
                    for _ in 0..fixed_count {
                        fixed.push(read_attr_arg(&mut r)?);
                    }
                    let named_count = r.u16()? as usize;
                    let mut named = Vec::with_capacity(named_count);
                    for _ in 0..named_count {
                        let kind = r.u8()?;
                        let name = r.u32()?;
                        let arg = read_attr_arg(&mut r)?;
                        named.push((kind, name, arg));
                    }
                    attrs.push(AttrDesc { ctor, fixed, named });
                }
            }
            types.push(TypeDef { name, field_count, static_field_count, parent, interfaces, overrides, fields, attrs });
        }
        let method_count = r.u32()? as usize;
        let mut methods = Vec::with_capacity(method_count);
        for mi in 0..method_count {
            let name = r.u32()?;
            let owner_type = r.u16()?;
            let flags = r.u8()?;
            let param_count = r.u8()?;
            let local_count = r.u16()?;
            let max_stack = r.u16()?;
            let slot = if version >= 3 { r.u32()? } else { mi as u32 };
            let code_len = r.u32()? as usize;
            let code = r.bytes(code_len)?.to_vec();
            let mut eh = Vec::new();
            if version >= 2 {
                let eh_count = r.u32()? as usize;
                for _ in 0..eh_count {
                    eh.push(EhClause {
                        kind: r.u8()?,
                        try_start: r.u32()?,
                        try_end: r.u32()?,
                        handler_start: r.u32()?,
                        handler_end: r.u32()?,
                        filter_start: if version >= 3 { r.u32()? } else { 0 },
                    });
                }
            }
            methods.push(MethodDef {
                name, owner_type, flags, param_count, local_count, max_stack, slot, code, eh,
            });
        }
        let entry = r.u32()?;
        let entry_method = if entry == 0xFFFF_FFFF { None } else { Some(entry) };
        let mut debug = Vec::new();
        if let Ok(debug_count) = r.u32() {
            for _ in 0..debug_count {
                let mi = r.u32()?;
                let count = r.u32()? as usize;
                let mut points = Vec::with_capacity(count);
                for _ in 0..count {
                    points.push(SequencePoint { il_offset: r.u32()?, line: r.u32()? });
                }
                debug.push((mi, points));
            }
        }
        let mut resources = Vec::new();
        if version >= 4 {
            if let Ok(res_count) = r.u32() {
                for _ in 0..res_count {
                    let name_len = r.u32()? as usize;
                    let name = String::from_utf8(r.bytes(name_len)?.to_vec()).map_err(|e| e.to_string())?;
                    let data_len = r.u32()? as usize;
                    let data = r.bytes(data_len)?.to_vec();
                    resources.push((name, data));
                }
            }
        }
        Ok(Module { strings, types, methods, entry_method, static_slot_count, debug, resources })
    }
}

fn write_attr_arg(out: &mut Vec<u8>, arg: &AttrArg) {
    match arg {
        AttrArg::Null => out.push(0),
        AttrArg::I32(v) => {
            out.push(1);
            out.extend_from_slice(&v.to_le_bytes());
        }
        AttrArg::I64(v) => {
            out.push(2);
            out.extend_from_slice(&v.to_le_bytes());
        }
        AttrArg::F64(v) => {
            out.push(3);
            out.extend_from_slice(&v.to_le_bytes());
        }
        AttrArg::Str(idx) => {
            out.push(4);
            out.extend_from_slice(&idx.to_le_bytes());
        }
        AttrArg::Bool(b) => {
            out.push(5);
            out.push(*b as u8);
        }
    }
}

fn read_attr_arg(r: &mut Reader<'_>) -> Result<AttrArg, String> {
    Ok(match r.u8()? {
        0 => AttrArg::Null,
        1 => AttrArg::I32(r.u32()? as i32),
        2 => AttrArg::I64(i64::from_le_bytes(r.bytes(8)?.try_into().unwrap())),
        3 => AttrArg::F64(f64::from_le_bytes(r.bytes(8)?.try_into().unwrap())),
        4 => AttrArg::Str(r.u32()?),
        5 => AttrArg::Bool(r.u8()? != 0),
        other => return Err(format!("bad attribute arg tag {other}")),
    })
}

struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn bytes(&mut self, n: usize) -> Result<&'a [u8], String> {
        if self.pos + n > self.data.len() {
            return Err("unexpected end of RNX file".into());
        }
        let s = &self.data[self.pos..self.pos + n];
        self.pos += n;
        Ok(s)
    }
    fn u8(&mut self) -> Result<u8, String> {
        Ok(self.bytes(1)?[0])
    }
    fn u16(&mut self) -> Result<u16, String> {
        Ok(u16::from_le_bytes(self.bytes(2)?.try_into().unwrap()))
    }
    fn u32(&mut self) -> Result<u32, String> {
        Ok(u32::from_le_bytes(self.bytes(4)?.try_into().unwrap()))
    }
}

/// Programmatic module builder — used by runtime tests and fuzzing to
/// assemble IL without going through the C# toolchain.
#[derive(Default)]
pub struct Builder {
    strings: Vec<String>,
    types: Vec<TypeDef>,
    methods: Vec<MethodDef>,
    entry: Option<u32>,
    static_slots: u32,
    resources: Vec<(String, Vec<u8>)>,
}

impl Builder {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn string(&mut self, s: &str) -> u32 {
        if let Some(i) = self.strings.iter().position(|x| x == s) {
            return i as u32;
        }
        self.strings.push(s.to_string());
        (self.strings.len() - 1) as u32
    }

    pub fn add_type(&mut self, name: &str, field_count: u16) -> u16 {
        let name = self.string(name);
        self.types.push(TypeDef {
            name,
            field_count,
            static_field_count: 0,
            parent: NO_TYPE,
            interfaces: Vec::new(),
            overrides: Vec::new(),
            fields: Vec::new(),
            attrs: Vec::new(),
        });
        (self.types.len() - 1) as u16
    }

    pub fn set_parent(&mut self, ty: u16, parent: u16) {
        self.types[ty as usize].parent = parent as u32;
    }

    pub fn add_interface(&mut self, ty: u16, iface: u16) {
        self.types[ty as usize].interfaces.push(iface as u32);
    }

    /// Record that `ty` implements/overrides virtual slot `slot` with `imp`.
    pub fn add_override(&mut self, ty: u16, slot: u32, imp: u32) {
        self.types[ty as usize].overrides.push((slot, imp));
    }

    /// Mark `method` as overriding the virtual slot rooted at `slot`.
    pub fn set_slot(&mut self, method: u32, slot: u32) {
        self.methods[method as usize].slot = slot;
    }

    pub fn alloc_static_slots(&mut self, n: u32) -> u32 {
        let base = self.static_slots;
        self.static_slots += n;
        base
    }

    #[allow(clippy::too_many_arguments)]
    pub fn add_method(
        &mut self,
        name: &str,
        owner_type: Option<u16>,
        flags: u8,
        param_count: u8,
        local_count: u16,
        code: Vec<u8>,
    ) -> u32 {
        let name = self.string(name);
        let slot = self.methods.len() as u32;
        self.methods.push(MethodDef {
            name,
            owner_type: owner_type.unwrap_or(0xFFFF),
            flags,
            param_count,
            local_count,
            max_stack: 64,
            slot,
            code,
            eh: Vec::new(),
        });
        (self.methods.len() - 1) as u32
    }

    /// Attach exception-handling clauses to the most recently added method.
    pub fn set_eh(&mut self, method: u32, eh: Vec<EhClause>) {
        self.methods[method as usize].eh = eh;
    }

    pub fn set_entry(&mut self, method: u32) {
        self.entry = Some(method);
    }

    /// Attach an embedded resource (name -> bytes).
    pub fn add_resource(&mut self, name: &str, data: Vec<u8>) {
        self.resources.push((name.to_string(), data));
    }

    pub fn build(self) -> Module {
        Module {
            strings: self.strings,
            types: self.types,
            methods: self.methods,
            entry_method: self.entry,
            static_slot_count: self.static_slots,
            debug: Vec::new(),
            resources: self.resources,
        }
    }
}
