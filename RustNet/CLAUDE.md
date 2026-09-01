# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What RustNet is

RustNet runs **C#/.NET applications on microcontrollers** via a runtime written in **Rust**. The pipeline: C# app → `dotnet build` → DLL → **MetadataProcessor** rewrites metadata tokens into a compact **RNX** module → RSA-signed **RNSB** container → flashed over the **RNDP** protocol → executed by the Rust IL interpreter on the device. `requirements.md` is the original product spec (Indonesian); `docs/architecture.md` is the authoritative architecture description.

## Commands

```bash
# Rust workspace (runtime + firmware) — 155 tests
cargo test --workspace
cargo test -p rustnet-core                  # just the IL interpreter
cargo test -p rustnet-core iterative_fib    # single test by name
cargo build -p rustnet-firmware             # host "virtual device" binary
cargo build -p rustnet-firmware --no-default-features --features chip-esp32  # chip variant (no `image` codecs — Xtensa LLVM can't compile them)
cargo build -p rustnet-core --no-default-features --target riscv32imc-unknown-none-elf  # bare-metal profile

# K210 bare-metal firmware (separate workspace, riscv64gc) — from runtime/firmware-k210/
cargo build --release                                   # Sipeed Maix Go, blink demo
rust-objcopy -O binary target/riscv64gc-unknown-none-elf/release/rustnet-firmware-k210 fw.bin
kflash -p COMn -b 1500000 fw.bin

# ESP32 Xtensa firmware (separate workspace, `esp` toolchain, target-dir C:/rnesp) — from runtime/firmware-esp32/
cargo build --release                                   # generic ESP32 DevKit (UART0 RNDP)
cargo build --release --features board-m5tough          # M5Stack Tough: AXP192 PMIC + ILI9342C panel + PSRAM
# Flash WITH the custom partition table so the FAT "storage" partition exists —
# without it the device falls back to in-RAM MemFs and apps/provisioning/autostart
# do not survive reboot:
espflash flash <elf> --partition-table runtime/firmware-esp32/partitions.csv --port COMn

# .NET (libraries + tools + tests) — from dotnet/
dotnet build dotnet/RustNet.slnx
dotnet test dotnet/RustNet.slnx             # includes E2E (needs firmware binary in target/)
                                            # Debug only: `--configuration Release` makes the E2E
                                            # LangApp crash — the Release compiler lowers async
                                            # state machines into a shape the interpreter lacks
dotnet test dotnet/tests/RustNet.Tests/RustNet.Tests.csproj   # what CI runs: the solution also holds
                                            # RustNet.Designer (WPF, net10.0-windows), which cannot
                                            # build on Linux at all
cargo build -p rustnet-firmware             # ALWAYS rebuild host variant before dotnet test:
                                            # E2E asserts chip "host-sim"; a chip-variant build
                                            # left in target/debug makes it fail

# VSCode extension — from vscode-extension/
npm install && npm run compile

# Dev utility: run an .rnx directly on the interpreter with a permissive host
cargo run -p rustnet-core --example run_rnx -- app.rnx
```

Manual smoke flow (all tools work against the virtual device):
```bash
./target/debug/rustnet-firmware --ephemeral        # or --port N --home dir
rustnet=dotnet/tools/RustNet.Cli/bin/Debug/net10.0/rustnet.exe
$rustnet keys generate --out keys && $rustnet provision --key keys/rustnet-signing.pub
$rustnet flash <app.dll> --name x --key keys/rustnet-signing.key --start
$rustnet logs -n 100        # or --follow; also: info, apps list, display capture, ota push...
```

## Architecture — the load-bearing contracts

Three byte-level contracts are each implemented twice (Rust device side, C# tool side) and MUST stay in sync:

1. **RNX module format** (v6: per-type custom attributes; v5: reflection field descriptors; v4: embedded resources) — `runtime/rustnet-core/src/rnx.rs` (reader/builder) ↔ `dotnet/tools/RustNet.MetadataProcessor/RnxCompiler.cs` (writer). All IL inline tokens are 4 bytes and are rewritten **in place** to 4-byte indices, so branch offsets never change.
2. **RNDP protocol frames + commands** — `runtime/firmware/src/proto.rs` ↔ `dotnet/tools/RustNet.Deploy/Rndp.cs` (CRC-16/CCITT-FALSE; spec in `docs/protocol.md`).
3. **RNSB signed containers** — `runtime/rustnet-secureboot/src/lib.rs` (verify) ↔ `dotnet/tools/RustNet.Deploy/Signing.cs` (seal). RSA PKCS#1 v1.5 + SHA-256 over header-with-zeroed-siglen + payload.

A fourth string-level contract: **canonical method names** `Ns.Type::Name(i4,string,...)`. The MetadataProcessor emits them (`SignatureProvider.cs`); the interpreter's intrinsic dispatch (`runtime/rustnet-core/src/intrinsics.rs` for corlib: Console/String/Math/Random) and the firmware host (`runtime/firmware/src/apphost.rs` for `RustNet.*`: HAL/FS/Display/WiFi/MQTT) match on them. Adding a C# API in `dotnet/src/RustNet.*` requires a matching arm in `apphost.rs` with the exact canonical name.

Other structure worth knowing:
- `runtime/rustnet-hal` traits are the chip abstraction; `rustnet-hal-host` is the simulator every test and the virtual device use. Real-silicon bring-up plugs into `runtime/firmware/src/chip.rs` (feature-gated `chip-*`).
- **Bare-metal ports** live in their own excluded workspaces and pair a register-level HAL crate (no deps beyond `rustnet-hal`, so it stays in the host test run) with a firmware binary: `rustnet-hal-stm32` + `firmware-stm32` (Cortex-M4F) and `rustnet-hal-k210` + `firmware-k210` (K210 RV64GC, Sipeed Maix Go) — **both verified on hardware**. K210 traps worth carrying forward: `mstatus.FS` is `Off` at reset so the FPU must be enabled in `#[pre_init]` or every `f64` traps as an illegal instruction; `ctrlr0`'s layout *moves* between controllers (tmod at bits 10..11 on SPI3 vs 8..9 on SPI0/1, work-mode at 8 vs 6, frame size at bit 0 vs 16); and the Maix panel is driven by **octal** SPI0 with its data lines switched onto the DVP pins by `sysctl.misc.spi_dvp_data_enable`, not by FPIOA.
- **Bare-metal graphics and files**: `rustnet-gfx` builds `no_std + alloc` (`--no-default-features`), so a real panel sits behind the same `Framebuffer` the virtual device draws into. `rustnet-fs` is std-bound (fatfs), so bare metal uses **`rustnet-flashfs`** instead — named blobs in a log over raw NOR, backing `RustNet.IO.FileSystem`. Both are workspace members, so their logic is covered by `cargo test --workspace`.
- **NOR flash discipline, learned the expensive way**: programming only *clears* bits, so writing into a region that is not fully erased silently ANDs into it and the damage surfaces after the next power cycle. Check blankness over the **whole span** a record will occupy, not its first bytes, and read records back after writing. Conversely, compaction must erase only the **used** span rounded to sectors — erasing a whole multi-megabyte window takes long enough to look like a dead device.
- **K210 panel**: the MaixLCD is driven by SPI0 in octal frame format, and `spi_ctrlr0` is configured **per transfer, not per driver** — a command is an 8-bit instruction (`0x202`), its parameters 8-bit units (`0x00A`), pixels 32-bit units (`0x022`). One value for all three gives a panel that wakes, lights its backlight and then shows a flat colour forever with every call returning `Ok`. The frame must also go out in a *single* transfer, or the image comes out rotated horizontally. Both facts come from mirroring MaixPy's `components/drivers/lcd/src/st7789.c`; when a vendor's firmware drives hardware yours does not, read its driver source early.
- **Interpreter cost model** (measured on the K210 at 390 MHz): a host call is ~220 µs because dispatch matches the canonical name as a string, and a *managed static method call* is ~65 µs — only 3× cheaper. So per-pixel work in C# is out, per-cell host calls are out, and extracting a one-line helper into a method inside a hot loop can cost more than the work it factors out.
- **Physical display path**: `Display.Present()` renders into an in-memory `Framebuffer` (`rustnet-gfx`); the apphost calls `Board::present_frame(rgb565, w, h)` (default no-op — the virtual device is captured by tools instead). A board with a wired panel overrides it. First real panel: the **M5Stack Tough** (`runtime/firmware-esp32/src/board.rs`, feature `board-m5tough`) — AXP192 PMIC over I²C powers the LCD rails/backlight, then an ILI9342C driver streams the framebuffer over SPI2 (manual CS/DC; frames go out by DMA in 40-row bands, copied PSRAM→a DMA-capable internal bounce buffer first because SPI DMA cannot read PSRAM directly). The 320×240 RGB565 framebuffer is 150 KB, so that build enables PSRAM in `sdkconfig.defaults` (`IGNORE_NOTFOUND` keeps PSRAM-less boards booting). A 320×240 framebuffer cannot fit contiguous ESP32 DRAM — PSRAM is mandatory.
- The firmware's `DeviceService` (`service.rs`) implements every RNDP command; `AppRunner` (`apphost.rs`) runs apps in interpreter fuel slices on a thread (stoppable, profiled, debugger pause/resume).
- **Source-level debugging** (`docs/debugging.md`): the MetadataProcessor emits the RNX debug section (PDB sequence points → IL/line); RNDP carries debug commands (set/clear BP, continue, step, state, stack, locals); `rustnet-debugger` is a DAP adapter (maps source⇄IL via `RnxDebugInfo`) that VSCode launches to debug on the (virtual) device. On-device E2E: `DebuggerBreakpointCycle`.
- Runtime v0.4 (RNX v3) supports the full language core: try/catch/finally + `catch when` filters, inheritance + interfaces + virtual dispatch (override tables; `ToString` overrides work), user generics (erasure), async/await (Debug-config class state machines; task continuations run as green threads), delegates/lambdas, BCL collections + foreach, LINQ subset, `StringBuilder`, `Regex`, string interpolation, plus the v0.3 device surface (CAN/Modbus/1-Wire, Ethernet/PPP/Cellular, SQL db, power/RTC/watchdog/extmem, signal control, JSON/XML/binary serializers, streams, XML-loadable UI). Full support matrix: `docs/dotnet-support.md`. Remaining limits (MetadataProcessor errors clearly): reflection is partial — `object.GetType()` + `Type.Name/FullName/Namespace/BaseType/ToString`, method enumeration (`GetMethods`/`GetMethod`/`MethodInfo.Name`), `typeof(T)` (`ldtoken`+`GetTypeFromHandle`, identity = `GetType()`) `MethodInfo.Invoke` (boxed `object[]` args, void returns null) and field enumeration (`GetFields`/`GetField`, `FieldInfo.Name/GetValue/SetValue`, public fields, RNX v5 descriptors) property enumeration (`GetProperties`/`GetProperty`, `PropertyInfo.Name/GetValue/SetValue`, via `get_`/`set_` accessors) and **type-level custom attributes** (`Type.GetCustomAttributes`, RNX v6 stores ctor + positional/named const args; instantiated on read) work (v0.9), but attributes on methods/fields and `ldtoken` of a method/field (array initializers) do not; catch clauses are untyped (discriminate with `when` on `ex.Message`), no Release-config async, entry must be `static void Main()`. **`string.Concat` with 5+ arguments** now works (the inline-array `ReadOnlySpan` lowering is modelled — `initobj <>y__InlineArrayN<T>` allocates a heap array, `InlineArray*` helpers are interpreter intrinsics). Byref (`ref`) is same-frame only: never pass `ref` locals into another managed method.
- Adding a `RustNet.*` API: C# `[InternalCall]` stub in `dotnet/src/RustNet.X` + matching arm in `runtime/firmware/src/apphost.rs` (exact canonical name), backed by a `rustnet-hal` trait + `rustnet-hal-host` simulator so the virtual device and tests exercise it. Byte-array marshalling is the only array channel across the host boundary — pack wider types (u16/u32) little-endian and decode in the C# wrapper (see `Signal`/`Modbus`).
- Templates are instantiated by `rustnet new` with `__NAME__` token replacement; they resolve `RustNet.*` libs via the `RUSTNET_SDK` env var (repo root). Template csprojs are standalone (no `dotnet/Directory.Build.props`), so they must set `ImplicitUsings` themselves.

## Gotchas

- `dotnet/RustNet.slnx` (not .sln); .NET 10 SDK required.
- xunit E2E tests auto-locate `target/{debug,release}/rustnet-firmware.exe` by walking up from the test bin dir (`RUSTNET_FIRMWARE` env overrides) and skip if absent.
- After changing Rust interpreter intrinsics, rebuild the firmware binary before re-running .NET E2E — the tests run the compiled exe, not the source.
- PowerShell 5.1 quirks in this environment: no `&&`, prefer separate commands or bash tool.
