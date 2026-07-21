use std::future::Future;
use std::pin::Pin;
use std::sync::{Arc, Mutex};
use std::task::{Context, Poll, Waker};

struct TimerEntry {
    deadline_ms: u64,
    waker: Option<Waker>,
    fired: bool,
}

struct DriverInner {
    now_ms: u64,
    timers: Vec<Arc<Mutex<TimerEntry>>>,
}

/// Monotonic-time source + timer wheel. On hardware, the SysTick/RTC ISR
/// calls `advance_to`; on the host, the firmware main loop feeds wall time.
#[derive(Clone)]
pub struct TimerDriver {
    inner: Arc<Mutex<DriverInner>>,
}

impl TimerDriver {
    pub fn new() -> Self {
        Self { inner: Arc::new(Mutex::new(DriverInner { now_ms: 0, timers: Vec::new() })) }
    }

    pub fn now_ms(&self) -> u64 {
        self.inner.lock().unwrap().now_ms
    }

    /// Advance the clock; due timers fire on the next `fire_due` call.
    pub fn advance_to(&self, now_ms: u64) {
        let mut inner = self.inner.lock().unwrap();
        if now_ms > inner.now_ms {
            inner.now_ms = now_ms;
        }
    }

    pub fn advance_by(&self, delta_ms: u64) {
        let mut inner = self.inner.lock().unwrap();
        inner.now_ms += delta_ms;
    }

    /// Wake every timer whose deadline has passed. Called by the executor.
    pub(crate) fn fire_due(&self) {
        let mut inner = self.inner.lock().unwrap();
        let now = inner.now_ms;
        inner.timers.retain(|entry| {
            let mut e = entry.lock().unwrap();
            if !e.fired && e.deadline_ms <= now {
                e.fired = true;
                if let Some(w) = e.waker.take() {
                    w.wake();
                }
            }
            !e.fired
        });
    }

    pub fn next_deadline(&self) -> Option<u64> {
        let inner = self.inner.lock().unwrap();
        inner
            .timers
            .iter()
            .filter_map(|e| {
                let e = e.lock().unwrap();
                if e.fired { None } else { Some(e.deadline_ms) }
            })
            .min()
    }

    fn register(&self, deadline_ms: u64) -> Arc<Mutex<TimerEntry>> {
        let entry = Arc::new(Mutex::new(TimerEntry { deadline_ms, waker: None, fired: false }));
        self.inner.lock().unwrap().timers.push(entry.clone());
        entry
    }
}

impl Default for TimerDriver {
    fn default() -> Self {
        Self::new()
    }
}

pub struct SleepFuture {
    entry: Arc<Mutex<TimerEntry>>,
}

impl Future for SleepFuture {
    type Output = ();
    fn poll(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<()> {
        let mut e = self.entry.lock().unwrap();
        if e.fired {
            Poll::Ready(())
        } else {
            e.waker = Some(cx.waker().clone());
            Poll::Pending
        }
    }
}

/// Async sleep relative to the driver's current time.
pub fn sleep_ms(driver: &TimerDriver, ms: u64) -> SleepFuture {
    let deadline = driver.now_ms() + ms;
    SleepFuture { entry: driver.register(deadline) }
}
