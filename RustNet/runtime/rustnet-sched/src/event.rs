use std::future::Future;
use std::pin::Pin;
use std::sync::{Arc, Mutex};
use std::task::{Context, Poll, Waker};

struct EventInner {
    set: bool,
    waiters: Vec<Waker>,
}

/// One-shot event for event-driven programming (ISR -> task signaling).
#[derive(Clone)]
pub struct Event {
    inner: Arc<Mutex<EventInner>>,
}

impl Event {
    pub fn new() -> Self {
        Self { inner: Arc::new(Mutex::new(EventInner { set: false, waiters: Vec::new() })) }
    }

    /// Signal the event, waking all current listeners.
    pub fn set(&self) {
        let mut inner = self.inner.lock().unwrap();
        inner.set = true;
        for w in inner.waiters.drain(..) {
            w.wake();
        }
    }

    pub fn reset(&self) {
        self.inner.lock().unwrap().set = false;
    }

    pub fn is_set(&self) -> bool {
        self.inner.lock().unwrap().set
    }

    pub fn listen(&self) -> EventListener {
        EventListener { inner: self.inner.clone() }
    }
}

impl Default for Event {
    fn default() -> Self {
        Self::new()
    }
}

pub struct EventListener {
    inner: Arc<Mutex<EventInner>>,
}

impl Future for EventListener {
    type Output = ();
    fn poll(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<()> {
        let mut inner = self.inner.lock().unwrap();
        if inner.set {
            Poll::Ready(())
        } else {
            inner.waiters.push(cx.waker().clone());
            Poll::Pending
        }
    }
}
