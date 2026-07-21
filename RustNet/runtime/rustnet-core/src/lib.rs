//! RustNet .NET runtime core.
//!
//! Pipeline: C# → `dotnet build` → DLL → MetadataProcessor → `.rnx` →
//! this crate loads and interprets it on the MCU. See `rnx` for the module
//! format, `interp` for the interpreter/GC, `host` for the firmware-facing
//! trait and `intrinsics` for the built-in corlib surface.
//!
//! Builds `no_std + alloc` when the default `std` feature is disabled —
//! the bare-metal profile real-silicon firmware links against
//! (`cargo build -p rustnet-core --no-default-features
//!  --target riscv32imc-unknown-none-elf`).

#![cfg_attr(not(feature = "std"), no_std)]

extern crate alloc;

/// Float math that resolves natively on std and through `libm` on
/// bare-metal targets (core has no f64 transcendentals).
pub(crate) mod fmath {
    macro_rules! shim {
        ($name:ident, $std:ident, $libm:ident, 1) => {
            #[cfg(feature = "std")]
            #[inline]
            pub fn $name(x: f64) -> f64 {
                x.$std()
            }
            #[cfg(not(feature = "std"))]
            #[inline]
            pub fn $name(x: f64) -> f64 {
                libm::$libm(x)
            }
        };
        ($name:ident, $std:ident, $libm:ident, 2) => {
            #[cfg(feature = "std")]
            #[inline]
            pub fn $name(x: f64, y: f64) -> f64 {
                x.$std(y)
            }
            #[cfg(not(feature = "std"))]
            #[inline]
            pub fn $name(x: f64, y: f64) -> f64 {
                libm::$libm(x, y)
            }
        };
    }
    shim!(sqrt, sqrt, sqrt, 1);
    shim!(fabs, abs, fabs, 1);
    shim!(sin, sin, sin, 1);
    shim!(cos, cos, cos, 1);
    shim!(tan, tan, tan, 1);
    shim!(ln, ln, log, 1);
    shim!(log10, log10, log10, 1);
    shim!(exp, exp, exp, 1);
    shim!(floor, floor, floor, 1);
    shim!(ceil, ceil, ceil, 1);
    shim!(round, round, round, 1);
    shim!(atan, atan, atan, 1);
    shim!(atan2, atan2, atan2, 2);
    shim!(pow, powf, pow, 2);
}

pub mod heap;
pub mod host;
pub mod interp;
pub mod intrinsics;
pub mod opcodes;
pub mod regex;
pub mod rnx;
pub mod value;

pub use host::{HostValue, RuntimeHost, TestHost};
pub use interp::{Interpreter, RunExit};
pub use rnx::{Builder, Module, MFLAG_CTOR, MFLAG_INTERNAL, MFLAG_STATIC};
pub use value::Value;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::opcodes as op;

    const S: u8 = MFLAG_STATIC;
    const SI: u8 = MFLAG_STATIC | MFLAG_INTERNAL;

    fn call(idx: u32) -> Vec<u8> {
        let mut v = vec![op::CALL];
        v.extend_from_slice(&idx.to_le_bytes());
        v
    }

    fn run_module(module: &Module) -> (RunExit, TestHost) {
        let mut interp = Interpreter::new(module, TestHost::default());
        let exit = interp.run_to_completion();
        let host = std::mem::take(&mut interp.host);
        (exit, host)
    }

    #[test]
    fn iterative_fibonacci() {
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(i4)", None, SI, 1, 0, vec![]);
        #[rustfmt::skip]
        let fib_code = vec![
            0x16, 0x0A,             // a = 0
            0x17, 0x0B,             // b = 1
            0x16, 0x0C,             // i = 0
            0x2B, 0x0C,             // br.s check
            0x06, 0x07, 0x58, 0x0D, // t = a + b
            0x07, 0x0A,             // a = b
            0x09, 0x0B,             // b = t
            0x08, 0x17, 0x58, 0x0C, // i++
            0x08, 0x02, 0x32, 0xF0, // if i < n goto body
            0x06, 0x2A,             // return a
        ];
        let fib = b.add_method("Demo::Fib(i4)", None, S, 1, 4, fib_code);
        let mut main_code = vec![0x1F, 10];
        main_code.extend(call(fib));
        main_code.extend(call(wl));
        main_code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, main_code);
        b.set_entry(main);
        let module = b.build();
        let (exit, host) = run_module(&module);
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "55\n");
    }

    #[test]
    fn recursive_fibonacci() {
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(i4)", None, SI, 1, 0, vec![]);
        // F is method index 1 (wl is 0)
        let f_idx = 1u32;
        #[rustfmt::skip]
        let mut f_code = vec![
            0x02,                   // ldarg.0
            0x18,                   // ldc.i4.2
            0x2F, 0x02,             // bge.s recurse
            0x02, 0x2A,             // return n
        ];
        f_code.push(0x02);
        f_code.push(0x17);
        f_code.push(0x59);
        f_code.extend(call(f_idx));
        f_code.push(0x02);
        f_code.push(0x18);
        f_code.push(0x59);
        f_code.extend(call(f_idx));
        f_code.push(0x58);
        f_code.push(0x2A);
        let f = b.add_method("Demo::F(i4)", None, S, 1, 0, f_code);
        assert_eq!(f, f_idx);
        let mut main_code = vec![0x1F, 12];
        main_code.extend(call(f));
        main_code.extend(call(wl));
        main_code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, main_code);
        b.set_entry(main);
        let (exit, host) = run_module(&b.build());
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "144\n");
    }

    #[test]
    fn string_concat_and_writeline() {
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(string)", None, SI, 1, 0, vec![]);
        let concat = b.add_method("System.String::Concat(string,string)", None, SI, 2, 0, vec![]);
        let s1 = b.string("Hello, ");
        let s2 = b.string("RustNet!");
        let mut code = vec![op::LDSTR];
        code.extend_from_slice(&s1.to_le_bytes());
        code.push(op::LDSTR);
        code.extend_from_slice(&s2.to_le_bytes());
        code.extend(call(concat));
        code.extend(call(wl));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_entry(main);
        let (exit, host) = run_module(&b.build());
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "Hello, RustNet!\n");
    }

    #[test]
    fn objects_fields_and_instance_calls() {
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(i4)", None, SI, 1, 0, vec![]);
        let counter = b.add_type("Demo.Counter", 1);
        // ctor(this, v): this.f0 = v
        let mut ctor_code = vec![0x02, 0x03, op::STFLD];
        ctor_code.extend_from_slice(&0u32.to_le_bytes());
        ctor_code.push(op::RET);
        let ctor = b.add_method("Demo.Counter::.ctor(i4)", Some(counter), MFLAG_CTOR, 1, 0, ctor_code);
        // Inc(this): this.f0 = this.f0 + 1
        let mut inc_code = vec![0x02, 0x02, op::LDFLD];
        inc_code.extend_from_slice(&0u32.to_le_bytes());
        inc_code.extend_from_slice(&[0x17, 0x58, op::STFLD]);
        inc_code.extend_from_slice(&0u32.to_le_bytes());
        inc_code.push(op::RET);
        let inc = b.add_method("Demo.Counter::Inc()", Some(counter), 0, 0, 0, inc_code);
        // main: var c = new Counter(5); c.Inc(); WriteLine(c.f0)
        let mut code = vec![0x1B]; // ldc.i4.5
        code.push(op::NEWOBJ);
        code.extend_from_slice(&ctor.to_le_bytes());
        code.push(0x0A); // stloc.0
        code.push(0x06); // ldloc.0
        code.push(op::CALLVIRT);
        code.extend_from_slice(&inc.to_le_bytes());
        code.push(0x06);
        code.push(op::LDFLD);
        code.extend_from_slice(&0u32.to_le_bytes());
        code.extend(call(wl));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 1, code);
        b.set_entry(main);
        let (exit, host) = run_module(&b.build());
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "6\n");
    }

    #[test]
    fn arrays_sum_of_squares() {
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(i4)", None, SI, 1, 0, vec![]);
        #[rustfmt::skip]
        let mut code = vec![
            0x1F, 5,                            // ldc.i4.s 5
            op::NEWARR, 4, 0, 0, 0,             // newarr int32
            0x0A,                               // arr -> loc0
            0x16, 0x0B,                         // i = 0
            0x2B, 0x0A,                         // br.s check1
            0x06, 0x07, 0x07, 0x07, 0x5A, 0x9E, // arr[i] = i*i
            0x07, 0x17, 0x58, 0x0B,             // i++
            0x07, 0x06, 0x8E, 0x32, 0xF1,       // if i < len goto body1
            0x16, 0x0C,                         // sum = 0
            0x16, 0x0B,                         // i = 0
            0x2B, 0x0A,                         // br.s check2
            0x08, 0x06, 0x07, 0x94, 0x58, 0x0C, // sum += arr[i]
            0x07, 0x17, 0x58, 0x0B,             // i++
            0x07, 0x06, 0x8E, 0x32, 0xF1,       // if i < len goto body2
            0x08,                               // ldloc sum
        ];
        code.extend(call(wl));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 3, code);
        b.set_entry(main);
        let (exit, host) = run_module(&b.build());
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "30\n");
    }

    #[test]
    fn statics_and_cctor() {
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(i4)", None, SI, 1, 0, vec![]);
        let slot = b.alloc_static_slots(1);
        let mut cctor_code = vec![op::LDC_I4, 42, 0, 0, 0, op::STSFLD];
        cctor_code.extend_from_slice(&slot.to_le_bytes());
        cctor_code.push(op::RET);
        b.add_method("Demo::.cctor()", None, S, 0, 0, cctor_code);
        let mut code = vec![op::LDSFLD];
        code.extend_from_slice(&slot.to_le_bytes());
        code.extend(call(wl));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_entry(main);
        let (exit, host) = run_module(&b.build());
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "42\n");
    }

    #[test]
    fn gc_reclaims_garbage_strings() {
        let mut b = Builder::new();
        let concat = b.add_method("System.String::Concat(string,string)", None, SI, 2, 0, vec![]);
        let sa = b.string("a");
        let sb = b.string("b");
        // for (i = 0; i < 5000; i++) { Concat("a","b"); }
        let mut code = vec![0x16, 0x0A, 0x2B, 0x00];
        // body:
        let body_start = code.len();
        code.push(op::LDSTR);
        code.extend_from_slice(&sa.to_le_bytes());
        code.push(op::LDSTR);
        code.extend_from_slice(&sb.to_le_bytes());
        code.extend(call(concat));
        code.push(op::POP);
        code.extend_from_slice(&[0x06, 0x17, 0x58, 0x0A]);
        // check:
        let check = code.len();
        code[3] = (check - body_start) as u8;
        code.push(0x06);
        code.push(op::LDC_I4);
        code.extend_from_slice(&5000i32.to_le_bytes());
        let after = code.len() + 2;
        let delta = (body_start as i64 - after as i64) as i8;
        code.push(0x32);
        code.push(delta as u8);
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 1, code);
        b.set_entry(main);
        let module = b.build();
        let mut interp = Interpreter::new(&module, TestHost::default());
        let exit = interp.run_to_completion();
        assert_eq!(exit, RunExit::Completed);
        assert!(interp.heap.collections >= 1, "GC never ran");
        // Everything allocated in the loop is garbage once the app exits.
        interp.collect_garbage();
        assert_eq!(interp.heap.live_count(), 0, "heap leaked: {}", interp.heap.live_count());
    }

    #[test]
    fn breakpoint_pause_and_resume() {
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(i4)", None, SI, 1, 0, vec![]);
        let mut code = vec![0x17, 0x18, 0x58]; // 1 + 2
        code.extend(call(wl));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_entry(main);
        let module = b.build();
        let mut interp = Interpreter::new(&module, TestHost::default());
        interp.set_breakpoint(main, 2);
        match interp.run(10_000) {
            RunExit::Paused { method, il_offset } => {
                assert_eq!(method, main);
                assert_eq!(il_offset, 2);
            }
            other => panic!("expected pause, got {other:?}"),
        }
        let trace = interp.stack_trace();
        assert_eq!(trace[0].0, "Demo::Main()");
        // Single-step once, then run to the end.
        interp.single_step = true;
        assert!(matches!(interp.run(10_000), RunExit::Paused { .. }));
        assert_eq!(interp.run(10_000), RunExit::Completed);
        assert_eq!(interp.host.console, "3\n");
    }

    #[test]
    fn hal_intrinsic_routes_to_host() {
        let mut b = Builder::new();
        let gpio = b.add_method("RustNet.Hal.Gpio::Write(i4,bool)", None, SI, 2, 0, vec![]);
        let mut code = vec![0x1F, 13, 0x17];
        code.extend(call(gpio));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_entry(main);
        let (exit, host) = run_module(&b.build());
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.calls.len(), 1);
        assert_eq!(host.calls[0].0, "RustNet.Hal.Gpio::Write(i4,bool)");
        assert_eq!(host.calls[0].1, vec![HostValue::I32(13), HostValue::I32(1)]);
    }

    #[test]
    fn rnx_binary_roundtrip() {
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(i4)", None, SI, 1, 0, vec![]);
        let mut code = vec![0x1F, 7, 0x1F, 6, 0x5A];
        code.extend(call(wl));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_entry(main);
        let module = b.build();
        let bytes = module.to_bytes();
        let loaded = Module::from_bytes(&bytes).expect("reload failed");
        let (exit, host) = run_module(&loaded);
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "42\n");
    }

    #[test]
    fn rnx_v4_resources_roundtrip() {
        let mut b = Builder::new();
        let main = b.add_method("Demo::Main()", None, S, 0, 0, vec![op::RET]);
        b.set_entry(main);
        b.add_resource("logo.gif", vec![0x47, 0x49, 0x46, 1, 2, 3]);
        b.add_resource("config.xml", b"<x/>".to_vec());
        let module = Module::from_bytes(&b.build().to_bytes()).expect("reload failed");
        assert_eq!(module.resource("logo.gif"), Some([0x47, 0x49, 0x46, 1, 2, 3].as_slice()));
        assert_eq!(module.resource("config.xml"), Some(b"<x/>".as_slice()));
        assert_eq!(module.resource("missing"), None);
    }

    #[test]
    fn sleep_intrinsic_advances_host_clock() {
        let mut b = Builder::new();
        let sleep = b.add_method("RustNet.Threading.Sleep::Ms(i4)", None, SI, 1, 0, vec![]);
        let mut code = vec![op::LDC_I4, 0xF4, 0x01, 0, 0]; // 500
        code.extend(call(sleep));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_entry(main);
        let (exit, host) = run_module(&b.build());
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.slept_ms, 500);
    }

    #[test]
    fn virtual_dispatch_inheritance_and_isinst() {
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(string)", None, SI, 1, 0, vec![]);
        let t_animal = b.add_type("Demo.Animal", 0);
        let t_dog = b.add_type("Demo.Dog", 0);
        b.set_parent(t_dog, t_animal);
        let s_generic = b.string("generic");
        let s_woof = b.string("Woof");
        let s_is = b.string("is-animal");
        // Animal::Speak (virtual root): prints "generic"
        let mut sp = vec![op::LDSTR];
        sp.extend_from_slice(&s_generic.to_le_bytes());
        sp.extend(call(wl));
        sp.push(op::RET);
        let animal_speak = b.add_method("Demo.Animal::Speak()", Some(t_animal), 0, 0, 0, sp);
        // Dog::Speak overrides Animal::Speak: prints "Woof"
        let mut sp = vec![op::LDSTR];
        sp.extend_from_slice(&s_woof.to_le_bytes());
        sp.extend(call(wl));
        sp.push(op::RET);
        let dog_speak = b.add_method("Demo.Dog::Speak()", Some(t_dog), 0, 0, 0, sp);
        b.set_slot(dog_speak, animal_speak);
        b.add_override(t_dog, animal_speak, dog_speak);
        let dog_ctor =
            b.add_method("Demo.Dog::.ctor()", Some(t_dog), MFLAG_CTOR, 0, 0, vec![op::RET]);
        // main: var d = new Dog(); ((Animal)d).Speak(); if (d is Animal) print
        let mut code = vec![op::NEWOBJ];
        code.extend_from_slice(&dog_ctor.to_le_bytes());
        code.push(0x0A); // stloc.0
        code.push(0x06); // ldloc.0
        code.push(op::CALLVIRT);
        code.extend_from_slice(&animal_speak.to_le_bytes());
        code.push(0x06); // ldloc.0
        code.push(0x75); // isinst Animal
        code.extend_from_slice(&(t_animal as u32).to_le_bytes());
        code.extend_from_slice(&[0x2C, 10]); // brfalse.s -> ret
        code.push(op::LDSTR);
        code.extend_from_slice(&s_is.to_le_bytes());
        code.extend(call(wl));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 1, code);
        b.set_entry(main);
        // Round-trip through v3 bytes so serialization is covered too.
        let module = Module::from_bytes(&b.build().to_bytes()).expect("v3 roundtrip");
        let (exit, host) = run_module(&module);
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "Woof\nis-animal\n");
    }

    #[test]
    fn exception_filters_select_matching_handler() {
        use crate::rnx::{EhClause, EH_FILTER};
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(string)", None, SI, 1, 0, vec![]);
        let boom = b.string("boom");
        let s2 = b.string("caught2");
        let sd = b.string("done");
        let mut code = Vec::new();
        code.push(op::LDSTR); // 0: throw "boom"       try = [0, 6)
        code.extend_from_slice(&boom.to_le_bytes());
        code.push(op::THROW); // 5
        code.push(op::POP); // 6: filter1 [6,10): reject (verdict 0)
        code.push(0x16); // 7: ldc.i4.0
        code.extend_from_slice(&[0xFE, 0x11]); // 8: endfilter
        code.push(op::POP); // 10: handler1 [10,13): never runs
        code.extend_from_slice(&[op::LEAVE_S, 17]); // 11: leave -> 30
        code.push(op::POP); // 13: filter2 [13,17): accept (verdict 1)
        code.push(0x17); // 14: ldc.i4.1
        code.extend_from_slice(&[0xFE, 0x11]); // 15: endfilter
        code.push(op::POP); // 17: handler2 [17,30): print "caught2"
        code.push(op::LDSTR); // 18
        code.extend_from_slice(&s2.to_le_bytes());
        code.extend(call(wl)); // 23
        code.extend_from_slice(&[op::LEAVE_S, 0]); // 28: leave -> 30
        code.push(op::LDSTR); // 30: print "done"
        code.extend_from_slice(&sd.to_le_bytes());
        code.extend(call(wl));
        code.push(op::RET);
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_eh(
            main,
            vec![
                EhClause { kind: EH_FILTER, try_start: 0, try_end: 6, handler_start: 10, handler_end: 13, filter_start: 6 },
                EhClause { kind: EH_FILTER, try_start: 0, try_end: 6, handler_start: 17, handler_end: 30, filter_start: 13 },
            ],
        );
        b.set_entry(main);
        let (exit, host) = run_module(&b.build());
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "caught2\ndone\n");
    }

    #[test]
    fn try_catch_finally_runs_handlers_in_order() {
        use crate::rnx::{EhClause, EH_CATCH, EH_FINALLY};
        // Roslyn lowering of try { throw } catch { } finally { }:
        // an inner catch clause wrapped by a finally clause, with `leave`
        // as the LAST instruction of the catch handler (regression: EH
        // range checks must use the instruction start, not the advanced ip).
        let mut b = Builder::new();
        let wl = b.add_method("System.Console::WriteLine(string)", None, SI, 1, 0, vec![]);
        let boom = b.string("boom");
        let sc = b.string("C");
        let sf = b.string("F");
        let sd = b.string("D");
        let mut code = Vec::new();
        code.push(op::LDSTR); // 0: ldstr "boom"
        code.extend_from_slice(&boom.to_le_bytes());
        code.push(op::THROW); // 5              (inner try = [0, 6))
        code.push(op::POP); // 6: catch handler: discard exception
        code.push(op::LDSTR); // 7
        code.extend_from_slice(&sc.to_le_bytes());
        code.extend(call(wl)); // 12
        code.extend_from_slice(&[op::LEAVE_S, 11]); // 17: leave -> 30 (catch = [6, 19))
        code.push(op::LDSTR); // 19: finally handler
        code.extend_from_slice(&sf.to_le_bytes());
        code.extend(call(wl)); // 24
        code.push(op::ENDFINALLY); // 29        (finally handler = [19, 30))
        code.push(op::LDSTR); // 30: after try/catch/finally
        code.extend_from_slice(&sd.to_le_bytes());
        code.extend(call(wl)); // 35
        code.push(op::RET); // 40
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_eh(
            main,
            vec![
                EhClause { kind: EH_CATCH, try_start: 0, try_end: 6, handler_start: 6, handler_end: 19, filter_start: 0 },
                EhClause { kind: EH_FINALLY, try_start: 0, try_end: 19, handler_start: 19, handler_end: 30, filter_start: 0 },
            ],
        );
        b.set_entry(main);
        let (exit, host) = run_module(&b.build());
        assert_eq!(exit, RunExit::Completed);
        assert_eq!(host.console, "C\nF\nD\n");
    }

    #[test]
    fn divide_by_zero_is_reported_with_stack_trace() {
        let mut b = Builder::new();
        let code = vec![0x17, 0x16, 0x5B, 0x2A]; // 1 / 0
        let main = b.add_method("Demo::Main()", None, S, 0, 0, code);
        b.set_entry(main);
        let (exit, _) = run_module(&b.build());
        match exit {
            RunExit::Error(e) => {
                assert!(e.contains("DivideByZeroException"), "{e}");
                assert!(e.contains("Demo::Main()"), "{e}");
            }
            other => panic!("expected error, got {other:?}"),
        }
    }
}
