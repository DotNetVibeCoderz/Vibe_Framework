//! RNDP framing and command codes.
//!
//! The definitions live in `rustnet-rndp`, which is `no_std` so bare-metal
//! firmware speaks exactly the same framing as this one rather than a second
//! hand-rolled copy. Re-exported here so existing `proto::` paths keep
//! working.

pub use rustnet_rndp::*;
