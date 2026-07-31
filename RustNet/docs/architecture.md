# RustNet Architecture

## The four layers

1. **Rust runtime (on device)** — `runtime/` workspace
   - `rustnet-core`: loads **RNX** modules and interprets ECMA-335 IL
     (arithmetic, branching, calls, objects, arrays, strings, statics),
     mark-sweep GC, breakpoint/single-step hooks, host-call marshalling.
   - `rustnet-hal` (+ `rustnet-hal-host` simulator): unified traits for
     GPIO, I2C, SPI, UART, I2S, PWM, ADC, power, delay.
   - `rustnet-sched`: single-threaded cooperative async executor + timers
     + events (tickless-friendly: `next_deadline_ms`).
   - `rustnet-crypto` / `rustnet-secureboot` / `rustnet-ota`: AES-CTR,
     SHA-2, HMAC, RSA; RNSB signed-image container; A/B slots with
     boot-attempt rollback.
   - `rustnet-fs`: VFS trait, RAM FS, FAT (via `fatfs`) over any block
     medium, AES-CTR encrypted overlay.
   - `rustnet-net`: HTTP/1.1 client + micro web server, MQTT 3.1.1
     client, TLS provider hook.
   - `rustnet-gfx`: RGB565 framebuffer, primitives, 8x8 font, double
     buffering with dirty-rect flush, SSD1306/ST7735 drivers.
   - `rustnet-usb`: descriptors, CDC/HID/MSC device classes, host
     enumeration with pluggable drivers, in-memory sim bus.
   - `firmware`: ties it together — `DeviceService` handles RNDP
     commands, `AppRunner` executes managed apps in fuel slices,
     `FirmwareHost` binds `RustNet.*` internal calls to services.

2. **C# class libraries (on device, compiled into apps)** — `dotnet/src/`
   Internal-call stubs marked `[InternalCall]` (Gpio, Adc, Pwm, I2c,
   FileSystem, Display, Wifi, Mqtt, Http, Log, Sleep, Uptime, Power) plus
   real managed code (RustNet.Devices drivers, Display.DrawCircle, ...)
   that runs on the interpreter.

3. **Desktop tooling** — `dotnet/tools/`
   - **MetadataProcessor**: DLL → RNX. Rewrites metadata tokens into
     direct table indices (method/string/field-slot/type), merges the app
     with referenced RustNet.* assemblies, canonicalizes method names as
     `Ns.Type::Name(i4,string,...)` — the contract with the runtime's
     intrinsic dispatch.
   - **Deploy**: RNDP client (TCP + serial), RSA signing byte-compatible
     with the Rust verifier.
   - **CLI** (`rustnet`), **Workbench** (Avalonia), **VSCode extension**.

4. **Ecosystem** — `rustnet pkg` (rnpkg zip + local/share registry),
   templates, community driver model.

## Execution pipeline

```
app.csproj ─dotnet build→ app.dll (+RustNet.*.dll)
  ─rustnet build→ app.rnx           (MetadataProcessor)
  ─rustnet flash→ RNSB container    (RSA-PKCS1-SHA256 signature)
  ─RNDP FLASH_APP→ device verifies signature + chip match + RNX parse
  ─RNDP START_APP→ AppRunner thread: Interpreter::run(fuel slices)
       intrinsics → FirmwareHost → HAL/FS/Net/Gfx services
```

## Security model

- **Provisioning**: `CMD_PROVISION_KEY` stores the RSA public key once
  (eFuse/OTP on real silicon).
- **Secure boot / flash**: every app and firmware image is an RNSB
  container; signature covers header + payload; chip family must match.
- **OTA**: streamed into the inactive A/B slot, verified before staging;
  unconfirmed boots roll back after 3 attempts.
- **Encrypted storage**: device config is AES-CTR encrypted with a
  device-unique key; the CLI/Workbench never see it in plaintext at rest.

## RNX format

See `runtime/rustnet-core/src/rnx.rs` for the authoritative byte layout
(strings / types / methods tables + entry point + optional debug section).
Design choice: all IL inline tokens are 4 bytes and are rewritten in place
to 4-byte indices, so branch offsets never change and the processor does
no fixups.

## Chip bring-up roadmap

`runtime/firmware/src/chip.rs` selects a chip feature (`chip-esp32`,
`chip-stm32`, `chip-ti`, `chip-nxp`, `chip-host`). Today every variant
compiles the full service stack against the simulator board; bringing up
real silicon means implementing the `rustnet_hal::Board` traits with the
vendor PAC/SDK and swapping the RNDP transport to USB-CDC/UART. The rest
of the stack (interpreter, services, tools) is transport- and
board-agnostic by construction.

**Bare metal is a different shape.** `runtime/firmware` is `std`-bound
(threads, TCP, the filesystem), so a target with no OS underneath it gets
its own binary in its own Cargo workspace, pairing a register-level HAL
crate with a cooperative service loop that alternates between answering
the tools and handing the interpreter a slice of fuel. Each HAL crate
depends on nothing but `rustnet-hal`, which is what lets it stay inside
the host workspace and its test run while also building for the
bare-metal target:

| Port | HAL crate | Firmware | Target | State |
|---|---|---|---|---|
| STM32F4 (Cortex-M4F) | `rustnet-hal-stm32` | `runtime/firmware-stm32` | `thumbv7em-none-eabihf` | **verified on hardware** — Nucleo-F401RE and Netduino 3 WiFi |
| Kendryte K210 (RV64GC) | `rustnet-hal-k210` | `runtime/firmware-k210` | `riscv64gc-unknown-none-elf` | **verified on hardware** — Sipeed Maix Go |

What these ports carry is the interpreter, the HAL, `rustnet-rndp`'s
framing and `rustnet-secureboot`. The K210 additionally carries graphics
(`rustnet-gfx` builds `no_std + alloc`, so a real panel sits behind the
same `Framebuffer` the virtual device draws into) and files
(`rustnet-flashfs`, since `rustnet-fs` is std-bound through `fatfs`). What
neither carries is OTA or the on-device debugger, and on the STM32
persistence is still a log-structured set of fixed records in reserved
flash rather than paths.
See `docs/chips.md` for the per-chip detail and each firmware's README for
its own hard-won facts.

Runtime v0.2 (RNX format v2) adds: try/catch/finally (per-method
exception-handler tables in the RNX module; filters and fault handlers
remain unsupported), delegates/lambdas (`ldftn` + `Func`/`Action`
construction), generic collection intrinsics (`List<T>`,
`Dictionary<K,V>`, `Queue<T>`, `Stack<T>`, `KeyValuePair`, tuples, and
`foreach` enumerators), a LINQ-to-objects subset (`Where`/`Select`/
`Sum`/`Count`/`OrderBy`/...), `StringBuilder`, `Regex` (compact
backtracking engine), string interpolation
(`DefaultInterpolatedStringHandler`), `Convert`/`BitConverter`/
`Encoding`, and cooperative threads (`System.Threading.Thread`,
`Interlocked`, a `Task` subset). Generic instantiations canonicalize to
arity names (`System.Collections.Generic.List`1::Add(object)`) — the
runtime's dispatch is type-argument-agnostic.

Runtime v0.3 widens the device surface: field buses (CAN, Modbus RTU/TCP,
1-Wire), network interfaces (Ethernet/PPP/Cellular behind the
`NetInterface` HAL trait), an embedded SQL engine (`rustnet-db`,
memory/flash/SD/USB storage), power management (wake sources, shutdown,
wake reason), RTC, watchdog, external memory (QSPI/SDRAM), TinyCLR-style
signal control, JSON/XML/binary serializers, streams, an XML-loadable UI
toolkit, RISC-V chip families (ESP32-C3, K210) and the IO_STATE protocol
command feeding the VSCode simulator panel. Per-feature guides live in
`docs/` (protocols, networking, database, system, serialization, ui,
chips, simulator, dotnet-support).

Runtime v0.4 (RNX format v3) completes the language core: inheritance
with true virtual dispatch (per-type override tables + virtual slots,
inherited field layout, chain-walking casts), user interfaces, ToString/
Equals/GetHashCode overrides, user-defined generics via erasure,
`catch when` exception filters, and async/await (builder/awaiter
intrinsics over task objects whose continuations run as green threads;
Task.Delay backed by pure timer threads; deadlock detection when every
thread blocks without a timed wake).

Remaining limits (enforced with clear errors by the MetadataProcessor):
partial reflection (`GetType`/`typeof`/`Type` members, method/field/property
enumeration, `MethodInfo.Invoke`, and type-level custom attributes work;
attributes on methods/fields do not), no `ldtoken` of a method or field
(array initializers), no fault
handlers, catch clauses are untyped (use `when` filters), Release-configuration
async (struct state machines) unsupported, entry point must be
`static void Main()`. (Inline-array `ReadOnlySpan` params like `string.Concat`
with 5+ args are now modelled — the buffer becomes a heap array.)
