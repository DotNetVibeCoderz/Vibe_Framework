#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};
use crate::value::{HeapObject, HeapRef, Value};

/// Smallest number of allocations between collections, and the floor the
/// adaptive nursery threshold never drops below.
const GC_MIN_THRESHOLD: usize = 1024;

/// Generational mark-sweep GC heap.
///
/// Objects are born **young**. A *minor* collection marks only young objects —
/// from the roots and from the **remembered set** — and frees the unmarked
/// ones; every survivor is promoted to the **old** generation. A *major*
/// collection is a full mark-sweep of everything, run only when the old
/// generation has doubled since the last one. Most objects die young, so most
/// collections touch a nursery-sized slice of the heap instead of all of it:
/// pause times track allocation, not the size of what the program keeps.
///
/// The invariant that makes this sound: right after any collection, old
/// objects point only at old objects (survivors are promoted together). An old
/// object can come to point at a young one only by being mutated — and every
/// mutation goes through [`Heap::get_mut`], which is therefore the **write
/// barrier**: mutable access to an old object puts it in the remembered set,
/// whose children a minor collection treats as roots. Because `slots` is
/// private, no mutation can bypass it — intrinsics included.
pub struct Heap {
    slots: Vec<Option<HeapObject>>,
    /// Generation per slot: true = old.
    old: Vec<bool>,
    /// Old slots already in `remembered_list`.
    remembered: Vec<bool>,
    remembered_list: Vec<u32>,
    /// Slots allocated since the last collection (the nursery).
    young: Vec<u32>,
    free: Vec<u32>,
    /// All false between collections.
    marks: Vec<bool>,
    allocs_since_gc: usize,
    /// Allocations between collections (the nursery size).
    pub gc_threshold: usize,
    old_count: usize,
    /// Old-generation size that triggers the next major collection.
    major_threshold: usize,
    pub collections: u64,
    pub minor_collections: u64,
    pub major_collections: u64,
    /// Most objects traced or swept by a single minor / major collection —
    /// the pause each kind costs, in work units.
    pub max_minor_work: usize,
    pub max_major_work: usize,
}

impl Heap {
    pub fn new() -> Self {
        Self {
            slots: Vec::new(),
            old: Vec::new(),
            remembered: Vec::new(),
            remembered_list: Vec::new(),
            young: Vec::new(),
            free: Vec::new(),
            marks: Vec::new(),
            allocs_since_gc: 0,
            gc_threshold: GC_MIN_THRESHOLD,
            old_count: 0,
            major_threshold: GC_MIN_THRESHOLD * 4,
            collections: 0,
            minor_collections: 0,
            major_collections: 0,
            max_minor_work: 0,
            max_major_work: 0,
        }
    }

    pub fn alloc(&mut self, obj: HeapObject) -> HeapRef {
        self.allocs_since_gc += 1;
        let idx = if let Some(idx) = self.free.pop() {
            self.slots[idx as usize] = Some(obj);
            self.old[idx as usize] = false;
            self.remembered[idx as usize] = false;
            idx
        } else {
            self.slots.push(Some(obj));
            self.old.push(false);
            self.remembered.push(false);
            self.marks.push(false);
            (self.slots.len() - 1) as u32
        };
        self.young.push(idx);
        idx
    }

    pub fn get(&self, r: HeapRef) -> Result<&HeapObject, String> {
        self.slots
            .get(r as usize)
            .and_then(|s| s.as_ref())
            .ok_or_else(|| format!("dangling heap reference {r}"))
    }

    /// Mutable access — and the write barrier (see the type's docs).
    pub fn get_mut(&mut self, r: HeapRef) -> Result<&mut HeapObject, String> {
        let i = r as usize;
        if i < self.slots.len() && self.old[i] && !self.remembered[i] {
            self.remembered[i] = true;
            self.remembered_list.push(r);
        }
        self.slots
            .get_mut(i)
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

    /// Objects currently in the old generation.
    pub fn old_count(&self) -> usize {
        self.old_count
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

    /// Collect garbage: a minor collection, or a major one when the old
    /// generation has grown past its threshold.
    pub fn collect(&mut self, roots: impl Iterator<Item = HeapRef>) {
        if self.old_count >= self.major_threshold {
            self.collect_full(roots);
        } else {
            self.collect_minor(roots);
        }
    }

    /// Full mark-sweep of every generation; all survivors end up old.
    pub fn collect_full(&mut self, roots: impl Iterator<Item = HeapRef>) {
        let mut worklist: Vec<HeapRef> = roots.collect();
        while let Some(r) = worklist.pop() {
            let idx = r as usize;
            if idx >= self.slots.len() || self.marks[idx] {
                continue;
            }
            if let Some(obj) = &self.slots[idx] {
                self.marks[idx] = true;
                children(obj, &mut worklist);
            }
        }
        let mut live = 0usize;
        for (i, slot) in self.slots.iter_mut().enumerate() {
            if slot.is_some() {
                if self.marks[i] {
                    live += 1;
                    self.marks[i] = false;
                    self.old[i] = true;
                } else {
                    *slot = None;
                    self.free.push(i as u32);
                }
            }
            self.remembered[i] = false;
        }
        self.remembered_list.clear();
        self.young.clear();
        self.old_count = live;
        self.max_major_work = self.max_major_work.max(self.slots.len() + live);
        self.major_threshold = live.saturating_mul(2).max(GC_MIN_THRESHOLD * 4);
        self.finish_collection(live);
        self.major_collections += 1;
    }

    /// Mark and sweep the nursery only; survivors are promoted.
    pub fn collect_minor(&mut self, roots: impl Iterator<Item = HeapRef>) {
        let mut worklist: Vec<HeapRef> = roots.collect();
        // Old objects mutated since the last collection may point at young
        // ones: their children are roots for this collection.
        for &r in &self.remembered_list {
            if let Some(obj) = &self.slots[r as usize] {
                children(obj, &mut worklist);
            }
        }
        while let Some(r) = worklist.pop() {
            let idx = r as usize;
            // Old objects are not traversed: they point only at old objects,
            // except those in the remembered set, already handled above.
            if idx >= self.slots.len() || self.old[idx] || self.marks[idx] {
                continue;
            }
            if let Some(obj) = &self.slots[idx] {
                self.marks[idx] = true;
                children(obj, &mut worklist);
            }
        }
        let mut survivors = 0usize;
        let work = self.young.len() + self.remembered_list.len();
        for &r in &self.young {
            let i = r as usize;
            if self.slots[i].is_none() {
                continue;
            }
            if self.marks[i] {
                self.marks[i] = false;
                self.old[i] = true;
                survivors += 1;
            } else {
                self.slots[i] = None;
                self.free.push(r);
            }
        }
        for &r in &self.remembered_list {
            self.remembered[r as usize] = false;
        }
        self.remembered_list.clear();
        self.young.clear();
        self.old_count += survivors;
        self.max_minor_work = self.max_minor_work.max(work + survivors);
        self.finish_collection(survivors);
        self.minor_collections += 1;
    }

    fn finish_collection(&mut self, _survivors: usize) {
        self.allocs_since_gc = 0;
        self.collections += 1;
        // The nursery stays small whatever the program keeps: a minor
        // collection's pause is bounded by it. What grows with the live data
        // is the major threshold (twice the old generation after a major).
        self.gc_threshold = GC_MIN_THRESHOLD;
    }
}

/// Push every heap reference held by `obj`.
fn children(obj: &HeapObject, out: &mut Vec<HeapRef>) {
    match obj {
        HeapObject::Str(_) => {}
        HeapObject::Array { data, .. } | HeapObject::ListObj(data) => out.extend(refs_in(data)),
        HeapObject::Object { fields, .. } => out.extend(refs_in(fields)),
        HeapObject::Boxed(v) | HeapObject::Delegate { target: v, .. } => {
            if let Value::Ref(r2) = v {
                out.push(*r2);
            }
        }
        HeapObject::MapObj(pairs) => {
            for (k, v) in pairs {
                if let Value::Ref(r2) = k {
                    out.push(*r2);
                }
                if let Value::Ref(r2) = v {
                    out.push(*r2);
                }
            }
        }
        HeapObject::Cursor { target, .. } => out.push(*target),
        HeapObject::ThreadObj { delegate, .. } => out.push(*delegate),
        HeapObject::TaskObj { value, continuations, .. } => {
            if let Value::Ref(r2) = value {
                out.push(*r2);
            }
            out.extend(continuations.iter().copied());
        }
        HeapObject::TypeObj { .. } => {}
        HeapObject::MethodInfoObj { .. } => {}
        HeapObject::FieldInfoObj { .. } => {}
        HeapObject::PropertyInfoObj { .. } => {}
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
    fn nursery_stays_small_while_the_major_threshold_tracks_the_old_generation() {
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
        assert_eq!(heap.old_count(), 2000);
        // Minor pauses stay bounded by the nursery, however much is kept...
        assert_eq!(heap.gc_threshold, GC_MIN_THRESHOLD);
        // ...and a major collection waits until the old generation doubles.
        heap.collect_full(roots.iter().copied());
        assert!(heap.major_threshold >= 4000, "major threshold {}", heap.major_threshold);
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

    fn array(heap: &mut Heap, items: Vec<Value>) -> HeapRef {
        heap.alloc(HeapObject::Array { elem: crate::value::ElemType::Ref, data: items })
    }

    #[test]
    fn survivors_are_promoted_and_not_rescanned() {
        let mut heap = Heap::new();
        let keep = heap.alloc_str("keep");
        heap.alloc_str("dies");
        heap.collect_minor([keep].into_iter());
        assert_eq!(heap.live_count(), 1);
        assert_eq!(heap.old_count(), 1);
        // An old object no longer referenced survives minor collections (it is
        // not traced) and goes at the next major one.
        heap.collect_minor(core::iter::empty());
        assert!(heap.get(keep).is_ok());
        heap.collect_full(core::iter::empty());
        assert!(heap.get(keep).is_err());
        assert_eq!(heap.old_count(), 0);
    }

    #[test]
    fn write_barrier_keeps_young_objects_stored_into_old_ones() {
        let mut heap = Heap::new();
        // An old container...
        let holder = array(&mut heap, vec![Value::Null]);
        heap.collect_minor([holder].into_iter());
        assert_eq!(heap.old_count(), 1);
        // ...gets a young object stored into it through get_mut (the barrier),
        let young = heap.alloc_str("young");
        if let HeapObject::Array { data, .. } = heap.get_mut(holder).unwrap() {
            data[0] = Value::Ref(young);
        }
        // and a minor collection rooted only at the container keeps it, though
        // the container itself is old and not traced.
        heap.collect_minor([holder].into_iter());
        assert_eq!(heap.str_value(young).unwrap(), "young");
        // Even with no roots at all, the remembered container's children are
        // kept until a major collection decides the container is dead.
        let young2 = heap.alloc_str("young2");
        if let HeapObject::Array { data, .. } = heap.get_mut(holder).unwrap() {
            data[0] = Value::Ref(young2);
        }
        heap.collect_minor(core::iter::empty());
        assert!(heap.get(young2).is_ok());
        heap.collect_full(core::iter::empty());
        assert_eq!(heap.live_count(), 0);
    }

    #[test]
    fn major_runs_when_the_old_generation_doubles() {
        let mut heap = Heap::new();
        let mut roots = Vec::new();
        // Keep promoting survivors until a major collection happens.
        for round in 0..20 {
            for i in 0..1000 {
                roots.push(heap.alloc_str(format!("{round}-{i}")));
            }
            heap.collect(roots.iter().copied());
        }
        assert!(heap.major_collections >= 1, "old generation grew 20000 without a major collection");
        assert!(heap.minor_collections > heap.major_collections);
        assert_eq!(heap.live_count(), 20000);
    }
}
