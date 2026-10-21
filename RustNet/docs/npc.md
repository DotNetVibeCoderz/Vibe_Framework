# NPC — Native Procedure Caller

Call native Rust code from a C# app, loaded onto the device at run time with
no firmware rebuild — RustNet's analogue of TinyCLR's RLP. Use it for the
tight loops the interpreter is too slow for: a per-byte checksum or a
per-pixel filter costs on the order of 100 µs a byte in IL and nanoseconds
natively.

## The ABI

Every exported procedure has one C shape:

```rust
#[no_mangle]
pub unsafe extern "C" fn my_proc(args: *const u8, args_len: u32, out: *mut u8, out_cap: u32) -> i32
```

It returns the number of bytes written to `out`, or a negative error code
(which reaches C# as an exception naming the code). Arguments and results are
byte arrays, as on every RustNet host call; wider values travel little-endian.
`runtime/npc-sample` is a complete module (`rustnet_sum_u32`, `rustnet_crc32`,
`rustnet_reverse`, `rustnet_fail`).

## Build, sign, push

```bash
rustnet native build runtime/npc-sample              # prints the cdylib path
rustnet native push target/debug/rustnet_npc_sample.dll --name sample \
        --key keys/rustnet-signing.key --device tcp:127.0.0.1:7878
```

`push` seals the module as an RNSB container of kind **Native** (4) and stores
it as `/data/native/<name>.rnsb`. `rustnet native seal` writes the container
to a file instead.

## Call it from C#

```csharp
using RustNet.Native;

if (Npc.IsSupported())
{
    var crc = new NativeProcedure("sample", "rustnet_crc32");
    byte[] result = crc.Invoke(Encoding.UTF8.GetBytes("123456789"));   // CBF43926, LE
    int sum = new NativeProcedure("sample", "rustnet_sum_u32").InvokeInt32(new[] { 40, 2 });
}
```

`Npc.Load(module, procedure)` returns a handle and `Npc.Invoke(handle, args,
maxOutput)` calls it; `NativeProcedure` wraps the two.

## Security

The module is verified against the device's provisioned key **when an app
loads it**, not when it is uploaded, so the data channel that carries it needs
no trust of its own. A module signed by any other key is refused
(`signature check failed`), as is a container of another kind or for another
chip. Signing controls *who* may load native code, not *what* it does: native
code runs unsandboxed and can fault the device.

## Where it runs

| Target | NPC |
|---|---|
| Host / virtual device | yes — the module is loaded as a dynamic library (feature `npc`, on in `chip-host`) |
| Meadow F7 (Cortex-M7) | yes — an RNPC module placed in SRAM1/2 (**verified on a Meadow F7**) |
| RP2040 (Cortex-M0+) | yes — a `thumbv6m` RNPC module placed in SRAM, sealed with `--chip rp2040` (**verified on a Pico**) |
| ESP32 (Xtensa) | yes — an RNPC v2 module, code in IRAM, data in DRAM (**verified on an M5Stack Core2**) |
| XIP-only / pure Harvard chips | never — `Npc.IsSupported()` is false and `Load` throws `NotSupported` |

## Modules for microcontrollers (RNPC)

A microcontroller has no dynamic loader, so its modules are **RNPC**: machine
code and data linked at address 0, plus the list of places that hold an
absolute address. Write the module as a `no_std` Rust staticlib
(`runtime/npc-sample-mcu` is a template — `crate-type = ["staticlib"]`, a
`#[panic_handler]`, `#[no_mangle] pub unsafe extern "C"` procedures) and:

```bash
rustnet native build runtime/npc-sample-mcu --target thumbv6m-none-eabi      # RP2040
rustnet native build runtime/npc-sample-mcu --target thumbv7em-none-eabihf   # Meadow F7
rustnet native build runtime/npc-sample-mcu --target xtensa-esp32-none-elf    # ESP32 (needs the `esp` toolchain)
rustnet native push <printed .rnpc path> --name sample --key keys/rustnet-signing.key \
        --chip stm32 --device serial:COMn                                     # rp2040 for the Pico
```

`native build` compiles the staticlib, finds the crate's own exported
functions with `llvm-nm`, links them with the toolchain's `rust-lld` at
address 0 keeping the relocations (`--emit-relocs`, the script in
`NpcModule.LinkerScript`), and converts the ELF: every `R_ARM_ABS32` word and
every Thumb-2 `movw`/`movt` pair (the precompiled `core` for Cortex-M4/M7
builds addresses that way) becomes a relocation entry; PC-relative relocations
need nothing because the module moves as a whole; anything else is refused by
name. On the device, `rustnet-npc` copies the image into RAM, zeroes `.bss`,
adds the load address to each entry and calls the export — Thumb bit and all.

**The ESP32 is different in one way that shapes its format.** Instruction RAM
is executable but takes only 32-bit loads and stores, so constant tables read a
byte at a time cannot live there. ESP32 modules are therefore **RNPC version
2**: linked with code (and its literal pools) at 0 and data at `0x1000_0000`,
each absolute word (`R_XTENSA_32`) recorded with the segment it lives in and the
segment it points into; `L32R`/`CALL` operands are PC-relative and must stay
inside the code. The firmware takes executable IRAM from ESP-IDF's capability
allocator, relocates the code in a DRAM staging buffer and copies it across a
word at a time. The `esp` toolchain builds the module with `-Zbuild-std=core`
(there is no prebuilt Xtensa `core`), and its GNU linker does the link.

Before a board is involved, `runtime/rustnet-npc/tools/emulate.py` places a
module with the real Rust loader at two different addresses and runs every
procedure on an emulated Cortex-M (Unicorn), checking the same results as the
host test. (Unicorn has no Xtensa, so the ESP32 path is checked on the board.) The C# app `runtime/npc-sample-mcu/demo/NpcDemo` checks them on a
board.

Verified by the `LibAppFeatureMatrix` and `NativeModuleSignedByAnotherKeyIsRefused`
end-to-end tests.
