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
| ESP32 IRAM, Cortex-M / RP2040 SRAM | not yet: needs a position-independent blob and a relocating loader into executable RAM, per chip |
| XIP-only / pure Harvard chips | never — `Npc.IsSupported()` is false and `Load` throws `NotSupported` |

Verified by the `LibAppFeatureMatrix` and `NativeModuleSignedByAnotherKeyIsRefused`
end-to-end tests.
