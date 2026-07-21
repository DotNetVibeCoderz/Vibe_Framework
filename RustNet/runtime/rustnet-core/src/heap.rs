#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};
use crate::value::{HeapObject, HeapRef, Value};

/// Smallest number of allocations between collections, and the floor the
/// adaptive threshold never drops below.
const GC_MIN_THRESHOLD: usize = 1024;

/// Mark-sweep GC heap. Slots are reused through a free list; collection is
/// triggered by allocation count and walks roots supplied by the
/// interpreter (frames + statics). The trigger threshold grows with the live
/// set after each collection, so a program that retains many objects does not
/// pay a full heap scan every `GC_MIN_THRESHOLD` allocations — GC frequency
/// tracks garbage produced, not the size of the live set.
pub struct Heap {
    slots: Vec<Option<HeapObject>>,
    free: Vec<u32>,
    marks: Vec<bool>,
    allocs_since_gc: usize,
    pub gc_threshold: usize,
    pub collections: u64,
}

impl Heap {
    pub fn new() -> Self {
        Self {
            slots: Vec::new(),
            free: Vec::new(),
            marks: Vec::new(),
            allocs_since_gc: 0,
            gc_threshold: GC_MIN_THRESHOLD,
            collections: 0,
        }
    }

    pub fn alloc(&mut self, obj: HeapObject) -> HeapRef {
        self.allocs_since_gc += 1;
        if let Some(idx) = self.free.pop() {
            self.slots[idx as usize] = Some(obj);
            idx
        } else {
            self.slots.push(Some(obj));
            (self.slots.len() - 1) as u32
        }
    }

    pub fn get(&self, r: HeapRef) -> Result<&HeapObject, String> {
        self.slots
            .get(r as usize)
            .and_then(|s| s.as_ref())
            .ok_or_else(|| format!("dangling heap reference {r}"))
    }

    pub fn get_mut(&mut self, r: HeapRef) -> Result<&mut HeapObject, String> {
        self.slots
            .get_mut(r as usize)
            .and_then(|s| s.as_mut())
            .ok_or_else(|| format!("dangling heap reference {r}"))
    }

    pub fn str_value(&self, r: HeapRef) -> Result<&str, String> {
        match self.get(r)? {
            HeapObject::Str(s) => Ok(s),
            other => Err(format!("expected string on heap, got {other:?}")),
        }
    }

    pub fn alloc_str(&mut self, s: impl Into<String>) -> HeapRef {
        self.alloc(HeapObject::Str(s.into()))
    }

    pub fn live_count(&self) -> usize {
        self.slots.iter().filter(|s| s.is_some()).count()
    }

    pub fn used_bytes(&self) -> u64 {
        self.slots
            .iter()
            .flatten()
            .map(|o| match o {
                HeapObject::Str(s) => 24 + s.len() as u64,
                HeapObject::Array { data, .. } => 24 + 16 * data.len() as u64,
                HeapObject::Object { fields, .. } => 24 + 16 * fields.len() as u64,
                HeapObject::ListObj(data) => 24 + 16 * data.len() as u64,
                HeapObject::MapObj(pairs) => 24 + 32 * pairs.len() as u64,
                _ => 24,
            })
            .sum()
    }

    pub fn should_collect(&self) -> bool {
        self.allocs_since_gc >= self.gc_threshold
    }

    /// Mark from roots and sweep everything unreachable.
    pub fn collect(&mut self, roots: impl Iterator<Item = HeapRef>) {
        self.marks.clear();
        self.marks.resize(self.slots.len(), false);
        let mut worklist: Vec<HeapRef> = roots.collect();
        while let Some(r) = worklist.pop() {
            let idx = r as usize;
            if idx >= self.slots.len() || self.marks[idx] {
                continue;
            }
            self.marks[idx] = true;
            if let Some(obj) = &self.slots[idx] {
                match obj {
                    HeapObject::Str(_) => {}
                    HeapObject::Array { data, .. } => {
                        worklist.extend(refs_in(data));
                    }
                    HeapObject::Object { fields, .. } => {
                        worklist.extend(refs_in(fields));
                    }
                    HeapObject::Boxed(v) | HeapObject::Delegate { target: v, .. } => {
                        if let Value::Ref(r2) = v {
                            worklist.push(*r2);
                        }
                    }
                    HeapObject::ListObj(data) => {
                        worklist.extend(refs_in(data));
                    }
                    HeapObject::MapObj(pairs) => {
                        for (k, v) in pairs {
                            if let Value::Ref(r2) = k {
                                worklist.push(*r2);
                            }
                            if let Value::Ref(r2) = v {
                                worklist.push(*r2);
                            }
                        }
                    }
                    HeapObject::Cursor { target, .. } => worklist.push(*target),
                    HeapObject::ThreadObj { delegate, .. } => worklist.push(*delegate),
                    HeapObject::TaskObj { value, continuations, .. } => {
                        if let Value::Ref(r2) = value {
                            worklist.push(*r2);
                        }
                        worklist.extend(continuations.iter().copied());
                    }
                    HeapObject::TypeObj { .. } => {}
                    HeapObject::MethodInfoObj { .. } => {}
                    HeapObject::FieldInfoObj { .. } => {}
                    HeapObject::PropertyInfoObj { .. } => {}
                }
            }
        }
        let mut live = 0usize;
        for (i, slot) in self.slots.iter_mut().enumerate() {
            if slot.is_some() {
                if self.marks[i] {
                    live += 1;
                } else {
                    *slot = None;
                    self.free.push(i as u32);
                }
            }
        }
        self.allocs_since_gc = 0;
        self.collections += 1;
        // Grow the next trigger with the surviving set: allow at least as many
        // new allocations as objects kept alive before scanning again.
        self.gc_threshold = live.saturating_mul(2).max(GC_MIN_THRESHOLD);
    }
}

fn refs_in(values: &[Value]) -> impl Iterator<Item = HeapRef> + '_ {
    values.iter().filter_map(|v| match v {
        Value::Ref(r) => Some(*r),
        Value::Addr(crate::value::Addr::Field(r, _)) => Some(*r),
        Value::Addr(crate::value::Addr::Elem(r, _)) => Some(*r),
        _ => None,
    })
}

impl Default for Heap {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn threshold_grows_with_live_set() {
        let mut heap = Heap::new();
        assert_eq!(heap.gc_threshold, GC_MIN_THRESHOLD);
        // Retain 2000 objects and churn some garbage.
        let roots: Vec<HeapRef> =
            (0..2000).map(|i| heap.alloc(HeapObject::Str(format!("keep{i}")))).collect();
        for i in 0..500 {
            heap.alloc(HeapObject::Str(format!("garbage{i}")));
        }
        heap.collect(roots.iter().copied());
        assert_eq!(heap.live_count(), 2000);
        // The next collection is deferred until allocations scale with the live
        // set, not the fixed floor — bounding GC overhead for large heaps.
        assert!(
            heap.gc_threshold >= 4000,
            "threshold {} should track the ~2000 live objects",
            heap.gc_threshold
        );
    }

    #[test]
    fn threshold_returns_to_floor_when_empty() {
        let mut heap = Heap::new();
        for i in 0..100 {
            heap.alloc(HeapObject::Str(format!("g{i}")));
        }
        heap.collect(core::iter::empty()); // nothing rooted -> all reclaimed
        assert_eq!(heap.live_count(), 0);
        assert_eq!(heap.gc_threshold, GC_MIN_THRESHOLD);
    }
}
