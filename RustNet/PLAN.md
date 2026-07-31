# RustNet development roadmap

Authoritative plan; progress checkboxes live in [Progress.md](Progress.md).
Architecture background: [docs/architecture.md](docs/architecture.md).

## v0.1 — Foundation ✅ (shipped)

Rust runtime workspace (HAL, IL interpreter, GC, scheduler, crypto,
secure boot, OTA, FS, net, gfx, USB), RNX/RNDP/RNSB contracts, C# class
libraries + MetadataProcessor + Deploy, CLI/Workbench/VSCode tooling,
templates, virtual device, E2E test rig.

## v0.2 — Modern C# ✅ (shipped)

try/catch/finally (RNX v2 EH tables), delegates/lambdas/closures, BCL
generic collections + foreach, LINQ subset, StringBuilder, Regex engine,
string interpolation, Convert/BitConverter/Encoding, green threads +
Interlocked + Task subset, GC coverage for all new heap shapes.

## v0.3 — Peripherals, data & UX ✅ (this release)

- Field buses: CAN, Modbus RTU/TCP master (+ on-device sim slave), 1-Wire
- Networking: Ethernet, PPP, Cellular interfaces (`NetInterface` HAL)
- Embedded SQL database (memory / flash / SD / USB storage)
- Power (wake sources, shutdown, reset, wake reason), RTC, watchdog,
  external memory (QSPI/SDRAM), device info, TinyCLR-style signal control
- Serializers: JSON / XML / binary document model; streams
  (MemoryStream/FileStream/BinaryPacker); Timers
- UI toolkit (WPF/Glide-style element tree, XML-loadable) on the display
- RISC-V chip ids (ESP32-C3, K210) across firmware + tools; serial RNDP
- VSCode simulator panel (display, GPIO, buses, logs); `rustnet io`
- Templates: datalogger-db, can-gateway, ui-dashboard

## v0.4 — Language completeness ✅ (this release)

- [x] interface + virtual dispatch + inheritance (RNX v3: parent links,
      flattened interface lists, override tables, virtual slots; inherited
      field layout; `isinst`/`castclass` walk the chain; `ToString`/
      `Equals`/`GetHashCode` overrides dispatch dynamically)
- [x] user-defined generic types & methods (erased instantiation —
      arity-canonicalized, type arguments folded to object)
- [x] exception filters (`catch when`) — filter regions + `endfilter`
      with correct fall-through to the next clause
- [x] async/await (AsyncTaskMethodBuilder/TaskAwaiter intrinsics, task
      objects with continuations on the green-thread scheduler,
      Task.Delay timers, cooperative `Wait`/`Result`, exception flow
      through faulted tasks; Debug-configuration class state machines)
- [x] inline-array/span params lowering (string.Concat 5+ args) — modelled
      in v0.9 (buffer becomes a heap array; InlineArray* helpers are intrinsics)
- [ ] debugger: breakpoints/stepping surfaced in VSCode DAP (moved to
      v0.6 alongside sequence-point emission)

## v0.5 — Real silicon ✅ (ESP32 verified end-to-end)

- [x] `no_std + alloc` profile for the interpreter core and the HAL —
      both build for `riscv32imc-unknown-none-elf` (float math via libm;
      `std` stays the default feature for hosts)
- [x] RNDP byte-pipe transport: `serve_pipe` accepts any Read+Write;
      `--stdio` mode ships the USB-CDC/UART shape with an integration test
- [x] ESP32-C3 board crate (`rustnet-hal-esp32c3`): register-level GPIO +
      mcycle delay compile for bare-metal RISC-V
- [x] HIL rig (`tools/hil-smoke.sh`) + HIL stage 0 (`rustnet probe` ROM
      bootloader client) verified on an ESP32-WROOM-32
- [x] **ESP32 (Xtensa) runs RustNet** — `runtime/firmware-esp32` (std on
      ESP-IDF): provision → RSA-verified flash → C# apps incl. async/await
      executing on-chip; live GPIO from managed code
- [x] ESP32 phase 2 (on-chip verified): wear-levelled FAT storage (apps +
      provisioning survive reboots/reflashes), WiFi STA + RNDP TCP :7878
      (**fully wireless deploy** over the user's WLAN), ADC/PWM(LEDC)/I2C
      from C#, real `esp_restart` on CMD_REBOOT
- [x] ESP32 phase 3 (on-chip verified): UART1/2 drivers, TWAI→CAN
      (self-test round trip), RMT→Signal generate, SNTP real-time RTC;
      app-thread stack made configurable to survive WiFi heap fragmentation
- [~] ESP32 phase 4:
  - [x] **restored the ESP32 build** — the `image` crate (added in v0.7/8) broke
        the Xtensa build; it's now an optional `image-codecs` feature, off for
        ESP32. The Xtensa release firmware builds again
  - [x] **SPI master** (VSPI SCLK18/MOSI23/MISO19/CS5) — compile-verified;
        on-chip transfer verification pending hardware
  - [x] **RMT RX capture / PulseFeedback** — transient 1 MHz RX channel with an
        on-recv-done ISR callback over a FreeRTOS queue; packed RMT symbols
        decoded back to µs pulse widths (mirrors the TX packing); channel + queue
        torn down each call so the shared RMT pool never exhausts. **Verified on
        the ESP32-WROOM-32 (COM4)**: `Signal.Capture` runs alloc→receive→timeout→
        teardown and re-allocates cleanly (idle line → empty), `PulseFeedback`
        trigger+measure times out correctly — no panic/reboot
  - [x] **GPIO edge interrupts** (`on_edge`) — shared per-pin ISR service, ISR
        samples the level and forwards it over a queue to a dispatch thread that
        invokes the boxed callback off ISR context (queue handle crosses the
        thread boundary as a `usize`). Compiles/links/boots clean on-chip; not
        yet reachable from a C# app (Rust-internal HAL method), so functional
        edge-triggering awaits an app-surface binding or external signal
  - [ ] OTA A/B via `esp_ota` partitions — need on-chip iteration
  - [x] **M5Stack Tough board bring-up** — the first RustNet device with a live
        physical panel. AXP192 PMIC over I²C (LDO2/LDO3/DCDC3 rails + GPIO reset/
        backlight-enable), ILI9342C 320×240 over SPI2 (manual CS/DC, FIFO/no-DMA
        so it streams from PSRAM), PSRAM enabled so the 150 KB framebuffer fits.
        New `Board::present_frame` hook flushes the framebuffer on `Present()`.
        Built with `--features board-m5tough`; **verified live on the M5 Tough
        (COM5)** running the `graphics-primitives` demo — backlight + full
        colour showcase confirmed on-screen
- [~] bare-metal firmware executor (no_std service loop) — **done for
      STM32F4 and the K210** (both below, both on hardware); ESP32-C3
      (esp-hal) still pending
- [x] **STM32F4 (Cortex-M4F) runs RustNet on bare metal** — the first target
      with no OS underneath it at all. `runtime/rustnet-hal-stm32` implements
      the HAL straight on the chip's registers with no dependency beyond
      `rustnet-hal`, so it stays inside the host workspace and its test run
      while also building for `thumbv7em-none-eabihf`;
      `runtime/firmware-stm32` is the bare-metal binary. Verified on a
      **Nucleo-F401RE** (over SWD, with the GPIO/USART registers read back to
      confirm the HAL wrote what it claimed) and a **Netduino 3 WiFi**
      (STM32F427VIT6, over DFU):
  - [x] **C# executing on-chip** — `rustnet-core` is already `no_std + alloc`,
        so the interpreter links directly; the whole `RuntimeHost` surface is
        four methods. A language-tour demo passes all nine of its checks on
        silicon: string interpolation, `List<T>`+foreach, `Dictionary`, LINQ
        `Where`/`Select`/`Sum`/`OrderBy`, lambdas, interface dispatch with a
        `ToString` override, user generics, `catch when`
  - [x] **the no_std service loop** — answers the tools, then hands the
        interpreter a slice of fuel. No RTOS, no executor. Receive is
        interrupt-driven of necessity: the F4 USART has no receive FIFO, so a
        polled reader drops most of every frame while a slice runs
  - [x] **`rustnet provision` / `flash` over the wire** — RSA-2048 verified
        **on-chip** (67 ms at 84 MHz, 35 ms at 168 MHz).
        `rustnet-crypto` and `rustnet-secureboot` gained a default `std`
        feature and now build for `thumbv7em-none-eabihf`
  - [x] **persistence in a reserved flash sector** — the provisioned key and
        the uploaded application survive a power cycle, so an uploaded app
        **starts again by itself**. `ExtMemory` already described NOR flash
        exactly, so the HAL needed no new trait; `memory.x` keeps the sector
        out of the FLASH region so the linker can never place code where the
        firmware erases
  - [x] **RNDP over the Netduino's own USB, as CDC** — one cable carries DFU
        for the firmware and RNDP for everything after it, no adapter. One PLL
        gives both the 168 MHz core and USB's 48 MHz from the single 25 MHz
        crystal
  - [~] **microSD block device** (`sdcard.rs`) — SPI is live in the HAL and the
        card identifies completely (CMD8 echo, ACMD41, OCR, CSD), but blocks are
        unreadable at every clock rate with the card reporting no error: a
        healthy controller in front of dead storage. Written but unproven;
        needs a second card
  - [ ] a real filesystem — the reserved sector holds fixed records, not paths,
        so `RustNet.IO.FileSystem` is still unimplemented here. `rustnet-fs`
        already has a `FatVolume` that implements `Vfs`, and `fatfs` has an
        `alloc` feature, so this is mostly the same `no_std` port the crypto
        crates took
  - [ ] **WiFi** — the Netduino's radio is a TI CC3100 on SPI1, which speaks
        SimpleLink's proprietary binary protocol rather than AT commands.
        There is no Rust driver for it, and nanoFramework's own port of this
        board ships without WiFi. An ESP-AT companion on a spare UART is the
        cheaper route to `NetInterface` here
- [~] **Kendryte K210 (RV64GC) — the second bare-metal port, and the first
      64-bit one.** `runtime/rustnet-hal-k210` follows the STM32 pattern
      exactly — register-level, no dependency beyond `rustnet-hal`, so it stays
      inside the host workspace and its test run (38 unit tests) while also
      building for `riscv64gc-unknown-none-elf`. `runtime/firmware-k210` is the
      bare-metal binary, targeting a **Sipeed Maix Go**. **Verified on
      hardware 2026-07-31**: boots at a detected 390 MHz, serves RNDP over
      UARTHS, runs a signed C# app restored from SPI NOR after a power cycle,
      keeps files in ~15 MB of that flash, and animates its 320×240 MaixLCD
      at around 20 fps. Walkthrough: `docs/deploy-maixgo.md`; internals and
      the hard-won facts: `runtime/firmware-k210/README.md`:
  - [x] **the FPU has to be switched on in software.** `mstatus.FS` is `Off`
        at reset on this core, so every floating-point instruction traps as
        illegal until a `csrs mstatus` in `#[pre_init]` clears it.
        `riscv-rt` does not do this; Kendryte's `crt.S` does. Skipping it
        builds fine and boots fine, then kills a C# program the first time it
        multiplies two doubles — with the trap pointing at the arithmetic
        rather than at the cause. The single most important non-obvious fact
        in the port
  - [~] FPIOA pad muxing carrying **Kendryte's own per-function pad table**
        rather than derived enables: a `UARTHS_TX` pad wants its driver on and
        its input off, a GPIOHS pad wants both so the block's `output_en` can
        pick, an I²C pad wants open-drain with a pull-up. Not guessable, so
        transcribed, with a test asserting every entry's low byte is its own
        function number
  - [~] **there is no pinout on this chip** — any of the 48 pads can carry any
        of 256 functions, so `Board::gpio(pin)` takes an FPIOA pad and
        allocates one of GPIOHS's 32 channels on first use, and the UARTs and
        SPI buses carry *no* default pins. On a Maix Go a plausible-looking
        default of IO8 for UART2 would hold the on-board ESP8285 in reset
  - [~] **the clock tree is read, not written.** The ROM has already brought
        PLL0 up before our image runs, and re-programming a PLL that feeds the
        executing core either works or hangs with nothing on the console to say
        which. `Clocks::detect()` recovers what is in force, everything scales
        off it, and the boot banner prints it — so the first hardware run is a
        measurement, including of whether the core is at ~403 MHz or still on
        the 26 MHz crystal
  - [~] RNDP over UARTHS, drained from **both** the PLIC interrupt (source 33)
        and by polling, running the same code either way. The 8-entry FIFO is a
        hard 694 µs deadline at 115200; a polling interval is only a latency
        choice. `info` reports `rx_dropped` and `max_poll_gap_us`, and
        `--no-default-features` drops the interrupt entirely if it misbehaves
        on first bring-up
  - [~] persistence in a 256 KB window at the top of the board's 16 MB SPI NOR.
        No linker-script guard needed, unlike the STM32: the mask ROM copies
        the image into SRAM and runs it there, so this flash holds no executing
        code, an erase is an ordinary SPI transaction that does not stall the
        core, and the protection is arithmetic — the window starts megabytes
        above the image and `SpiFlash` refuses anything outside it
  - [~] **6 MB of SRAM changes the shape of the port.** 4 MB heap,
        half-megabyte `rustnet flash` containers, and none of the F401RE's
        kilobyte negotiation. A 320×240 RGB565 framebuffer — the thing that
        forces PSRAM on an ESP32 — is a rounding error here
  - [ ] the **ST7789V LCD on SPI0**, which would be the first
        `Board::present_frame` on a bare-metal target and needs no PSRAM
        bounce buffer
  - [ ] **WiFi** via the on-board ESP8285 as an ESP-AT companion on UART1 —
        the same shape the Netduino needs, so one `NetInterface` serves both
  - [ ] microSD on SPI1 (`firmware-stm32/src/sdcard.rs` ports directly), the
        camera and microphone array, and eventually the KPU

---

## v0.6 — Cloud connectivity ✅

New `RustNet.Cloud` library over the existing MQTT/HTTP stack; all
providers testable against the virtual device.

- [x] Azure IoT Hub — device client (MQTT + SAS-token auth, D2C telemetry,
      C2D subscribe); SAS signed on-device via the HMAC intrinsic
- [x] AWS IoT Core — device client (MQTT, Device Shadow topics + envelope)
- [x] Google Cloud IoT Core — device client (MQTT + JWT HS256,
      config/state/events); RS256 device-key signing is the TLS integration point
- [x] IFTTT — Webhooks (Maker) trigger client over HTTP
- [x] MQTT username/password (ConnectAuth) + RustNet.Security (HMAC, Url)
      building blocks; JSON payloads via RustNet.Serialization
- [x] `cloud-telemetry` template; all four clients verified running on the
      virtual device (Azure SAS + GCP JWT built on-device via the intrinsic)

## v0.7 — Rich UI, graphics & the UI Designer

TinyCLR-parity managed UI + a desktop designer.
Ref: ghielectronics.com/docs/tinyclr/feature/user-interface

- [x] `RustNet.UI` component expansion: window/stack/panel/border/canvas/
      grid/**scrollviewer** containers + label/button/textbox/checkbox/radio/
      slider/progress/listbox/image controls, two-pass measure/arrange layout,
      hit-testing + Tap input/event model, and ToXml round-trip (designer save
      path); verified on-device (layout + render + input via display capture).
      ScrollViewer scrolls, clamps, clips content and round-trips through XML
- [x] `RustNet.Graphics` bitmap blit — `Display.DrawImage(x,y,w,h,rgb565)`
      intrinsic (one-call framebuffer blit); **linear gradient fill**
      (`FillGradient`), **alpha blend** (`BlendImage`, global alpha) and
      **clip rectangle** (`SetClip`/`ClearClip`) — all verified on-device
- [x] `RustNet.Drawing` + image decoders — `System.Drawing`-shaped `Bitmap`
      with **BMP (24/32-bit)** and **GIF (LZW, interlaced)** managed decoders
      plus **PNG** and **JPEG** via the runtime (`Native::DecodeRgb565`, Rust
      `image` crate); all four verified pixel-exact on host and on-device
- [x] Display support: managed panel configuration —
      `Display.Configure(driver, width, height, rotation)` selects the panel
      driver (SSD1306/ST7735/ILI9341/Generic) and applies 0/90/180/270°
      rotation; `Display.Width()`/`Height()` report rotation-aware logical
      size. Verified on-device (rotated draw + clip land at computed pixels)
- [x] **UI Designer** (`RustNet.Designer`, WPF desktop tool): VS-WPF-style
      editor — toolbox (incl. scrollviewer), WYSIWYG canvas (renders exactly
      as the device paints), element tree, live property grid,
      **drag-to-move** for canvas children; opens/edits/saves the
      `RustNet.UI` XML round-trip. Verified: headless `--selftest` (render,
      round-trip and drag-to-move), and a designer-saved layout renders on
      the virtual device
- [x] `RustNet.Resources` — embedded assets bundled into the RNX (v4
      resources section) and read at runtime by name; verified on-device
      (embed GIF → in RNX → GetBytes → decode → draw). `image-viewer` template

## v0.8 — Media & USB (chip-gated)

Features available only where the silicon supports them; the HAL trait
returns `NotSupported` elsewhere.

- [x] Camera capture — `RustNet.Media.Camera` (`Configure`/`Capture`/
      `Width`/`Height`); `rustnet-hal` `Camera` trait + `SimCamera` colour-bar
      simulator; RGB565/grayscale frames blit straight to the display.
      Verified on-device (8 colour bars captured pixel-exact) (ref: feature/camera)
- [x] MJPEG video record/playback — `RustNet.Media.Video.EncodeJpeg` (runtime
      JPEG encoder) + `MjpegWriter`/`MjpegReader` (length-prefixed JPEG-frame
      container). Verified on-device (camera frame → JPEG → 3-frame clip →
      read back → decode → draw, colour bars intact) (ref: feature/mjpeg-video)
- [x] Audio playback — `RustNet.Media.Audio` (`Configure`/`Play`/
      `SamplesPlayed`) over the I2S HAL; LE 16-bit PCM. Verified on-device
      (cumulative sample count tracks two Play calls) (ref: audio)
- [x] VNC server — `RustNet.Media.Vnc` (`Start`/`Stop`/`IsRunning`); RFB 3.8
      server (security None, 32bpp true-colour, Raw full-frame updates)
      streaming the framebuffer over TCP. Verified on-device with a real RFB
      client (handshake → ServerInit → framebuffer pixels match) (ref: feature/vnc)
- [x] USB Host — `RustNet.Usb.UsbHost` (`Enumerate`/`BulkOut`/`BulkIn`);
      enumerates an attached device via CDC/HID/MSC class drivers and moves
      bulk data (ref: feature/usb-host)
- [x] USB Client — `RustNet.Usb.UsbClient` (`BeginCdc`/`Read`/`Write`); the
      board presents itself as a USB peripheral (ref: feature/usb-client)
- [x] USB PC Communication — CDC-ACM virtual serial over USB (the `BeginCdc`
      channel). Verified on-device: one app presents a CDC device, the host
      enumerates it and bulk-transfers PING/PONG both ways through the USB
      simulator (ref: feature/usb-pc-comm)

## v0.9 — .NET completeness

- [x] Reflection (ref: feature/reflection):
  - [x] `object.GetType()` → `System.Type`; `Type.Name`/`FullName`/
        `Namespace`/`BaseType` (walks the RNX type hierarchy), `ToString`;
        works for user types and BCL values (string/int/…). Verified in the
        LangApp E2E (`Circle`.BaseType = `Shape`)
  - [x] method enumeration: `Type.GetMethods()` → `MethodInfo[]`,
        `Type.GetMethod(name)`, `MethodInfo.Name`/`ToString`; `==`/`!=` on
        reflection handles (`op_Equality`/`op_Inequality`). Verified in the
        LangApp E2E (`GetMethod("Name")`, `GetMethod("Area")`)
  - [x] `typeof(T)` via `ldtoken` + `Type.GetTypeFromHandle` — same
        `System.Type` identity as `GetType()` (`typeof(T) == obj.GetType()`);
        `ldtoken` of a type resolves to an RNX type index (user) or interned
        full name (BCL). Verified in LangApp E2E (`typeof(Circle)` Name/Namespace
        + identity, `typeof(int).Name`)
  - [x] `MethodInfo.Invoke` — reflective call with a boxed `object[]` (args
        unboxed, value-type return flows back as `object`, void → null). Void
        arity solved with a new `MFLAG_HASRET` MethodDef bit (set from the
        signature's return type); reuses `dispatch_delegate`. Verified in the
        LangApp E2E (`Area` non-void + `Scale` void/boxed-arg → `area=12 scaled=48`)
  - [x] `GetFields` — `Type.GetFields()`/`GetField(name)` → `FieldInfo[]`
        (public, own + inherited), `FieldInfo.Name`, `GetValue`/`SetValue`
        (instance + static). Added RNX **v5** per-type field descriptors
        (name/flags/slot; `MetadataProcessor` emits them from the field layout).
        Verified in LangApp E2E (`Radius` get=4 → set 5, `GetFields().Length`=1)
  - [x] `GetProperties` — `Type.GetProperties()`/`GetProperty(name)` →
        `PropertyInfo[]`, `PropertyInfo.Name`, `GetValue`/`SetValue` (paired
        from `get_`/`set_` accessors, dispatched through them; no RNX change).
        Verified in LangApp E2E (`Color` set "red" → get, count 1)
  - [x] custom attributes (type-level) — `Type.GetCustomAttributes(bool)` /
        `(Type, bool)` instantiate user attributes (ctor positional + named
        field/property args). RNX **v6** stores per-type attribute records
        (ctor idx + tagged const args); the runtime builds instances via
        `invoke_managed`. Base `System.Attribute::.ctor()` is a no-op. Verified
        in LangApp E2E (`[Tag("shape", Rank = 7)]` → label/rank read back)
- [x] inline-array/span params lowering — `initobj <>y__InlineArrayN<T>`
      allocates a heap array (size N marked into the operand); the
      `<PrivateImplementationDetails>.InlineArray*` helpers are interpreter
      intrinsics (element byref / span = the array); `string.Concat(span)`
      concatenates the array. `string.Concat` with 5+ parts now works.
      Verified in LangApp E2E (`concat5=circle|square|end`)
- [x] debugger: breakpoints/stepping surfaced in VSCode DAP (sequence
      points) — MetadataProcessor emits the RNX debug section from PDB
      sequence points; RNDP gains continue/step/clear/state/locals commands;
      `RnxDebugInfo` maps source lines ↔ (method, IL); the `rustnet-debugger`
      DAP adapter bridges VSCode to the device over RNDP; the VSCode extension
      contributes the `rustnet` debug type. Verified: on-device breakpoint
      cycle E2E (`DebuggerBreakpointCycle`) + DAP framing/mapping unit tests

## v1.0 — Production hardening

- [~] GC / interpreter perf:
  - [x] adaptive GC threshold — the collection trigger grows with the live set
        after each collection (`live * 2`, floored at 1024) instead of a fixed
        1024, so retain-heavy heaps aren't fully scanned every 1024 allocations;
        GC frequency tracks garbage, not live-set size. Verified: heap unit tests
  - [ ] generational/incremental GC (write barriers), or interpreter
        superinstructions / baseline JIT where RAM is writable+executable
- [x] database:
  - [x] secondary indexes — `CREATE/DROP INDEX`; equality `WHERE` (incl. `?`
        params and one `AND` conjunct) served from the index then re-checked;
        maps rebuilt after mutations; index defs persist (snapshot format v2).
        Verified: rustnet-db unit tests + SysApp E2E (`db indexed room=attic`)
  - [x] WAL-style incremental persistence — optional `Storage` WAL methods;
        each mutating statement (SQL + params) is appended to the log and
        replayed on open, folded into a snapshot at a checkpoint (every 64
        writes / on open). Device VFS bridge uses a sibling `<db>.wal`.
        Verified: rustnet-db WAL unit test + SysApp E2E (`db reopened count=3`)
- [~] RNDP over BLE; fleet OTA campaigns; package registry for `rustnet pkg`:
  - [x] package registry dependency resolution — `rustnet pkg install` resolves
        the transitive closure (semver-ordered, minimum-version selection,
        diamond dedup, deps-first install, exact `--version` pin). Verified:
        `PackageResolver` unit tests
  - [x] fleet OTA campaigns — `rustnet ota campaign <fw> --fleet devices.txt`:
        canary-first staged rollout, per-device push+confirm, abort-after-N
        failures (remainder skipped), per-device reporting. `OtaCampaign`
        orchestrator (injected pusher). Verified: unit tests + a 2-virtual-device
        E2E (`FleetOtaCampaign`)
  - [~] RNDP over BLE — `BleTransport` fragments the RNDP byte stream into
        ATT-MTU-sized GATT packets and reassembles them, so RNDP framing works
        over BLE unchanged; `ble:<address>` spec + pluggable
        `TransportFactory.BleLinkProvider`. Verified: fragmentation/reassembly +
        factory unit tests (loopback link). Remaining: per-chip BLE radio HAL

---

# Post-1.0 roadmap

Everything below builds on the shipped runtime/contracts; new managed
libraries follow the existing `[InternalCall]` + `apphost.rs` pattern (or are
pure managed code where they need no host bridge). Community peripheral
drivers ship through the package registry, not the core repo.

## v1.1 — On-device ML & GNSS

Machine learning and navigation for capable MCUs. TFLite is gated to boards
with enough compute/RAM; the lightweight ML and NMEA paths run anywhere.

- [x] **Persistent app autostart** — a flashed app is **automatically executed
      on power-up / reboot**, no host reconnect. Starting an app (`flash --start`
      / `apps start`) marks it as the autostart app; the firmware relaunches it
      on boot. `rustnet apps autostart <name>|off` toggles it explicitly; a
      boot-loop guard skips autostart after 3 consecutive unattended boots (any
      flash/start clears the counter) so a crashing app cannot brick the device.
      **Verified live on the M5 Tough**: the demo auto-resumes after an
      `espflash reset` with no host `--start`. Two persistence fixes were
      needed: `DirFs::write` now `fsync`s (FAT metadata was lingering in RAM),
      and the ESP32 must be built + flashed with the custom partition table
      (`partitions.csv` — a FAT "storage" partition; the default table has none,
      so the device was silently falling back to in-RAM MemFs). Flash with
      `espflash flash <elf> --partition-table runtime/firmware-esp32/partitions.csv`.
      TODO: make the build's partition-table path portable (esp-idf-sys needs an
      absolute `CONFIG_PARTITION_TABLE_CUSTOM_FILENAME`); teach `rustnet firmware
      flash` to pass `--partition-table` automatically.
- [ ] **TensorFlow Lite for Microcontrollers** — `RustNet.AI.TensorFlow`:
      load a `.tflite` model (embed via `RustNet.Resources`), run inference
      on int8/float tensors; backed by a Rust host bridge to TFLite-Micro on
      capable silicon, `NotSupported` on tiny targets
      (ref: developer.wildernesslabs.co → TensorFlowLite)
- [ ] **Lightweight ML** — `RustNet.ML`: pure-managed, allocation-frugal
      estimators trainable/inferable on-device —
      classification (binary + multiclass), regression, clustering (k-means),
      recommendation, and a small feed-forward neural network. No external
      runtime; models serialize through `RustNet.Serialization`
- [ ] **GPS / GNSS NMEA parser** — `RustNet.Sensors.Gnss`: streaming NMEA-0183
      decoder (GGA/RMC/GSV/GSA/VTG…) over UART, surfacing fix/position/
      velocity/satellites-in-view events; driver-agnostic (MT3339, NEO-M8,
      BG95 GNSS) (ref: developer.wildernesslabs.co → Gps_Gnss_Nmea_Processor)

## v1.2 — Alternative UI frontends

Desktop/host UI toolkits as **alternatives to `RustNet.UI`** for tools,
simulators and gateway apps that run on a full OS (not the constrained
device surface). Each is an optional package.

- [ ] **WinForms** frontend (Windows tooling)
- [ ] **GTK** frontend (Linux/cross-platform)
- [ ] **Silk.NET + SkiaSharp** frontend (GPU-accelerated, cross-platform)
- [ ] **AsciiConsole** frontend (headless/terminal — renders the UI tree as
      text, useful for CI and SSH sessions)

## v1.3+ — Community peripheral library (packages)

A driver ecosystem shipped through `rustnet pkg` — each driver is a managed
`RustNet.*` package over the existing HAL buses (I2C/SPI/UART/GPIO/PWM/ADC),
contributed and versioned independently of the core. Grouped by category:

### Sensors
- [ ] **Atmospheric** (temp/humidity/pressure): BME280, BME680/688,
      BMP180/280, SHT31D, SHT4x, HTU21D/31D, AHT10/20, DHT10/12, Si70xx,
      MS5611, MPL3115A2, CCS811, SGP40
- [ ] **Motion** (accel/gyro/mag): ADXL335–377, MPU6050, LSM6DSOX,
      LSM303AGR, BNO055, BMI270, HMC5883, LIS3MDL, MAG3110
- [ ] **Light**: BH1750, BH1745, TSL2591, VEML7700, MAX44009, SI1145, TEMT6000
- [ ] **Distance**: VL53L0X (ToF), HC-SR04, MaxBotix, A02YYUW, ME007YS
- [ ] **Environmental** (gas/VOC/CO₂/AQI): SCD30, SCD40/41, ENS160, PMSA003I,
      CCS811, AGS01DB
- [ ] **Color**: TCS3472x
- [ ] **Camera / thermal imaging**: Arducam, VC0706, AMG8833, MLX90640
- [ ] **Temperature**: LM75, MCP9808, TMP102, MAX6675 (thermocouple),
      MCP960x, thermistor
- [ ] **HID / touch**: XPT2046 (resistive touch), TSC2004, MPR121 (capacitive
      keypad), keyboard, joystick
- [ ] **GNSS/GPS modules**: MT3339, NEO-M8, BG95 (GNSS+cellular) — driver
      layer over the v1.1 NMEA parser
- [ ] **Load cell / weight**: HX711, NAU7802
- [ ] **Soil moisture**: capacitive soil sensor, FC-28
- [ ] **Power / current**: INA219/228/260, current transducer
- [ ] **Battery fuel gauge**: MAX17043/17044
- [ ] **Flow** (liquid): Hall-effect Gr105/201/216, YF-B series
- [ ] **Sound**: KY-038

### Displays
- [ ] **Character/segment**: HD44780 (I2C/digital LCD), TM1637,
      seven/fourteen/sixteen-segment LED
- [ ] **Monochrome OLED**: SSD1306, SSD1309, SH1106, SH1107, CH1115, UC1609C,
      ST7565
- [ ] **Color TFT (SPI)**: ILI9341, ILI9163/9225/9481/9486/9488, ST7735,
      ST7789, ST7796S, SSD1331, SSD1351, GC9A01, HX8357B/D
- [ ] **E-paper**: IL0373/0376F/0398/3897/91874, SSD1608/1680/1681, UC8151C,
      Waveshare EPD (1.54"/2.13"/2.7"/2.9"/4.2"/5.65"/7.5")

### Actuators / motors
- [ ] **DC motor**: `HBridgeMotor`, `BidirectionalDcMotor`
- [ ] **Stepper**: A4988, ULN2003, `GpioStepper`
- [ ] **ESC** for brushless/drone motors
- [ ] **Industrial**: CerusXDrive (Franklin Electric VFD), Tb67h420ftg,
      StepperOnline series (BLD510B, …)
- [ ] **LED actuators**: `Led`, `PwmLed`, `RgbLed`, `RgbPwmLed`,
      `LedBarGraph`, WS2812, APA102, PCA9633
- [ ] **Relay**: `Relay`
- [ ] **Buzzer**: `PiezoSpeaker`

### Support ICs (not sensors, but commonly paired)
- [ ] **ADC/DAC**: ADS1015/1115, ADS7128, MCP3xxx, MCP492x
- [ ] **IO expanders**: MCP23xxx, PCF/PCA857x, TCA9535/9555, HT16K33
- [ ] **RTC**: DS1307, DS3231/3232, PCF8523
- [ ] **Digital potentiometer**: MCP4xxx

## v1.4 — NPC: Native Procedure Caller (RLP-style)

Call **native Rust code from a C# app**, loaded onto the device at runtime
with **no firmware rebuild** — the RustNet analogue of TinyCLR's RLP (Runtime
Loadable Procedures). Ref: ghielectronics.com/docs/tinyclr/feature/rlp.

**Feasibility: yes, in stages** — the runtime already has the three pieces
that make this tractable, so the work is a loader + a stable ABI, not new
infrastructure:
- **Marshalling** already exists — the host boundary marshals **byte arrays**
  (u16/u32 packed LE); NPC reuses that exact shape, so no new value protocol.
- **Signed delivery** already exists — RNSB signs+verifies containers and RNDP
  flashes them; a native module ships as a **signed RNSB payload** (loading
  unsigned native code would be a security hole, so signing is mandatory).
- **Dispatch seam** already exists — one intrinsic
  (`RustNet.Native.Npc::Invoke(handle, u1[]) -> u1[]`) routes into a loaded
  function, mirroring the existing `[InternalCall]` + `apphost.rs` pattern.

**ABI** (stable, C-callable): `extern "C" fn(args: *const u8, args_len: u32,
out: *mut u8, out_cap: u32) -> i32` returning the bytes written (or a negative
error). Simple, bounds-checked, matches the byte-array channel.

**The hard part is executing loaded code, so it is chip-gated** (like RLP):
- [ ] **Host / virtual device (std)** — load a native module as a dynamic
      library (`libloading`) and call it. Fully feasible now; the reference
      implementation + the E2E test path (build a Rust `cdylib`, sign it,
      "flash" it, invoke from C#, check the result).
- [ ] **RAM-executable silicon** (ESP32 IRAM, Cortex-M SRAM) — a
      position-independent blob + a small relocating loader into executable
      RAM (MPU/`W^X` aware). Feasible; per-chip bring-up.
- [ ] **XIP/flash-only or pure-Harvard chips** — return `NotSupported` (can't
      write+execute at runtime), exactly as RLP is unavailable there.
- [ ] **Toolchain** — `rustnet native build` compiles a Rust crate to the
      target's loadable form, `rustnet native push` signs (RNSB) + flashes it
      and returns a handle; `Npc.Load(name)` resolves the handle, `Npc.Invoke`
      calls it. Safety caveat documented: native code runs unsandboxed and can
      fault the device — signing gates *who* can load, not *what* it does.

Contract-stability rule: RNX/RNDP/RNSB changes require version bumps and
dual-side (Rust + C#) updates in the same commit — see CLAUDE.md.

## v1.5 — RP2040 silicon (Raspberry Pi Pico / Pico W)

Bring RustNet to the **RP2040** (dual Cortex-M0+ @ 133 MHz, 264 KB SRAM,
external QSPI XIP flash, native USB 1.1, PIO). Same staged path the ESP32
bring-up (v0.5) followed — the `no_std + alloc` interpreter core and the HAL
traits already exist, so this is a board crate + a firmware target, not new
infrastructure.

- [ ] **Toolchain / core profile** — build `rustnet-core` + `rustnet-hal`
      for `thumbv6m-none-eabi`. Cortex-M0+ has no native atomic CAS and no
      hardware divide: add `portable-atomic` (critical-section impl) and lean
      on libm/soft-division, mirroring the existing RISC-V `no_std` profile
- [ ] **Board crate** (`rustnet-hal-rp2040`) over `rp-hal`/Embassy — GPIO,
      PWM, ADC, I2C, SPI, UART behind the existing HAL traits, so every
      `RustNet.*` API lights up unchanged
- [ ] **PIO** — map the RP2040's programmable-IO state machines onto the
      `Signal`/RMT-style surface (precise pulse generate/capture) and to
      WS2812/APA102 LED drivers; a differentiator no current target has
- [ ] **Deploy over native USB-CDC** — the RP2040's built-in USB device
      presents CDC-ACM; RNDP rides the existing byte-pipe transport
      (`serve_pipe` / `--stdio`), so `rustnet flash/logs/…` work over the USB
      cable with no extra hardware. Reuses the v0.8 `RustNet.Usb` device stack
- [ ] **Persistent flash** — app + provisioning storage in an XIP QSPI flash
      region (survives reboot/reflash), then **OTA A/B** across two app slots
- [ ] **Pico W WiFi** (CYW43) — optional: WiFi STA + RNDP TCP :7878 for fully
      wireless deploy, exactly like the ESP32 phase-2 path; plain Pico stays
      USB-only
- [ ] **NPC on RP2040** — SRAM is writable+executable on M0+, so the v1.4
      "RAM-executable silicon" path applies: relocate a PIC blob into SRAM and
      call it (per-chip bring-up)
- [ ] **HIL verification** — extend `tools/hil-smoke.sh` with a Pico target
      (BOOTSEL/UF2 via `elf2uf2`/`picotool`), then the on-chip E2E: provision
      → RSA-verified flash → C# app (incl. async/await) with live GPIO/PWM/ADC
      from managed code

Note: the Linux-class **Raspberry Pi Zero** (Broadcom SoC) is a different
device — it runs the standard `std` host firmware build directly, no silicon
bring-up needed. This section targets the RP2040 microcontroller (Pico family).
