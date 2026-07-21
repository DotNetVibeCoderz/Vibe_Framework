#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};
use crate::heap::Heap;
use crate::host::RuntimeHost;
use crate::opcodes as op;
use crate::rnx::{EhClause, Module, EH_CATCH, EH_FINALLY, MFLAG_CTOR};
use crate::value::{Addr, ElemType, HeapObject, Value};
use alloc::collections::BTreeSet;

/// Sentinel `LeaveCont::target`: after the finally, resume exception unwind.
const UNWIND_TARGET: u32 = 0xFFFF_FFFF;
/// Instructions per green-thread scheduling slice.
const THREAD_SLICE: u64 = 1000;

#[derive(Debug, Clone)]
struct LeaveCont {
    /// Remaining finally handler entry points to run (innermost first).
    remaining: Vec<u32>,
    /// Where to branch once all finallys ran (or UNWIND_TARGET).
    target: u32,
}

#[derive(Debug)]
pub struct Frame {
    pub method: u32,
    pub ip: usize,
    /// Start of the instruction currently (or last) executed in this frame.
    /// `ip` advances past operands during decode, so EH range checks must
    /// use this — a trailing `leave`/`throw` would otherwise sit exactly on
    /// `try_end` and miss its enclosing clause.
    pub instr_ip: usize,
    pub locals: Vec<Value>,
    pub args: Vec<Value>,
    pub stack: Vec<Value>,
    conts: Vec<LeaveCont>,
    /// Filter clauses that rejected the exception currently unwinding
    /// through this frame (cleared when a handler is entered).
    rejected_eh: Vec<u16>,
}

#[derive(Debug, Default)]
struct ThreadSlot {
    /// Parked frame stack (empty for the active thread — it lives in
    /// `Interpreter::frames`).
    frames: Vec<Frame>,
    sleep_until: u64,
    join_on: Option<u32>,
    finished: bool,
    /// Task to complete when this thread retires (Task.Run bodies and
    /// pure timer threads backing Task.Delay).
    completes: Option<u32>,
    /// Task this thread is cooperatively blocked on (Task.Wait/Result).
    wait_task: Option<u32>,
}

#[derive(Debug, PartialEq)]
pub enum RunExit {
    /// All threads ran to completion.
    Completed,
    /// Hit a breakpoint or finished a single step (active thread).
    Paused { method: u32, il_offset: u32 },
    /// Fuel exhausted; call `run` again to continue.
    OutOfFuel,
    /// Unhandled managed exception or interpreter fault.
    Error(String),
}

pub struct Interpreter<'m, H: RuntimeHost> {
    pub module: &'m Module,
    pub heap: Heap,
    pub statics: Vec<Value>,
    pub frames: Vec<Frame>,
    pub host: H,
    pub instructions: u64,
    breakpoints: BTreeSet<(u32, u32)>,
    pub single_step: bool,
    last_paused: Option<(u32, u32)>,
    started: bool,
    threads: Vec<ThreadSlot>,
    cur_thread: usize,
    slice_used: u64,
    sleep_request: Option<u64>,
    /// Set by Task.Wait/Result intrinsics: park the active thread until
    /// this task completes (consumed by the scheduler like sleep_request).
    wait_request: Option<u32>,
    pending_return: Option<Value>,
    /// (exception, target frame, EH clause index in that frame's method).
    pending_exception: Option<(Value, u32, u32)>,
    /// Filter code is running for this (exception, clause index, and the
    /// throw-site ip to restore when the filter rejects — the search must
    /// resume from where the exception occurred, not from filter code).
    pending_filter: Option<(Value, u16, u32)>,
    current_exception: Option<Value>,
}

impl<'m, H: RuntimeHost> Interpreter<'m, H> {
    pub fn new(module: &'m Module, host: H) -> Self {
        Self {
            module,
            heap: Heap::new(),
            statics: vec![Value::I32(0); module.static_slot_count as usize],
            frames: Vec::new(),
            host,
            instructions: 0,
            breakpoints: BTreeSet::new(),
            single_step: false,
            last_paused: None,
            started: false,
            threads: Vec::new(),
            cur_thread: 0,
            slice_used: 0,
            sleep_request: None,
            wait_request: None,
            pending_return: None,
            pending_exception: None,
            pending_filter: None,
            current_exception: None,
        }
    }

    // ---- debugger surface ----

    pub fn set_breakpoint(&mut self, method: u32, il_offset: u32) {
        self.breakpoints.insert((method, il_offset));
    }

    pub fn clear_breakpoint(&mut self, method: u32, il_offset: u32) {
        self.breakpoints.remove(&(method, il_offset));
    }

    pub fn stack_trace(&self) -> Vec<(String, u32, Option<u32>)> {
        self.frames
            .iter()
            .rev()
            .map(|f| {
                let name = self.module.method_name(f.method).to_string();
                let line = self.module.line_for(f.method, f.ip as u32);
                (name, f.ip as u32, line)
            })
            .collect()
    }

    pub fn top_locals(&self) -> Vec<Value> {
        self.frames.last().map(|f| f.locals.clone()).unwrap_or_default()
    }

    /// Top (deepest) frame's locals, formatted `local_<i> = <value>` for the
    /// debugger's variables view.
    pub fn top_locals_display(&self) -> Vec<String> {
        let Some(f) = self.frames.last() else {
            return Vec::new();
        };
        f.locals
            .iter()
            .enumerate()
            .map(|(i, v)| {
                let s = self
                    .display_value(*v)
                    .unwrap_or_else(|_| alloc::format!("{v:?}"));
                alloc::format!("local_{i} = {s}")
            })
            .collect()
    }

    pub fn thread_count(&self) -> usize {
        let parked = self.threads.iter().filter(|t| !t.finished && !t.frames.is_empty()).count();
        parked + if self.frames.is_empty() { 0 } else { 1 }
    }

    // ---- execution ----

    /// Queue the entry point (running all static ctors first).
    pub fn start(&mut self) -> Result<(), String> {
        let entry = self.module.entry_method.ok_or("module has no entry point")?;
        self.push_frame(entry, Vec::new())?;
        let cctors: Vec<u32> = (0..self.module.methods.len() as u32)
            .filter(|&i| self.module.method_name(i).ends_with("::.cctor()"))
            .collect();
        for c in cctors {
            self.push_frame(c, Vec::new())?;
        }
        self.threads.push(ThreadSlot::default());
        self.cur_thread = 0;
        self.started = true;
        Ok(())
    }

    pub fn run_to_completion(&mut self) -> RunExit {
        loop {
            match self.run(1_000_000_000) {
                RunExit::OutOfFuel => continue,
                exit => return exit,
            }
        }
    }

    fn task_pending(&self, task: u32) -> bool {
        matches!(self.heap.get(task), Ok(HeapObject::TaskObj { state: 0, .. }))
    }

    fn thread_runnable(&self, idx: usize, now: u64) -> bool {
        let t = &self.threads[idx];
        if t.finished {
            return false;
        }
        if t.sleep_until > now {
            return false;
        }
        if let Some(j) = t.join_on {
            if !self.threads.get(j as usize).map(|jt| jt.finished).unwrap_or(true) {
                return false;
            }
        }
        if let Some(task) = t.wait_task {
            if self.task_pending(task) {
                return false;
            }
        }
        true
    }

    fn switch_thread(&mut self, next: usize) {
        if next == self.cur_thread {
            return;
        }
        core::mem::swap(&mut self.frames, &mut self.threads[self.cur_thread].frames);
        core::mem::swap(&mut self.frames, &mut self.threads[next].frames);
        self.cur_thread = next;
        self.slice_used = 0;
    }

    /// Pick the next runnable thread (round-robin). None = all blocked.
    fn pick_next(&self, now: u64) -> Option<usize> {
        let n = self.threads.len();
        for off in 1..=n {
            let idx = (self.cur_thread + off) % n;
            if self.thread_runnable(idx, now) {
                return Some(idx);
            }
        }
        None
    }

    fn all_finished(&self) -> bool {
        self.frames.is_empty()
            && self
                .threads
                .iter()
                .all(|t| t.finished || (t.frames.is_empty() && t.completes.is_none()))
    }

    /// Execute up to `fuel` instructions across all green threads.
    pub fn run(&mut self, fuel: u64) -> RunExit {
        if !self.started {
            if let Err(e) = self.start() {
                return RunExit::Error(e);
            }
        }
        let mut budget = fuel;
        loop {
            // Retire the active thread when its stack drains. Threads
            // backing a task (Task.Run bodies, Task.Delay timers) complete
            // it on retirement — for pure timers only once the delay is up.
            if self.frames.is_empty() {
                let slot = &mut self.threads[self.cur_thread];
                if !slot.finished {
                    match slot.completes {
                        Some(task) if self.host.now_ms() >= slot.sleep_until => {
                            slot.completes = None;
                            slot.finished = true;
                            if let Err(e) = self.complete_task(task, Value::Null, false) {
                                return RunExit::Error(e);
                            }
                        }
                        Some(_) => {} // timer not due yet: stays parked
                        None => slot.finished = true,
                    }
                }
                if self.all_finished() {
                    return RunExit::Completed;
                }
            }

            // Reschedule when blocked, finished or the slice is up.
            let now = self.host.now_ms();
            let cur_ok = !self.frames.is_empty()
                && self.thread_runnable_active(now)
                && self.slice_used < THREAD_SLICE;
            if !cur_ok {
                match self.pick_next(now) {
                    Some(next) => {
                        self.switch_thread(next);
                        if self.frames.is_empty() {
                            continue;
                        }
                    }
                    None if !self.frames.is_empty() && self.thread_runnable_active(u64::MAX) => {
                        // Only the active thread exists but it is sleeping:
                        // let the host advance time.
                        let wake = self.threads[self.cur_thread].sleep_until;
                        let now = self.host.now_ms();
                        if wake > now {
                            self.host.sleep_ms(wake - now);
                        }
                        self.threads[self.cur_thread].sleep_until = 0;
                        self.slice_used = 0;
                        continue;
                    }
                    None => {
                        // Everything is sleeping/joined/awaiting: advance to
                        // the earliest timed wake, or report a deadlock when
                        // nothing can ever become runnable again.
                        let now = self.host.now_ms();
                        let earliest = self
                            .threads
                            .iter()
                            .filter(|t| !t.finished && t.sleep_until > now)
                            .map(|t| t.sleep_until)
                            .min();
                        match earliest {
                            Some(wake) => self.host.sleep_ms(wake - now),
                            None => {
                                return RunExit::Error(
                                    "deadlock: all threads blocked with no timed wake".into(),
                                )
                            }
                        }
                        let now = self.host.now_ms();
                        for t in &mut self.threads {
                            if t.sleep_until <= now {
                                t.sleep_until = 0;
                            }
                        }
                        self.slice_used = 0;
                        continue;
                    }
                }
                if self.frames.is_empty() {
                    continue;
                }
            }

            if budget == 0 {
                return RunExit::OutOfFuel;
            }
            budget -= 1;
            self.slice_used += 1;

            let frame = self.frames.last().unwrap();
            let site = (frame.method, frame.ip as u32);

            if self.single_step {
                self.single_step = false;
                self.instructions += 1;
                if let Err(e) = self.step_checked() {
                    return RunExit::Error(e);
                }
                return match self.frames.last() {
                    Some(f) => {
                        let paused = (f.method, f.ip as u32);
                        self.last_paused = Some(paused);
                        RunExit::Paused { method: paused.0, il_offset: paused.1 }
                    }
                    None => RunExit::Completed,
                };
            }

            if self.breakpoints.contains(&site) && self.last_paused != Some(site) {
                self.last_paused = Some(site);
                return RunExit::Paused { method: site.0, il_offset: site.1 };
            }
            self.last_paused = None;

            self.instructions += 1;
            if let Err(e) = self.step_checked() {
                return RunExit::Error(e);
            }

            if let Some(ms) = self.sleep_request.take() {
                let now = self.host.now_ms();
                self.threads[self.cur_thread].sleep_until = now + ms;
                self.slice_used = THREAD_SLICE; // force reschedule
            }

            if let Some(task) = self.wait_request.take() {
                self.threads[self.cur_thread].wait_task = Some(task);
                self.slice_used = THREAD_SLICE; // force reschedule
            }

            if self.heap.should_collect() {
                self.collect_garbage();
            }
        }
    }

    fn thread_runnable_active(&self, now: u64) -> bool {
        let t = &self.threads[self.cur_thread];
        if t.finished {
            return false;
        }
        if now != u64::MAX && t.sleep_until > now {
            return false;
        }
        if let Some(j) = t.join_on {
            if !self.threads.get(j as usize).map(|jt| jt.finished).unwrap_or(true) {
                return false;
            }
        }
        if let Some(task) = t.wait_task {
            if self.task_pending(task) {
                return false;
            }
        }
        true
    }

    /// One instruction; runtime faults become managed exceptions when a
    /// catch handler exists anywhere on the stack.
    fn step_checked(&mut self) -> Result<(), String> {
        match self.step_one() {
            Ok(()) => Ok(()),
            Err(e) => {
                let exc = Value::Ref(self.heap.alloc_str(e));
                self.raise(exc).map_err(|msg| self.annotate_error(msg))
            }
        }
    }

    pub fn collect_garbage(&mut self) {
        let mut roots: Vec<u32> = Vec::new();
        let visit = |frames: &Vec<Frame>, roots: &mut Vec<u32>| {
            for f in frames {
                for v in f.locals.iter().chain(f.args.iter()).chain(f.stack.iter()) {
                    match v {
                        Value::Ref(r) => roots.push(*r),
                        Value::Addr(Addr::Field(r, _)) | Value::Addr(Addr::Elem(r, _)) => {
                            roots.push(*r)
                        }
                        _ => {}
                    }
                }
            }
        };
        visit(&self.frames, &mut roots);
        for t in &self.threads {
            visit(&t.frames, &mut roots);
        }
        for v in &self.statics {
            if let Value::Ref(r) = v {
                roots.push(*r);
            }
        }
        if let Some((Value::Ref(r), _, _)) = &self.pending_exception {
            roots.push(*r);
        }
        if let Some((Value::Ref(r), _, _)) = &self.pending_filter {
            roots.push(*r);
        }
        for t in &self.threads {
            if let Some(task) = t.completes {
                roots.push(task);
            }
            if let Some(task) = t.wait_task {
                roots.push(task);
            }
        }
        if let Some(Value::Ref(r)) = &self.current_exception {
            roots.push(*r);
        }
        self.heap.collect(roots.into_iter());
    }

    fn annotate_error(&self, e: String) -> String {
        let frames: Vec<String> = self
            .stack_trace()
            .into_iter()
            .map(|(name, off, line)| match line {
                Some(l) => format!("  at {name} (IL_{off:04x}, line {l})"),
                None => format!("  at {name} (IL_{off:04x})"),
            })
            .collect();
        format!("{e}\n{}", frames.join("\n"))
    }

    /// Frame push exposed to the intrinsic layer (delegate dispatch).
    pub(crate) fn push_frame_public(&mut self, method_idx: u32, args: Vec<Value>) -> Result<(), String> {
        self.push_frame(method_idx, args)
    }

    fn push_frame(&mut self, method_idx: u32, args: Vec<Value>) -> Result<(), String> {
        if self.frames.len() >= 256 {
            return Err("StackOverflowException".into());
        }
        let m = self
            .module
            .methods
            .get(method_idx as usize)
            .ok_or_else(|| format!("bad method index {method_idx}"))?;
        self.frames.push(Frame {
            method: method_idx,
            ip: 0,
            instr_ip: 0,
            locals: vec![Value::I32(0); m.local_count as usize],
            args,
            stack: Vec::with_capacity(m.max_stack as usize),
            conts: Vec::new(),
            rejected_eh: Vec::new(),
        });
        Ok(())
    }

    // ---- threading (green threads) ----

    /// Called by intrinsics: sleep the active thread cooperatively.
    pub(crate) fn request_sleep(&mut self, ms: u64) {
        self.sleep_request = Some(ms);
    }

    pub(crate) fn request_wait_task(&mut self, task: u32) {
        self.wait_request = Some(task);
    }

    /// The `MoveNext()` method of an async state-machine type.
    pub(crate) fn find_move_next(&self, type_idx: u16) -> Option<u32> {
        (0..self.module.methods.len() as u32).find(|&i| {
            let m = &self.module.methods[i as usize];
            m.owner_type == type_idx && self.module.method_name(i).ends_with("::MoveNext()")
        })
    }

    /// Mark a task complete/faulted and schedule its continuations, each
    /// as a fresh green thread running the state machine's MoveNext.
    pub(crate) fn complete_task(
        &mut self,
        task: u32,
        value: Value,
        faulted: bool,
    ) -> Result<(), String> {
        let conts = match self.heap.get_mut(task)? {
            HeapObject::TaskObj { state, value: v, continuations } => {
                if *state != crate::value::TASK_PENDING {
                    return Ok(()); // already settled
                }
                *state = if faulted { crate::value::TASK_FAULTED } else { crate::value::TASK_DONE };
                *v = value;
                core::mem::take(continuations)
            }
            other => return Err(format!("complete_task on {other:?}")),
        };
        for sm in conts {
            self.spawn_continuation(sm)?;
        }
        Ok(())
    }

    /// Run `sm.MoveNext()` on its own green thread.
    pub(crate) fn spawn_continuation(&mut self, sm: u32) -> Result<(), String> {
        let type_idx = match self.heap.get(sm)? {
            HeapObject::Object { type_idx, .. } => *type_idx,
            other => return Err(format!("async continuation on {other:?}")),
        };
        let method = self
            .find_move_next(type_idx)
            .ok_or("async state machine has no MoveNext")?;
        let m = &self.module.methods[method as usize];
        let frame = Frame {
            method,
            ip: 0,
            instr_ip: 0,
            locals: vec![Value::I32(0); m.local_count as usize],
            args: vec![Value::Ref(sm)],
            stack: Vec::new(),
            conts: Vec::new(),
            rejected_eh: Vec::new(),
        };
        self.threads.push(ThreadSlot { frames: vec![frame], ..Default::default() });
        Ok(())
    }

    /// New pending task completed by a pure timer after `ms`.
    pub(crate) fn spawn_delay_task(&mut self, ms: u64) -> u32 {
        let task = self.heap.alloc(HeapObject::TaskObj {
            state: crate::value::TASK_PENDING,
            value: Value::Null,
            continuations: Vec::new(),
        });
        let now = self.host.now_ms();
        self.threads.push(ThreadSlot {
            sleep_until: now + ms,
            completes: Some(task),
            ..Default::default()
        });
        task
    }

    /// Tie a running thread's retirement to a task's completion (Task.Run).
    pub(crate) fn set_thread_completes(&mut self, thread_idx: u32, task: u32) {
        if let Some(t) = self.threads.get_mut(thread_idx as usize) {
            t.completes = Some(task);
        }
    }

    /// Spawn a green thread that invokes the delegate; returns thread index.
    pub(crate) fn spawn_thread(&mut self, delegate: u32) -> Result<u32, String> {
        let (method, target) = match self.heap.get(delegate)? {
            HeapObject::Delegate { method, target } => (*method, *target),
            other => return Err(format!("Thread expects a delegate, got {other:?}")),
        };
        let m = self
            .module
            .methods
            .get(method as usize)
            .ok_or("bad delegate method")?;
        let args = if m.is_static() { Vec::new() } else { vec![target] };
        let frame = Frame {
            method,
            ip: 0,
            instr_ip: 0,
            locals: vec![Value::I32(0); m.local_count as usize],
            args,
            stack: Vec::new(),
            conts: Vec::new(),
            rejected_eh: Vec::new(),
        };
        self.threads.push(ThreadSlot { frames: vec![frame], ..Default::default() });
        Ok((self.threads.len() - 1) as u32)
    }

    pub(crate) fn join_thread(&mut self, idx: u32) {
        if !self.threads.get(idx as usize).map(|t| t.finished).unwrap_or(true) {
            self.threads[self.cur_thread].join_on = Some(idx);
            self.slice_used = THREAD_SLICE;
        }
    }

    pub(crate) fn thread_finished(&self, idx: u32) -> bool {
        self.threads.get(idx as usize).map(|t| t.finished).unwrap_or(true)
    }

    /// Run a managed method to completion on a private stack (used by LINQ
    /// and other intrinsics that need to call back into IL).
    pub(crate) fn invoke_managed(
        &mut self,
        method: u32,
        args: Vec<Value>,
    ) -> Result<Option<Value>, String> {
        let m = self
            .module
            .methods
            .get(method as usize)
            .ok_or("bad method index in nested call")?;
        if m.is_internal() {
            // Route through the intrinsic dispatcher on a scratch frame.
            let saved = core::mem::take(&mut self.frames);
            self.frames.push(Frame {
                method,
                ip: 0,
                instr_ip: 0,
                locals: Vec::new(),
                args: Vec::new(),
                stack: args,
                conts: Vec::new(),
            rejected_eh: Vec::new(),
            });
            let result = crate::intrinsics::call_internal(self, method, false);
            let value = self.frames.pop().and_then(|mut f| f.stack.pop());
            self.frames = saved;
            result?;
            return Ok(value);
        }
        let saved = core::mem::take(&mut self.frames);
        let saved_pending = self.pending_return.take();
        self.push_frame(method, args)?;
        let mut guard = 50_000_000u64;
        let outcome = loop {
            if self.frames.is_empty() {
                break Ok(self.pending_return.take());
            }
            if guard == 0 {
                break Err("nested managed call ran too long".to_string());
            }
            guard -= 1;
            self.instructions += 1;
            if let Err(e) = self.step_one() {
                let exc = Value::Ref(self.heap.alloc_str(e));
                if let Err(msg) = self.raise(exc) {
                    break Err(msg);
                }
            }
        };
        self.frames = saved;
        self.pending_return = saved_pending;
        outcome
    }

    /// Invoke a delegate heap object with the given (unbound) arguments.
    pub(crate) fn invoke_delegate(
        &mut self,
        delegate: u32,
        mut call_args: Vec<Value>,
    ) -> Result<Option<Value>, String> {
        let (method, target) = match self.heap.get(delegate)? {
            HeapObject::Delegate { method, target } => (*method, *target),
            other => return Err(format!("expected delegate, got {other:?}")),
        };
        let m = self
            .module
            .methods
            .get(method as usize)
            .ok_or("bad delegate method")?;
        let args = if m.is_static() {
            call_args
        } else {
            let mut a = vec![target];
            a.append(&mut call_args);
            a
        };
        self.invoke_managed(method, args)
    }

    // ---- exception handling ----

    /// Is `ip` inside the filter region of a filter clause?
    fn in_filter(eh: &EhClause, ip: u32) -> bool {
        eh.kind == crate::rnx::EH_FILTER && ip >= eh.filter_start && ip < eh.handler_start
    }

    pub(crate) fn raise(&mut self, exc: Value) -> Result<(), String> {
        // Locate the innermost catch/filter clause on the stack, skipping
        // filters this exception already rejected.
        let mut target: Option<(usize, u16)> = None;
        'outer: for fi in (0..self.frames.len()).rev() {
            let f = &self.frames[fi];
            let m = &self.module.methods[f.method as usize];
            let mut best: Option<(u16, &EhClause)> = None;
            for (ci, eh) in m.eh.iter().enumerate() {
                let catching = eh.kind == EH_CATCH || eh.kind == crate::rnx::EH_FILTER;
                let ip = f.instr_ip as u32;
                if !catching || !eh.covers(ip) || eh.in_handler(ip) || Self::in_filter(eh, ip) {
                    continue;
                }
                if eh.kind == crate::rnx::EH_FILTER && f.rejected_eh.contains(&(ci as u16)) {
                    continue;
                }
                let better = match best {
                    Some((_, b)) => (eh.try_end - eh.try_start) < (b.try_end - b.try_start),
                    None => true,
                };
                if better {
                    best = Some((ci as u16, eh));
                }
            }
            if let Some((ci, _)) = best {
                target = Some((fi, ci));
                break 'outer;
            }
        }
        let Some((tfi, clause)) = target else {
            let msg = self.display_value(exc).unwrap_or_else(|_| "<exception>".into());
            return Err(format!("Unhandled exception: {msg}"));
        };
        self.pending_exception = Some((exc, tfi as u32, clause as u32));
        self.continue_unwind()
    }

    fn continue_unwind(&mut self) -> Result<(), String> {
        loop {
            let (exc, tfi, clause_idx) = self
                .pending_exception
                .ok_or("continue_unwind without pending exception")?;
            let top = self.frames.len() - 1;
            let ip;
            let method_idx;
            {
                let f = self.frames.last().unwrap();
                ip = f.instr_ip as u32;
                method_idx = f.method;
            }
            let m = &self.module.methods[method_idx as usize];
            let target_handler = self.module.methods[self.frames[tfi as usize].method as usize]
                .eh
                .get(clause_idx as usize)
                .map(|c| c.handler_start)
                .unwrap_or(0);
            // Innermost finally covering ip that must run before we leave
            // this frame / reach the catch handler.
            let mut best: Option<(u32, u32)> = None;
            for eh in &m.eh {
                if eh.kind != EH_FINALLY || !eh.covers(ip) || eh.in_handler(ip) {
                    continue;
                }
                if top as u32 == tfi && target_handler >= eh.try_start && target_handler < eh.try_end {
                    continue; // finally wraps the catch handler: runs later
                }
                let span = eh.try_end - eh.try_start;
                if best.map(|(_, s)| span < s).unwrap_or(true) {
                    best = Some((eh.handler_start, span));
                }
            }
            if let Some((hs, _)) = best {
                let f = self.frames.last_mut().unwrap();
                f.conts.push(LeaveCont { remaining: Vec::new(), target: UNWIND_TARGET });
                f.stack.clear();
                f.ip = hs as usize;
                return Ok(());
            }
            if top as u32 == tfi {
                let clause = self.module.methods[method_idx as usize]
                    .eh
                    .get(clause_idx as usize)
                    .cloned()
                    .ok_or("bad EH clause index during unwind")?;
                let is_filter = clause.kind == crate::rnx::EH_FILTER;
                let f = self.frames.last_mut().unwrap();
                f.stack.clear();
                f.stack.push(exc);
                if is_filter {
                    // Run the filter code; `endfilter` decides entry.
                    let throw_ip = f.instr_ip as u32;
                    f.ip = clause.filter_start as usize;
                    self.pending_filter = Some((exc, clause_idx as u16, throw_ip));
                } else {
                    f.ip = clause.handler_start as usize;
                    f.rejected_eh.clear();
                    self.current_exception = Some(exc);
                }
                self.pending_exception = None;
                return Ok(());
            }
            self.frames.pop();
        }
    }

    /// `endfilter`: verdict on the stack decides whether the pending
    /// filter's handler runs or the search continues past it.
    fn do_endfilter(&mut self) -> Result<(), String> {
        let verdict = self.pop()?.as_i32()?;
        let Some((exc, clause_idx, throw_ip)) = self.pending_filter.take() else {
            return Ok(()); // stray endfilter: ignore
        };
        let method_idx = self.frames.last().ok_or("endfilter with no frame")?.method;
        let clause = self.module.methods[method_idx as usize]
            .eh
            .get(clause_idx as usize)
            .cloned()
            .ok_or("bad filter clause index")?;
        if verdict != 0 {
            let f = self.frames.last_mut().unwrap();
            f.stack.clear();
            f.stack.push(exc);
            f.ip = clause.handler_start as usize;
            f.rejected_eh.clear();
            self.current_exception = Some(exc);
            Ok(())
        } else {
            let f = self.frames.last_mut().unwrap();
            f.rejected_eh.push(clause_idx);
            f.instr_ip = throw_ip as usize;
            self.raise(exc)
        }
    }

    fn do_leave(&mut self, target: usize) -> Result<(), String> {
        let f = self.frames.last().unwrap();
        let ip = f.instr_ip as u32;
        let m = &self.module.methods[f.method as usize];
        let mut applicable: Vec<(u32, u32)> = m
            .eh
            .iter()
            .filter(|eh| {
                eh.kind == EH_FINALLY
                    && eh.covers(ip)
                    && !eh.in_handler(ip)
                    && !(target as u32 >= eh.try_start && (target as u32) < eh.try_end)
            })
            .map(|eh| (eh.handler_start, eh.try_end - eh.try_start))
            .collect();
        applicable.sort_by_key(|(_, span)| *span);
        let f = self.frames.last_mut().unwrap();
        f.stack.clear();
        if applicable.is_empty() {
            f.ip = target;
            return Ok(());
        }
        let first = applicable[0].0;
        let remaining = applicable[1..].iter().map(|(h, _)| *h).collect();
        f.conts.push(LeaveCont { remaining, target: target as u32 });
        f.ip = first as usize;
        Ok(())
    }

    fn do_endfinally(&mut self) -> Result<(), String> {
        let cont = self.frames.last_mut().unwrap().conts.pop();
        match cont {
            None => Ok(()), // fell into a handler outside leave/unwind: ignore
            Some(c) if c.target == UNWIND_TARGET => self.continue_unwind(),
            Some(mut c) => {
                let f = self.frames.last_mut().unwrap();
                f.stack.clear();
                if c.remaining.is_empty() {
                    f.ip = c.target as usize;
                } else {
                    let next = c.remaining.remove(0);
                    f.conts.push(c);
                    f.ip = next as usize;
                }
                Ok(())
            }
        }
    }

    // ---- helpers on the current frame ----

    fn code(&self) -> &'m [u8] {
        let f = self.frames.last().unwrap();
        &self.module.methods[f.method as usize].code
    }

    fn frame_mut(&mut self) -> &mut Frame {
        self.frames.last_mut().unwrap()
    }

    fn fetch_u8(&mut self) -> Result<u8, String> {
        let code = self.code();
        let f = self.frame_mut();
        let b = *code.get(f.ip).ok_or("IL ran off end of method")?;
        f.ip += 1;
        Ok(b)
    }

    fn fetch_i8(&mut self) -> Result<i8, String> {
        Ok(self.fetch_u8()? as i8)
    }

    fn fetch_u16(&mut self) -> Result<u16, String> {
        let code = self.code();
        let f = self.frame_mut();
        let bytes = code.get(f.ip..f.ip + 2).ok_or("IL ran off end")?;
        f.ip += 2;
        Ok(u16::from_le_bytes(bytes.try_into().unwrap()))
    }

    fn fetch_u32(&mut self) -> Result<u32, String> {
        let code = self.code();
        let f = self.frame_mut();
        let bytes = code.get(f.ip..f.ip + 4).ok_or("IL ran off end")?;
        f.ip += 4;
        Ok(u32::from_le_bytes(bytes.try_into().unwrap()))
    }

    fn fetch_i32(&mut self) -> Result<i32, String> {
        Ok(self.fetch_u32()? as i32)
    }

    fn fetch_u64(&mut self) -> Result<u64, String> {
        let code = self.code();
        let f = self.frame_mut();
        let bytes = code.get(f.ip..f.ip + 8).ok_or("IL ran off end")?;
        f.ip += 8;
        Ok(u64::from_le_bytes(bytes.try_into().unwrap()))
    }

    fn push(&mut self, v: Value) {
        self.frame_mut().stack.push(v);
    }

    fn pop(&mut self) -> Result<Value, String> {
        self.frame_mut().stack.pop().ok_or_else(|| "evaluation stack underflow".into())
    }

    fn branch(&mut self, offset: i32) {
        let f = self.frame_mut();
        f.ip = (f.ip as i64 + offset as i64) as usize;
    }

    // ---- address (managed pointer) load/store ----

    pub(crate) fn load_addr(&mut self, addr: Addr) -> Result<Value, String> {
        match addr {
            Addr::Local(i) => self
                .frames
                .last()
                .and_then(|f| f.locals.get(i as usize))
                .copied()
                .ok_or_else(|| format!("byref local {i} not in current frame")),
            Addr::Arg(i) => self
                .frames
                .last()
                .and_then(|f| f.args.get(i as usize))
                .copied()
                .ok_or_else(|| format!("byref arg {i} not in current frame")),
            Addr::StaticSlot(i) => Ok(self.statics[i as usize]),
            Addr::Field(r, slot) => match self.heap.get(r)? {
                HeapObject::Object { fields, .. } => fields
                    .get(slot as usize)
                    .copied()
                    .ok_or_else(|| "field slot out of range".into()),
                _ => Err("field access on non-object".into()),
            },
            Addr::Elem(r, i) => match self.heap.get(r)? {
                HeapObject::Array { data, .. } => data
                    .get(i as usize)
                    .copied()
                    .ok_or_else(|| "IndexOutOfRangeException".into()),
                _ => Err("element access on non-array".into()),
            },
        }
    }

    pub(crate) fn store_addr(&mut self, addr: Addr, v: Value) -> Result<(), String> {
        match addr {
            Addr::Local(i) => {
                let f = self.frames.last_mut().ok_or("no frame for byref store")?;
                let slot = f
                    .locals
                    .get_mut(i as usize)
                    .ok_or_else(|| format!("byref local {i} not in current frame"))?;
                *slot = v;
                Ok(())
            }
            Addr::Arg(i) => {
                let f = self.frames.last_mut().ok_or("no frame for byref store")?;
                let slot = f
                    .args
                    .get_mut(i as usize)
                    .ok_or_else(|| format!("byref arg {i} not in current frame"))?;
                *slot = v;
                Ok(())
            }
            Addr::StaticSlot(i) => {
                self.statics[i as usize] = v;
                Ok(())
            }
            Addr::Field(r, slot) => match self.heap.get_mut(r)? {
                HeapObject::Object { fields, .. } => {
                    *fields.get_mut(slot as usize).ok_or("field slot out of range")? = v;
                    Ok(())
                }
                _ => Err("field access on non-object".into()),
            },
            Addr::Elem(r, i) => match self.heap.get_mut(r)? {
                HeapObject::Array { data, .. } => {
                    *data.get_mut(i as usize).ok_or("IndexOutOfRangeException")? = v;
                    Ok(())
                }
                _ => Err("element access on non-array".into()),
            },
        }
    }

    /// Deref managed pointers so intrinsics get plain values.
    pub(crate) fn deref(&mut self, v: Value) -> Result<Value, String> {
        match v {
            Value::Addr(a) => self.load_addr(a),
            other => Ok(other),
        }
    }

    // ---- one instruction ----

    fn step_one(&mut self) -> Result<(), String> {
        {
            let f = self.frames.last_mut().ok_or("no frame")?;
            f.instr_ip = f.ip;
        }
        let opcode = self.fetch_u8()?;
        match opcode {
            op::NOP | op::BREAK => {}
            op::LDARG_0..=op::LDARG_3 => {
                let i = (opcode - op::LDARG_0) as usize;
                let v = *self
                    .frames
                    .last()
                    .unwrap()
                    .args
                    .get(i)
                    .ok_or("ldarg out of range")?;
                self.push(v);
            }
            op::LDLOC_0..=op::LDLOC_3 => {
                let i = (opcode - op::LDLOC_0) as usize;
                let v = self.frames.last().unwrap().locals[i];
                self.push(v);
            }
            op::STLOC_0..=op::STLOC_3 => {
                let i = (opcode - op::STLOC_0) as usize;
                let v = self.pop()?;
                self.frame_mut().locals[i] = v;
            }
            op::LDARG_S => {
                let i = self.fetch_u8()? as usize;
                let v = *self.frames.last().unwrap().args.get(i).ok_or("ldarg.s out of range")?;
                self.push(v);
            }
            op::LDARGA_S => {
                let i = self.fetch_u8()? as u16;
                self.push(Value::Addr(Addr::Arg(i)));
            }
            op::STARG_S => {
                let i = self.fetch_u8()? as usize;
                let v = self.pop()?;
                self.frame_mut().args[i] = v;
            }
            op::LDLOC_S => {
                let i = self.fetch_u8()? as usize;
                let v = self.frames.last().unwrap().locals[i];
                self.push(v);
            }
            op::LDLOCA_S => {
                let i = self.fetch_u8()? as u16;
                self.push(Value::Addr(Addr::Local(i)));
            }
            op::STLOC_S => {
                let i = self.fetch_u8()? as usize;
                let v = self.pop()?;
                self.frame_mut().locals[i] = v;
            }
            op::LDNULL => self.push(Value::Null),
            op::LDC_I4_M1 => self.push(Value::I32(-1)),
            op::LDC_I4_0..=op::LDC_I4_8 => {
                self.push(Value::I32((opcode - op::LDC_I4_0) as i32));
            }
            op::LDC_I4_S => {
                let v = self.fetch_i8()? as i32;
                self.push(Value::I32(v));
            }
            op::LDC_I4 => {
                let v = self.fetch_i32()?;
                self.push(Value::I32(v));
            }
            op::LDC_I8 => {
                let v = self.fetch_u64()? as i64;
                self.push(Value::I64(v));
            }
            op::LDC_R4 => {
                let v = f32::from_bits(self.fetch_u32()?);
                self.push(Value::F64(v as f64));
            }
            op::LDC_R8 => {
                let v = f64::from_bits(self.fetch_u64()?);
                self.push(Value::F64(v));
            }
            op::DUP => {
                let v = *self.frames.last().unwrap().stack.last().ok_or("dup on empty stack")?;
                self.push(v);
            }
            op::POP => {
                self.pop()?;
            }
            op::CALL => {
                let target = self.fetch_u32()?;
                self.call_method(target)?;
            }
            op::CALLVIRT => {
                let target = self.fetch_u32()?;
                let target = self.devirtualize(target)?;
                self.call_method(target)?;
            }
            op::RET => {
                let mut done = self.frames.pop().ok_or("ret with no frame")?;
                if let Some(ret) = done.stack.pop() {
                    if let Some(caller) = self.frames.last_mut() {
                        caller.stack.push(ret);
                    } else {
                        self.pending_return = Some(ret);
                    }
                }
            }
            op::BR_S => {
                let off = self.fetch_i8()? as i32;
                self.branch(off);
            }
            op::BRFALSE_S => {
                let off = self.fetch_i8()? as i32;
                let v = self.pop()?;
                if !v.is_truthy() {
                    self.branch(off);
                }
            }
            op::BRTRUE_S => {
                let off = self.fetch_i8()? as i32;
                let v = self.pop()?;
                if v.is_truthy() {
                    self.branch(off);
                }
            }
            op::BEQ_S | op::BGE_S | op::BGT_S | op::BLE_S | op::BLT_S | op::BNE_UN_S
            | op::BGE_UN_S | op::BGT_UN_S | op::BLE_UN_S | op::BLT_UN_S => {
                let off = self.fetch_i8()? as i32;
                if self.compare_branch(opcode + (op::BEQ - op::BEQ_S))? {
                    self.branch(off);
                }
            }
            op::BR => {
                let off = self.fetch_i32()?;
                self.branch(off);
            }
            op::BRFALSE => {
                let off = self.fetch_i32()?;
                let v = self.pop()?;
                if !v.is_truthy() {
                    self.branch(off);
                }
            }
            op::BRTRUE => {
                let off = self.fetch_i32()?;
                let v = self.pop()?;
                if v.is_truthy() {
                    self.branch(off);
                }
            }
            op::BEQ | op::BGE | op::BGT | op::BLE | op::BLT | op::BNE_UN | op::BGE_UN
            | op::BGT_UN | op::BLE_UN | op::BLT_UN => {
                let off = self.fetch_i32()?;
                if self.compare_branch(opcode)? {
                    self.branch(off);
                }
            }
            op::SWITCH => {
                let n = self.fetch_u32()? as usize;
                let mut targets = Vec::with_capacity(n);
                for _ in 0..n {
                    targets.push(self.fetch_i32()?);
                }
                let v = self.pop()?.as_i32()?;
                if (v as usize) < n && v >= 0 {
                    self.branch(targets[v as usize]);
                }
            }
            op::ADD | op::SUB | op::MUL | op::DIV | op::DIV_UN | op::REM | op::REM_UN
            | op::ADD_OVF | op::SUB_OVF | op::MUL_OVF => {
                self.arith(opcode)?;
            }
            op::AND | op::OR | op::XOR | op::SHL | op::SHR | op::SHR_UN => {
                self.bitwise(opcode)?;
            }
            op::NEG => {
                let v = self.pop()?;
                let out = match v {
                    Value::I32(x) => Value::I32(x.wrapping_neg()),
                    Value::I64(x) => Value::I64(x.wrapping_neg()),
                    Value::F64(x) => Value::F64(-x),
                    _ => return Err("neg on non-numeric".into()),
                };
                self.push(out);
            }
            op::NOT => {
                let v = self.pop()?;
                let out = match v {
                    Value::I32(x) => Value::I32(!x),
                    Value::I64(x) => Value::I64(!x),
                    _ => return Err("not on non-integer".into()),
                };
                self.push(out);
            }
            op::CONV_I1 => self.conv(|v| Value::I32(v.as_i32().unwrap_or(0) as i8 as i32))?,
            op::CONV_I2 => self.conv(|v| Value::I32(v.as_i32().unwrap_or(0) as i16 as i32))?,
            op::CONV_I4 | op::CONV_OVF_I4 | op::CONV_I => {
                self.conv_checked(|v| v.as_i32().map(Value::I32))?
            }
            op::CONV_I8 | op::CONV_OVF_I8 => self.conv_checked(|v| v.as_i64().map(Value::I64))?,
            op::CONV_R4 => self.conv_checked(|v| v.as_f64().map(|f| Value::F64(f as f32 as f64)))?,
            op::CONV_R8 => self.conv_checked(|v| v.as_f64().map(Value::F64))?,
            op::CONV_U4 | op::CONV_OVF_U4 | op::CONV_U => {
                self.conv_checked(|v| v.as_i64().map(|x| Value::I32(x as u32 as i32)))?
            }
            op::CONV_U8 | op::CONV_OVF_U8 => self.conv_checked(|v| v.as_i64().map(Value::I64))?,
            op::CONV_U1 | op::CONV_OVF_U1 => {
                self.conv(|v| Value::I32(v.as_i32().unwrap_or(0) as u8 as i32))?
            }
            op::CONV_U2 | op::CONV_OVF_U2 => {
                self.conv(|v| Value::I32(v.as_i32().unwrap_or(0) as u16 as i32))?
            }
            op::CONV_OVF_I1 => self.conv(|v| Value::I32(v.as_i32().unwrap_or(0) as i8 as i32))?,
            op::CONV_OVF_I2 => self.conv(|v| Value::I32(v.as_i32().unwrap_or(0) as i16 as i32))?,
            op::LDSTR => {
                let idx = self.fetch_u32()?;
                let s = self
                    .module
                    .strings
                    .get(idx as usize)
                    .ok_or("ldstr index out of range")?
                    .clone();
                let r = self.heap.alloc_str(s);
                self.push(Value::Ref(r));
            }
            op::NEWOBJ => {
                let ctor = self.fetch_u32()?;
                self.new_object(ctor)?;
            }
            op::CASTCLASS | op::ISINST => {
                let type_idx = self.fetch_u32()? as u16;
                let v = self.pop()?;
                let matches = match v {
                    Value::Ref(r) => match self.heap.get(r)? {
                        HeapObject::Object { type_idx: t, .. } => {
                            type_idx == 0xFFFF || self.type_is(*t, type_idx as u32)
                        }
                        _ => type_idx == 0xFFFF,
                    },
                    Value::Null => false,
                    _ => false,
                };
                if matches || opcode == op::ISINST {
                    self.push(if matches { v } else { Value::Null });
                } else if matches!(v, Value::Null) {
                    self.push(Value::Null);
                } else {
                    return Err("InvalidCastException".into());
                }
            }
            op::THROW => {
                let v = self.pop()?;
                return match self.raise(v) {
                    Ok(()) => Ok(()),
                    Err(e) => Err(e),
                };
            }
            op::LDFLD => {
                let slot = self.fetch_u32()? as u16;
                let obj = self.pop()?;
                let obj = self.deref(obj)?;
                match obj {
                    Value::Ref(r) => {
                        let v = self.load_addr(Addr::Field(r, slot))?;
                        self.push(v);
                    }
                    Value::Null => return Err("NullReferenceException".into()),
                    other => return Err(format!("ldfld on {other:?}")),
                }
            }
            op::LDFLDA => {
                let slot = self.fetch_u32()? as u16;
                let obj = self.pop()?;
                match self.deref(obj)? {
                    Value::Ref(r) => self.push(Value::Addr(Addr::Field(r, slot))),
                    _ => return Err("ldflda on non-object".into()),
                }
            }
            // ldind.* (0x46..=0x50): load through a managed pointer (byref).
            0x46..=0x50 => {
                let addr = self.pop()?;
                let v = match addr {
                    Value::Addr(a) => self.load_addr(a)?,
                    other => return Err(format!("ldind on non-pointer {other:?}")),
                };
                // Narrow loads sign/zero-extend into i32.
                let v = match opcode {
                    0x46 => Value::I32(v.as_i32()? as i8 as i32),  // i1
                    0x47 => Value::I32(v.as_i32()? as u8 as i32),  // u1
                    0x48 => Value::I32(v.as_i32()? as i16 as i32), // i2
                    0x49 => Value::I32(v.as_i32()? as u16 as i32), // u2
                    _ => v,
                };
                self.push(v);
            }
            // stind.* (0x51..=0x57, 0xDF): store through a managed pointer.
            0x51..=0x57 | 0xDF => {
                let v = self.pop()?;
                let addr = self.pop()?;
                let v = match opcode {
                    0x52 => Value::I32(v.as_i32()? as i8 as i32),  // i1
                    0x53 => Value::I32(v.as_i32()? as i16 as i32), // i2
                    _ => v,
                };
                match addr {
                    Value::Addr(a) => self.store_addr(a, v)?,
                    other => return Err(format!("stind on non-pointer {other:?}")),
                }
            }
            op::STFLD => {
                let slot = self.fetch_u32()? as u16;
                let v = self.pop()?;
                let obj = self.pop()?;
                match self.deref(obj)? {
                    Value::Ref(r) => self.store_addr(Addr::Field(r, slot), v)?,
                    Value::Null => return Err("NullReferenceException".into()),
                    other => return Err(format!("stfld on {other:?}")),
                }
            }
            op::LDSFLD => {
                let slot = self.fetch_u32()?;
                let v = *self.statics.get(slot as usize).ok_or("static slot out of range")?;
                self.push(v);
            }
            op::LDSFLDA => {
                let slot = self.fetch_u32()?;
                self.push(Value::Addr(Addr::StaticSlot(slot)));
            }
            op::STSFLD => {
                let slot = self.fetch_u32()?;
                let v = self.pop()?;
                *self.statics.get_mut(slot as usize).ok_or("static slot out of range")? = v;
            }
            op::BOX => {
                let _elem = self.fetch_u32()?;
                let v = self.pop()?;
                let boxed = match v {
                    Value::Ref(_) | Value::Null => v,
                    other => Value::Ref(self.heap.alloc(HeapObject::Boxed(other))),
                };
                self.push(boxed);
            }
            op::UNBOX_ANY => {
                let _elem = self.fetch_u32()?;
                let v = self.pop()?;
                match v {
                    Value::Ref(r) => match self.heap.get(r)? {
                        HeapObject::Boxed(inner) => {
                            let inner = *inner;
                            self.push(inner)
                        }
                        _ => self.push(v),
                    },
                    other => self.push(other),
                }
            }
            op::NEWARR => {
                let elem = ElemType::from_code(self.fetch_u32()?)?;
                let len = self.pop()?.as_i32()?;
                if len < 0 {
                    return Err("OverflowException: negative array size".into());
                }
                let data = vec![elem.default_value(); len as usize];
                let r = self.heap.alloc(HeapObject::Array { elem, data });
                self.push(Value::Ref(r));
            }
            op::LDLEN => {
                let v = self.pop()?;
                match self.deref(v)? {
                    Value::Ref(r) => match self.heap.get(r)? {
                        HeapObject::Array { data, .. } => {
                            let n = data.len();
                            self.push(Value::I32(n as i32))
                        }
                        HeapObject::ListObj(data) => {
                            let n = data.len();
                            self.push(Value::I32(n as i32))
                        }
                        HeapObject::Str(s) => {
                            let n = s.chars().count();
                            self.push(Value::I32(n as i32))
                        }
                        _ => return Err("ldlen on non-array".into()),
                    },
                    Value::Null => return Err("NullReferenceException".into()),
                    _ => return Err("ldlen on non-array".into()),
                }
            }
            op::LDELEMA => {
                let _type = self.fetch_u32()?;
                let idx = self.pop()?.as_i32()?;
                let arr = self.pop()?;
                match self.deref(arr)? {
                    Value::Ref(r) => self.push(Value::Addr(Addr::Elem(r, idx as u32))),
                    _ => return Err("ldelema on non-array".into()),
                }
            }
            op::LDELEM_I1..=op::LDELEM_REF => {
                self.load_element()?;
            }
            op::LDELEM => {
                let _type = self.fetch_u32()?;
                self.load_element()?;
            }
            op::STELEM_I..=op::STELEM_REF => {
                self.store_element()?;
            }
            op::STELEM => {
                let _type = self.fetch_u32()?;
                self.store_element()?;
            }
            op::LEAVE => {
                let off = self.fetch_i32()?;
                let target = (self.frames.last().unwrap().ip as i64 + off as i64) as usize;
                self.do_leave(target)?;
            }
            op::LEAVE_S => {
                let off = self.fetch_i8()? as i32;
                let target = (self.frames.last().unwrap().ip as i64 + off as i64) as usize;
                self.do_leave(target)?;
            }
            op::ENDFINALLY => {
                self.do_endfinally()?;
            }
            op::LDTOKEN => {
                // `typeof(T)`: the MetadataProcessor rewrote the type token to
                // either an RNX type index (user type) or, with the high bit
                // set, a string index carrying a BCL/external type's full name.
                let tok = self.fetch_u32()?;
                let (type_idx, name) = if tok & 0x8000_0000 != 0 {
                    let s = self
                        .module
                        .strings
                        .get((tok & 0x7FFF_FFFF) as usize)
                        .cloned()
                        .unwrap_or_default();
                    (None, s)
                } else {
                    let ti = tok as u16;
                    (Some(ti), self.module.type_name(ti).to_string())
                };
                let r = self.heap.alloc(HeapObject::TypeObj { type_idx, name });
                self.push(Value::Ref(r));
            }
            op::PREFIX => {
                let second = self.fetch_u8()?;
                self.step_prefixed(second)?;
            }
            other => {
                return Err(format!(
                    "unsupported IL opcode 0x{other:02X} in {}",
                    self.module.method_name(self.frames.last().unwrap().method)
                ));
            }
        }
        Ok(())
    }

    fn step_prefixed(&mut self, second: u8) -> Result<(), String> {
        match second {
            op::P_CEQ | op::P_CGT | op::P_CGT_UN | op::P_CLT | op::P_CLT_UN => {
                let b = self.pop()?;
                let a = self.pop()?;
                let result = self.compare_raw(a, b, second)?;
                self.push(Value::I32(result as i32));
            }
            op::P_LDFTN | op::P_LDVIRTFTN => {
                if second == op::P_LDVIRTFTN {
                    // ldvirtftn pops the object; the delegate ctor gets it
                    // separately, so re-push after reading.
                    let obj = self.pop()?;
                    let m = self.fetch_u32()?;
                    self.push(obj);
                    self.push(Value::I32(m as i32));
                } else {
                    let m = self.fetch_u32()?;
                    self.push(Value::I32(m as i32));
                }
            }
            op::P_LDARG => {
                let i = self.fetch_u16()? as usize;
                let v = *self.frames.last().unwrap().args.get(i).ok_or("ldarg out of range")?;
                self.push(v);
            }
            op::P_LDARGA => {
                let i = self.fetch_u16()?;
                self.push(Value::Addr(Addr::Arg(i)));
            }
            op::P_STARG => {
                let i = self.fetch_u16()? as usize;
                let v = self.pop()?;
                self.frame_mut().args[i] = v;
            }
            op::P_LDLOC => {
                let i = self.fetch_u16()? as usize;
                let v = self.frames.last().unwrap().locals[i];
                self.push(v);
            }
            op::P_LDLOCA => {
                let i = self.fetch_u16()?;
                self.push(Value::Addr(Addr::Local(i)));
            }
            op::P_STLOC => {
                let i = self.fetch_u16()? as usize;
                let v = self.pop()?;
                self.frame_mut().locals[i] = v;
            }
            op::P_INITOBJ => {
                let t = self.fetch_u32()?;
                let addr = self.pop()?;
                if let Value::Addr(a) = addr {
                    // The MetadataProcessor marks `initobj <>y__InlineArrayN<T>`
                    // with the high bit + N: allocate a heap array as the buffer.
                    if t & 0x8000_0000 != 0 {
                        let n = (t & 0x7FFF_FFFF) as usize;
                        let arr = self.heap.alloc(HeapObject::Array {
                            elem: ElemType::Ref,
                            data: vec![Value::Null; n],
                        });
                        self.store_addr(a, Value::Ref(arr))?;
                    } else {
                        self.store_addr(a, Value::I32(0))?;
                    }
                }
            }
            op::P_CONSTRAINED => {
                let _type = self.fetch_u32()?;
            }
            op::P_RETHROW => {
                let exc = self.current_exception.unwrap_or(Value::Null);
                return self.raise(exc);
            }
            op::P_ENDFILTER => {
                return self.do_endfilter();
            }
            op::P_VOLATILE | op::P_TAIL | op::P_READONLY => {}
            other => return Err(format!("unsupported prefixed opcode 0xFE 0x{other:02X}")),
        }
        Ok(())
    }

    fn load_element(&mut self) -> Result<(), String> {
        let idx = self.pop()?.as_i32()?;
        let arr = self.pop()?;
        match self.deref(arr)? {
            Value::Ref(r) => {
                let v = match self.heap.get(r)? {
                    HeapObject::ListObj(data) => *data
                        .get(idx as usize)
                        .ok_or("ArgumentOutOfRangeException")?,
                    _ => self.load_addr(Addr::Elem(r, idx as u32))?,
                };
                self.push(v);
                Ok(())
            }
            Value::Null => Err("NullReferenceException".into()),
            other => Err(format!("ldelem on {other:?}")),
        }
    }

    fn store_element(&mut self) -> Result<(), String> {
        let v = self.pop()?;
        let idx = self.pop()?.as_i32()?;
        let arr = self.pop()?;
        match self.deref(arr)? {
            Value::Ref(r) => self.store_addr(Addr::Elem(r, idx as u32), v),
            Value::Null => Err("NullReferenceException".into()),
            other => Err(format!("stelem on {other:?}")),
        }
    }

    fn conv(&mut self, f: impl FnOnce(Value) -> Value) -> Result<(), String> {
        let v = self.pop()?;
        self.push(f(v));
        Ok(())
    }

    fn conv_checked(&mut self, f: impl FnOnce(Value) -> Result<Value, String>) -> Result<(), String> {
        let v = self.pop()?;
        let out = f(v)?;
        self.push(out);
        Ok(())
    }

    fn arith(&mut self, opcode: u8) -> Result<(), String> {
        let b = self.pop()?;
        let a = self.pop()?;
        let out = match (a, b) {
            (Value::F64(_), _) | (_, Value::F64(_)) => {
                let (x, y) = (a.as_f64()?, b.as_f64()?);
                Value::F64(match opcode {
                    op::ADD | op::ADD_OVF => x + y,
                    op::SUB | op::SUB_OVF => x - y,
                    op::MUL | op::MUL_OVF => x * y,
                    op::DIV | op::DIV_UN => x / y,
                    op::REM | op::REM_UN => x % y,
                    _ => unreachable!(),
                })
            }
            (Value::I64(_), _) | (_, Value::I64(_)) => {
                let (x, y) = (a.as_i64()?, b.as_i64()?);
                if matches!(opcode, op::DIV | op::DIV_UN | op::REM | op::REM_UN) && y == 0 {
                    return Err("DivideByZeroException".into());
                }
                Value::I64(match opcode {
                    op::ADD | op::ADD_OVF => x.wrapping_add(y),
                    op::SUB | op::SUB_OVF => x.wrapping_sub(y),
                    op::MUL | op::MUL_OVF => x.wrapping_mul(y),
                    op::DIV => x.wrapping_div(y),
                    op::DIV_UN => ((x as u64) / (y as u64)) as i64,
                    op::REM => x.wrapping_rem(y),
                    op::REM_UN => ((x as u64) % (y as u64)) as i64,
                    _ => unreachable!(),
                })
            }
            _ => {
                let (x, y) = (a.as_i32()?, b.as_i32()?);
                if matches!(opcode, op::DIV | op::DIV_UN | op::REM | op::REM_UN) && y == 0 {
                    return Err("DivideByZeroException".into());
                }
                Value::I32(match opcode {
                    op::ADD | op::ADD_OVF => x.wrapping_add(y),
                    op::SUB | op::SUB_OVF => x.wrapping_sub(y),
                    op::MUL | op::MUL_OVF => x.wrapping_mul(y),
                    op::DIV => x.wrapping_div(y),
                    op::DIV_UN => ((x as u32) / (y as u32)) as i32,
                    op::REM => x.wrapping_rem(y),
                    op::REM_UN => ((x as u32) % (y as u32)) as i32,
                    _ => unreachable!(),
                })
            }
        };
        self.push(out);
        Ok(())
    }

    fn bitwise(&mut self, opcode: u8) -> Result<(), String> {
        let b = self.pop()?;
        let a = self.pop()?;
        let out = match (a, b) {
            (Value::I64(x), _) => {
                let y = b.as_i64()?;
                Value::I64(match opcode {
                    op::AND => x & y,
                    op::OR => x | y,
                    op::XOR => x ^ y,
                    op::SHL => x.wrapping_shl(y as u32),
                    op::SHR => x.wrapping_shr(y as u32),
                    op::SHR_UN => ((x as u64).wrapping_shr(y as u32)) as i64,
                    _ => unreachable!(),
                })
            }
            _ => {
                let (x, y) = (a.as_i32()?, b.as_i32()?);
                Value::I32(match opcode {
                    op::AND => x & y,
                    op::OR => x | y,
                    op::XOR => x ^ y,
                    op::SHL => x.wrapping_shl(y as u32),
                    op::SHR => x.wrapping_shr(y as u32),
                    op::SHR_UN => ((x as u32).wrapping_shr(y as u32)) as i32,
                    _ => unreachable!(),
                })
            }
        };
        self.push(out);
        Ok(())
    }

    fn compare_branch(&mut self, opcode: u8) -> Result<bool, String> {
        let b = self.pop()?;
        let a = self.pop()?;
        Ok(match opcode {
            op::BEQ => self.compare_raw(a, b, op::P_CEQ)?,
            op::BGE => !self.compare_raw(a, b, op::P_CLT)?,
            op::BGT => self.compare_raw(a, b, op::P_CGT)?,
            op::BLE => !self.compare_raw(a, b, op::P_CGT)?,
            op::BLT => self.compare_raw(a, b, op::P_CLT)?,
            op::BNE_UN => !self.compare_raw(a, b, op::P_CEQ)?,
            op::BGE_UN => !self.compare_raw(a, b, op::P_CLT_UN)?,
            op::BGT_UN => self.compare_raw(a, b, op::P_CGT_UN)?,
            op::BLE_UN => !self.compare_raw(a, b, op::P_CGT_UN)?,
            op::BLT_UN => self.compare_raw(a, b, op::P_CLT_UN)?,
            other => return Err(format!("bad branch opcode 0x{other:02X}")),
        })
    }

    fn compare_raw(&self, a: Value, b: Value, kind: u8) -> Result<bool, String> {
        match (a, b) {
            (Value::Ref(x), Value::Ref(y)) => {
                return Ok(match kind {
                    op::P_CEQ => x == y,
                    _ => false,
                });
            }
            (Value::Null, Value::Null) => return Ok(kind == op::P_CEQ),
            (Value::Ref(_), Value::Null) => {
                return Ok(matches!(kind, op::P_CGT_UN));
            }
            (Value::Null, Value::Ref(_)) => return Ok(false),
            _ => {}
        }
        let out = match (a, b) {
            (Value::F64(_), _) | (_, Value::F64(_)) => {
                let (x, y) = (a.as_f64()?, b.as_f64()?);
                match kind {
                    op::P_CEQ => x == y,
                    op::P_CGT => x > y,
                    op::P_CGT_UN => x > y || x.is_nan() || y.is_nan(),
                    op::P_CLT => x < y,
                    op::P_CLT_UN => x < y || x.is_nan() || y.is_nan(),
                    _ => return Err("bad compare kind".into()),
                }
            }
            (Value::I64(_), _) | (_, Value::I64(_)) => {
                let (x, y) = (a.as_i64()?, b.as_i64()?);
                match kind {
                    op::P_CEQ => x == y,
                    op::P_CGT => x > y,
                    op::P_CGT_UN => (x as u64) > (y as u64),
                    op::P_CLT => x < y,
                    op::P_CLT_UN => (x as u64) < (y as u64),
                    _ => return Err("bad compare kind".into()),
                }
            }
            _ => {
                let (x, y) = (a.as_i32()?, b.as_i32()?);
                match kind {
                    op::P_CEQ => x == y,
                    op::P_CGT => x > y,
                    op::P_CGT_UN => (x as u32) > (y as u32),
                    op::P_CLT => x < y,
                    op::P_CLT_UN => (x as u32) < (y as u32),
                    _ => return Err("bad compare kind".into()),
                }
            }
        };
        Ok(out)
    }

    /// Structural equality used by collections and LINQ (strings by
    /// content, numbers numerically, refs by identity otherwise).
    pub(crate) fn value_eq(&self, a: Value, b: Value) -> bool {
        match (a, b) {
            (Value::Ref(x), Value::Ref(y)) => {
                if x == y {
                    return true;
                }
                match (self.heap.get(x), self.heap.get(y)) {
                    (Ok(HeapObject::Str(sx)), Ok(HeapObject::Str(sy))) => sx == sy,
                    (Ok(HeapObject::Boxed(bx)), Ok(HeapObject::Boxed(by))) => {
                        self.value_eq(*bx, *by)
                    }
                    _ => false,
                }
            }
            (Value::Ref(x), other) | (other, Value::Ref(x)) => match self.heap.get(x) {
                Ok(HeapObject::Boxed(inner)) => self.value_eq(*inner, other),
                _ => false,
            },
            (Value::Null, Value::Null) => true,
            _ => self.compare_raw(a, b, op::P_CEQ).unwrap_or(false),
        }
    }

    // ---- calls & object creation ----

    /// Is runtime type `t` assignable to `target` (same type, an ancestor,
    /// or an implemented interface anywhere on the chain)?
    pub(crate) fn type_is(&self, t: u16, target: u32) -> bool {
        let mut cur = t as u32;
        while cur != crate::rnx::NO_TYPE {
            if cur == target {
                return true;
            }
            let Some(td) = self.module.types.get(cur as usize) else {
                return false;
            };
            if td.interfaces.contains(&target) {
                return true;
            }
            cur = td.parent;
        }
        false
    }

    /// Resolve a `callvirt` target against the receiver's runtime type:
    /// walk the type chain looking for an override of the target's root
    /// virtual slot. Non-virtual targets and non-Object receivers (strings,
    /// collections, delegates, ...) fall through to the static target.
    fn devirtualize(&mut self, target: u32) -> Result<u32, String> {
        let Some(m) = self.module.methods.get(target as usize) else {
            return Ok(target);
        };
        if m.is_static() {
            return Ok(target);
        }
        let argc = m.arg_count();
        let receiver = {
            let f = self.frames.last().ok_or("callvirt with no frame")?;
            if f.stack.len() < argc {
                return Ok(target);
            }
            f.stack[f.stack.len() - argc]
        };
        let receiver = match receiver {
            Value::Addr(a) => self.load_addr(a)?,
            v => v,
        };
        let Value::Ref(r) = receiver else {
            return Ok(target);
        };
        let HeapObject::Object { type_idx, .. } = self.heap.get(r)? else {
            return Ok(target);
        };
        let key = m.slot;
        let mut cur = *type_idx as u32;
        while cur != crate::rnx::NO_TYPE {
            let Some(td) = self.module.types.get(cur as usize) else {
                break;
            };
            if let Some((_, imp)) = td.overrides.iter().find(|(slot, _)| *slot == key) {
                return Ok(*imp);
            }
            cur = td.parent;
        }
        Ok(target)
    }

    fn call_method(&mut self, target: u32) -> Result<(), String> {
        let m = self
            .module
            .methods
            .get(target as usize)
            .ok_or_else(|| format!("bad call target {target}"))?;
        if m.is_internal() {
            return crate::intrinsics::call_internal(self, target, false);
        }
        let argc = m.arg_count();
        let mut args = vec![Value::I32(0); argc];
        for i in (0..argc).rev() {
            args[i] = self.pop()?;
        }
        // Instance call on a managed pointer: auto-deref `this`.
        if !m.is_static() {
            if let Value::Addr(a) = args[0] {
                args[0] = self.load_addr(a)?;
            }
            if matches!(args[0], Value::Null) {
                return Err("NullReferenceException".into());
            }
        }
        self.push_frame(target, args)
    }

    fn new_object(&mut self, ctor_idx: u32) -> Result<(), String> {
        let m = self
            .module
            .methods
            .get(ctor_idx as usize)
            .ok_or("bad newobj ctor index")?;
        if m.flags & MFLAG_CTOR == 0 {
            return Err("newobj target is not a constructor".into());
        }
        if m.is_internal() {
            // Delegate construction: ctor(object target, native int ftn).
            let name = &self.module.strings[m.name as usize];
            if m.param_count == 2 && name.ends_with("::.ctor(object,i)") {
                let ftn = self.pop()?.as_i32()? as u32;
                let target = self.pop()?;
                let r = self.heap.alloc(HeapObject::Delegate { method: ftn, target });
                self.push(Value::Ref(r));
                return Ok(());
            }
            return crate::intrinsics::call_internal(self, ctor_idx, true);
        }
        let owner = m.owner_type;
        let t = self
            .module
            .types
            .get(owner as usize)
            .ok_or("newobj ctor has no owner type")?;
        let obj = HeapObject::Object {
            type_idx: owner,
            fields: vec![Value::I32(0); t.field_count as usize],
        };
        let r = self.heap.alloc(obj);
        let argc = m.arg_count();
        let mut args = vec![Value::I32(0); argc];
        for i in (1..argc).rev() {
            args[i] = self.pop()?;
        }
        args[0] = Value::Ref(r);
        // The new object is on the caller's stack once the ctor finishes.
        self.push(Value::Ref(r));
        self.push_frame(ctor_idx, args)
    }

    /// Human-readable value formatting (Console.WriteLine semantics).
    pub(crate) fn display_value(&self, v: Value) -> Result<String, String> {
        Ok(match v {
            Value::I32(x) => x.to_string(),
            Value::I64(x) => x.to_string(),
            Value::F64(x) => format!("{x}"),
            Value::Null => String::new(),
            Value::Ref(r) => match self.heap.get(r)? {
                HeapObject::Str(s) => s.clone(),
                HeapObject::Boxed(inner) => self.display_value(*inner)?,
                HeapObject::Array { data, .. } => format!("<array[{}]>", data.len()),
                HeapObject::ListObj(data) => format!("<list[{}]>", data.len()),
                HeapObject::MapObj(pairs) => format!("<dictionary[{}]>", pairs.len()),
                HeapObject::Object { type_idx, .. } => self.module.type_name(*type_idx).to_string(),
                HeapObject::Delegate { .. } => "<delegate>".into(),
                HeapObject::Cursor { .. } => "<enumerator>".into(),
                HeapObject::TaskObj { state, .. } => format!("<task:{state}>"),
                HeapObject::ThreadObj { .. } => "<thread>".into(),
                HeapObject::TypeObj { name, .. } => name.clone(),
                HeapObject::MethodInfoObj { method_idx } => {
                    crate::intrinsics::method_simple_name(self.module, *method_idx)
                }
                HeapObject::FieldInfoObj { name, .. } => name.clone(),
                HeapObject::PropertyInfoObj { name, .. } => name.clone(),
            },
            Value::Addr(_) => "<byref>".into(),
        })
    }
}
