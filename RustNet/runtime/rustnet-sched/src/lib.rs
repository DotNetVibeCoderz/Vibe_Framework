//! Cooperative async task scheduler.
//!
//! A small single-threaded executor designed for MCU use: no work stealing,
//! no threads, deterministic wake ordering. Tasks are `async` blocks; timers
//! integrate through a monotonic-time driver supplied by the platform. The
//! C# `Task`/`await` programming model maps onto this executor through the
//! runtime's async intrinsics.

mod executor;
mod timer;
mod event;

pub use executor::{Executor, JoinHandle, Spawner, TaskId};
pub use timer::{sleep_ms, TimerDriver};
pub use event::{Event, EventListener};

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    use std::rc::Rc;

    #[test]
    fn runs_spawned_tasks_to_completion() {
        let mut exec = Executor::new();
        let log = Rc::new(RefCell::new(Vec::new()));
        let (l1, l2) = (log.clone(), log.clone());
        exec.spawner().spawn(async move {
            l1.borrow_mut().push("a");
        });
        exec.spawner().spawn(async move {
            l2.borrow_mut().push("b");
        });
        exec.run_until_idle();
        assert_eq!(*log.borrow(), vec!["a", "b"]);
    }

    #[test]
    fn tasks_can_spawn_tasks() {
        let mut exec = Executor::new();
        let hit = Rc::new(RefCell::new(false));
        let hit2 = hit.clone();
        let spawner = exec.spawner();
        let inner_spawner = spawner.clone();
        spawner.spawn(async move {
            inner_spawner.spawn(async move {
                *hit2.borrow_mut() = true;
            });
        });
        exec.run_until_idle();
        assert!(*hit.borrow());
    }

    #[test]
    fn join_handle_returns_value() {
        let mut exec = Executor::new();
        let handle = exec.spawner().spawn(async { 21 * 2 });
        exec.run_until_idle();
        assert_eq!(handle.try_result(), Some(42));
    }

    #[test]
    fn timers_fire_in_order() {
        let mut exec = Executor::new();
        let driver = exec.timer_driver();
        let log = Rc::new(RefCell::new(Vec::new()));
        let (l1, l2) = (log.clone(), log.clone());
        let (d1, d2) = (driver.clone(), driver.clone());
        exec.spawner().spawn(async move {
            sleep_ms(&d1, 20).await;
            l1.borrow_mut().push(20u64);
        });
        exec.spawner().spawn(async move {
            sleep_ms(&d2, 10).await;
            l2.borrow_mut().push(10u64);
        });
        // Advance simulated time manually.
        exec.run_until_idle();
        driver.advance_to(10);
        exec.run_until_idle();
        driver.advance_to(20);
        exec.run_until_idle();
        assert_eq!(*log.borrow(), vec![10, 20]);
    }

    #[test]
    fn event_wakes_waiter() {
        let mut exec = Executor::new();
        let event = Event::new();
        let seen = Rc::new(RefCell::new(false));
        let seen2 = seen.clone();
        let listener = event.listen();
        exec.spawner().spawn(async move {
            listener.await;
            *seen2.borrow_mut() = true;
        });
        exec.run_until_idle();
        assert!(!*seen.borrow());
        event.set();
        exec.run_until_idle();
        assert!(*seen.borrow());
    }
}
