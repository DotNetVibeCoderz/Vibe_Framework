use crate::timer::TimerDriver;
use std::cell::RefCell;
use std::collections::VecDeque;
use std::future::Future;
use std::pin::Pin;
use std::rc::Rc;
use std::cell::Cell;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::task::{Context, Poll, RawWaker, RawWakerVTable, Waker};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct TaskId(pub u64);

type BoxFuture = Pin<Box<dyn Future<Output = ()>>>;

struct Task {
    #[allow(dead_code)]
    id: TaskId,
    future: BoxFuture,
    woken: Arc<AtomicBool>,
}

struct SharedQueue {
    ready: RefCell<VecDeque<Task>>,
    incoming: RefCell<Vec<Task>>,
    // Cell, not AtomicU64: the queue lives in an Rc (single-threaded) and
    // 32-bit MCU targets (Xtensa) have no 64-bit atomics.
    next_id: Cell<u64>,
}

/// Handle that lets tasks (and the embedder) enqueue new tasks.
#[derive(Clone)]
pub struct Spawner {
    queue: Rc<SharedQueue>,
}

impl Spawner {
    pub fn spawn<F, T>(&self, future: F) -> JoinHandle<T>
    where
        F: Future<Output = T> + 'static,
        T: 'static,
    {
        let id = TaskId(self.queue.next_id.get());
        self.queue.next_id.set(id.0 + 1);
        let slot: Rc<RefCell<Option<T>>> = Rc::new(RefCell::new(None));
        let slot2 = slot.clone();
        let wrapped = Box::pin(async move {
            let value = future.await;
            *slot2.borrow_mut() = Some(value);
        });
        self.queue.incoming.borrow_mut().push(Task {
            id,
            future: wrapped,
            woken: Arc::new(AtomicBool::new(true)),
        });
        JoinHandle { id, slot }
    }
}

/// Result slot for a spawned task.
pub struct JoinHandle<T> {
    pub id: TaskId,
    slot: Rc<RefCell<Option<T>>>,
}

impl<T> JoinHandle<T> {
    /// Returns the task's output if it has completed.
    pub fn try_result(&self) -> Option<T> {
        self.slot.borrow_mut().take()
    }
}

/// Single-threaded cooperative executor.
pub struct Executor {
    queue: Rc<SharedQueue>,
    /// Tasks parked waiting for a wake.
    parked: Vec<Task>,
    timers: TimerDriver,
}

impl Default for Executor {
    fn default() -> Self {
        Self::new()
    }
}

impl Executor {
    pub fn new() -> Self {
        Self {
            queue: Rc::new(SharedQueue {
                ready: RefCell::new(VecDeque::new()),
                incoming: RefCell::new(Vec::new()),
                next_id: Cell::new(1),
            }),
            parked: Vec::new(),
            timers: TimerDriver::new(),
        }
    }

    pub fn spawner(&self) -> Spawner {
        Spawner { queue: self.queue.clone() }
    }

    pub fn timer_driver(&self) -> TimerDriver {
        self.timers.clone()
    }

    /// Poll every runnable task until nothing can make progress.
    /// Returns the number of task polls performed (for profiling).
    pub fn run_until_idle(&mut self) -> u64 {
        let mut polls = 0u64;
        loop {
            self.timers.fire_due();
            // Move woken parked tasks and newly spawned tasks into the queue.
            self.queue.ready.borrow_mut().extend(self.queue.incoming.borrow_mut().drain(..));
            let mut still_parked = Vec::new();
            for task in self.parked.drain(..) {
                if task.woken.swap(false, Ordering::AcqRel) {
                    self.queue.ready.borrow_mut().push_back(task);
                } else {
                    still_parked.push(task);
                }
            }
            self.parked = still_parked;

            let Some(mut task) = self.queue.ready.borrow_mut().pop_front() else {
                break;
            };
            polls += 1;
            let waker = flag_waker(task.woken.clone());
            let mut cx = Context::from_waker(&waker);
            match task.future.as_mut().poll(&mut cx) {
                Poll::Ready(()) => {}
                Poll::Pending => self.parked.push(task),
            }
        }
        polls
    }

    /// Number of tasks that are alive (ready or parked).
    pub fn task_count(&self) -> usize {
        self.parked.len()
            + self.queue.ready.borrow().len()
            + self.queue.incoming.borrow().len()
    }

    /// Earliest pending timer deadline, for tickless sleeping in firmware.
    pub fn next_deadline_ms(&self) -> Option<u64> {
        self.timers.next_deadline()
    }
}

fn flag_waker(flag: Arc<AtomicBool>) -> Waker {
    fn clone(data: *const ()) -> RawWaker {
        let arc = unsafe { Arc::<AtomicBool>::from_raw(data as *const AtomicBool) };
        let cloned = arc.clone();
        std::mem::forget(arc);
        RawWaker::new(Arc::into_raw(cloned) as *const (), &VTABLE)
    }
    fn wake(data: *const ()) {
        let arc = unsafe { Arc::<AtomicBool>::from_raw(data as *const AtomicBool) };
        arc.store(true, Ordering::Release);
    }
    fn wake_by_ref(data: *const ()) {
        let arc = unsafe { Arc::<AtomicBool>::from_raw(data as *const AtomicBool) };
        arc.store(true, Ordering::Release);
        std::mem::forget(arc);
    }
    fn drop_waker(data: *const ()) {
        unsafe { drop(Arc::<AtomicBool>::from_raw(data as *const AtomicBool)) };
    }
    static VTABLE: RawWakerVTable = RawWakerVTable::new(clone, wake, wake_by_ref, drop_waker);
    unsafe { Waker::from_raw(RawWaker::new(Arc::into_raw(flag) as *const (), &VTABLE)) }
}
