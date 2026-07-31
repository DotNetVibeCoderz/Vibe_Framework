# Development progress

Tracking checklist for the feature roadmap in [PLAN.md](PLAN.md).
✅ = implemented + verified by tests on the virtual device.
🟡 = structure in place, real-silicon/vendor work pending.

## Bare metal (STM32F4)
- [x] **C# running on Cortex-M4F with no OS** — `rustnet-hal-stm32` (register-level GPIO/USART/SPI/DWT delay, no deps beyond `rustnet-hal`) + `runtime/firmware-stm32`; verified on a Nucleo-F401RE over SWD and a Netduino 3 WiFi over DFU ✅
- [x] **no_std service loop** — answers RNDP, then gives the interpreter a fuel slice; interrupt-driven receive because the F4 USART has no FIFO ✅
- [x] **`provision` + `flash` over the wire**, RSA-2048 verified on-chip; `rustnet-crypto`/`rustnet-secureboot` now build for `thumbv7em-none-eabihf` ✅
- [x] **Persistence in a reserved flash sector** — key and app survive a power cycle, so an uploaded app restarts by itself ✅
- [x] **RNDP over the Netduino's native USB (CDC)** — no serial adapter; one PLL feeds both the 168 MHz core and USB's 48 MHz ✅
- [~] microSD block device — card identifies but its storage is unreadable; driver written, unproven, needs a second card 🟡
- [ ] Real filesystem on this target (`RustNet.IO.FileSystem`) — `rustnet-fs`'s `FatVolume` plus a `no_std` port of `fatfs`
- [ ] WiFi (CC3100) — see Networking

## Bare metal (Kendryte K210, RV64GC)
Second bare-metal port, first 64-bit one. **Runs C# on hardware as of
2026-07-31** — a Sipeed Maix Go on COM10, booting at a detected 390 MHz.
`runtime/firmware-k210/README.md` records what the first run settled, and the
one real bug it found.
- [x] `rustnet-hal-k210` — FPIOA pad muxing (Kendryte's own per-function pad table), GPIOHS 32 channels with software open-drain, UARTHS + UART1..3 (fractional divisor), `mcycle` delay/clock, DesignWare SSI SPI0/1/3, SYSCTL clock readback; no deps beyond `rustnet-hal`, so it stays in the host workspace — **38 unit tests** ✅
- [x] `runtime/firmware-k210` — `riscv64gc-unknown-none-elf` + `riscv-rt`, RAM-only link over the 6 MB SRAM, 4 MB heap, interpreter linked, RGB-LED boot diagnostics (green progress / red failure) ✅
- [x] RNDP over UARTHS — drained from both the PLIC interrupt (source 33) and by polling, because the 8-entry FIFO is a hard 694 µs deadline at 115200 while a polling interval is only a latency choice; measured `rx_dropped: 0` and a steady-state `max_poll_gap_us` of ≈27 ms across repeated multi-kilobyte uploads ✅
- [x] `provision` + `flash` over the wire, RSA-2048 verified on-chip, `ChipFamily::K210` already on both sides of the contract ✅
- [x] Persistence in a 256 KB window at the top of the board's 16 MB SPI NOR — no linker guard needed, unlike the STM32: the image runs from SRAM, so this flash holds no executing code and an erase does not stall the core ✅
- [x] **A blank check has to cover the whole record** — a stock Maix Go's flash window is erased at the front and written further in, so a four-byte probe said "blank" and a 12 KB app was programmed over live data. NOR only clears bits, so the two writes ANDed: four corrupted bytes three pages deep, in an app that ran perfectly until the next power cycle. `tail_is_blank` now checks the whole span and `write_record` reads back and compares — both fixed in the STM32 port too, which carries the same log format ✅
- [x] **ST7789V 320×240 panel on SPI0** — octal frame format with the data lines switched onto the DVP pins; `Display.Present()` reaches glass and the demo animates at ~20 fps. The thing that took the longest: **`spi_ctrlr0` is not one value** — MaixPy reconfigures it and the frame width before *every* transfer (8-bit instruction for a command, 8-bit units for its parameters, 32-bit units for pixels), and one value for all three gives a panel that wakes, lights its backlight and then shows a flat colour forever ✅
- [x] **`ctrlr1` must be cleared per transfer** — a receive leaves its frame count behind and the next transmit inherits it: one four-byte panel read at boot doubled every subsequent frame's cost, 64 ms to 126. Latent on SPI3 too, where reads and writes alternate constantly ✅
- [x] **LED colours are the reverse of Sipeed's own `config_maix_go.py`** — IO12 is blue and IO13 green, confirmed by eye and then by the Maix Go datasheet's pin table. A vendor config file is not automatically authoritative ✅
- [x] **`rustnet-gfx` builds `no_std + alloc`** — same framebuffer and primitives the virtual device draws into, so a demo that looks right in `display capture` looks right on the panel ✅
- [x] **`rustnet-flashfs`** — named blobs in a log over raw NOR, backing the whole `RustNet.IO.FileSystem` surface on ~15 MB of the board's flash. A workspace member depending only on `rustnet-hal`, so its scan/compact logic is tested against a fake NOR that models programming as bit-clearing — the failure mode that actually bites ✅
- [x] **`demo/Showcase`** — starfield, rotating wireframe solids over a perspective floor, and a title that catches fire; measured on the board at 15/17/6 fps and reporting its own frame times into `rustnet logs` ✅
- [x] **The UARTHS interrupt fires** — `info` reports `rx_irqs`, which climbs into the tens of thousands during an upload. The port's biggest open question, and unanswerable before: the polled drain covers for a dead interrupt until an application is busy enough to widen the polling gap, and then uploads fail for no visible reason ✅
- [x] **Compaction has to erase what is used, not the whole region** — a sector erase costs tens to hundreds of milliseconds, so erasing a multi-megabyte window stalled the device past the tools' timeout. Fixed in both ports ✅
- [x] **Opening the serial port reboots the board, and DTR picks the boot mode** — reset is asserted whenever DTR and RTS differ, so the first request after connecting is lost; and pulsing RTS enters the ROM loader rather than the application. `RndpClient` now pings until answered before letting a real command through, and only resets after an unanswered probe ✅
- [x] **The FPU has to be switched on in software** — `mstatus.FS` is `Off` at reset, so a `csrs mstatus` in `#[pre_init]`; without it a C# program dies the first time it multiplies two doubles, and the trap points at the arithmetic rather than the cause. `riscv-rt` does not do this; Kendryte's `crt.S` does ✅
- [ ] WiFi via the on-board ESP8285 as an ESP-AT companion on UART1 — same shape the STM32F4 port needs, so one `NetInterface` would serve both
- [ ] microSD on SPI1 (`firmware-stm32/src/sdcard.rs` is directly reusable), camera, microphone array, KPU

## Communication protocols (add)
- [~] **RNDP over BLE** (v1.0) — `BleTransport` fragments the RNDP byte stream
  into ATT-MTU GATT packets + reassembles (framing unchanged); `ble:<address>`
  spec + pluggable `TransportFactory.BleLinkProvider`. Fragmentation/factory
  unit tests (loopback). Remaining: per-chip BLE radio HAL.

## Communication protocols
- [x] CAN bus — HAL trait, loopback+filter simulator, packed-frame managed API, E2E ✅
- [x] Modbus — RTU (CRC-16) + TCP/MBAP framing in Rust, fc 1–6/15/16, on-device sim slave, master API, E2E ✅
- [x] 1-Wire — bus trait + Dallas CRC8, DS18B20 simulator + C# driver, E2E ✅

## Networking
- [x] Ethernet interface (DHCP/static, status) ✅
- [x] PPP interface (serial modem parameters) ✅
- [x] Cellular interface (APN, operator, RSSI) ✅
- [x] WiFi (pre-existing) + HTTP/MQTT/web server ✅
- [ ] Real-silicon network stacks (lwIP/AT-modem) 🟡
- [ ] STM32F4 `NetInterface` — the Netduino's CC3100 needs TI's SimpleLink
      protocol ported (no Rust driver exists; nanoFramework's own port of the
      board ships without WiFi). An ESP-AT companion on a spare UART is the
      cheaper path 🟡

## Database
- [x] Embedded SQL engine (`rustnet-db`): CREATE/INSERT/SELECT/UPDATE/DELETE, WHERE/ORDER BY/LIMIT, aggregates, LIKE, `?` params ✅
- [x] In-memory databases ✅
- [x] Flash / SD card / USB drive persistence via VFS-path snapshots ✅
- [x] Managed API with JSON row results (`RustNet.Data.Database`) ✅
- [x] **Secondary indexes** ✅ (v1.0) — `CREATE/DROP INDEX`; equality `WHERE`
  (incl. `?` params, one `AND` conjunct) served from the index then re-checked;
  maps rebuilt after mutations; defs persist (snapshot format v2). rustnet-db
  unit tests + SysApp E2E (`db indexed room=attic`)
- [x] **WAL incremental persistence** ✅ (v1.0) — optional `Storage` WAL
  methods; mutating statements (SQL+params) appended + replayed on open, folded
  into a snapshot at a checkpoint (every 64 writes / on open); device VFS bridge
  uses a sibling `<db>.wal`. rustnet-db WAL unit test + SysApp E2E (`db reopened
  count=3`). **DB v1.0 hardening COMPLETE.**

## Power management
- [x] Sleep modes (light/deep/hibernate) ✅
- [x] Wake by GPIO edge ✅ / wake by RTC alarm ✅ / wake reason ✅
- [x] Shutdown ✅ / Reset ✅ (virtual device: halts app + logs; real chips reboot)
- [x] Battery status ✅

## System
- [x] **Adaptive GC threshold** ✅ (v1.0) — the mark-sweep collector's trigger
  grows with the live set (`live * 2`, floor 1024) instead of a fixed 1024, so
  retain-heavy heaps aren't fully scanned every 1024 allocations; GC frequency
  tracks garbage, not live-set size. heap unit tests
- [x] RTC (epoch + calendar, alarm) ✅
- [x] Watchdog (start/feed/stop) ✅
- [x] External memory: QSPI flash (NOR semantics) + SDRAM ✅
- [x] Device info query (chip/board/version/uptime/JSON) ✅

## Serializers
- [x] JSON (DOM + parser + writer) ✅
- [x] XML (element tree + parser + writer) ✅
- [x] Binary (tagged encoding of the JSON DOM) ✅

## UI library
- [x] WPF/Glide-style element tree (window/stack/label/button/progress/rect) ✅
- [x] XML layout loading from device filesystem ✅
- [x] Rendering to device display; visible in capture/simulator/Workbench ✅
- [x] Touch/button input routing (Tap/hit-test), controls incl. image/listbox,
  and **ScrollViewer** (scroll/clamp/clip + XML round-trip) ✅ on-device

## .NET support
- [x] LINQ ✅  Regex ✅  Timers ✅  UTF8 ✅  Base64 ✅  BitConverter ✅
- [x] ToString (incl. bool True/False) ✅  StringBuilder ✅  BCL generics ✅
- [x] Tuple ✅  Collections ✅  Multithreading (green threads) ✅
- [x] Task subset ✅  Streams (Memory/File/BinaryPacker) ✅
- [x] try/catch/finally ✅  delegates/lambdas ✅  string interpolation ✅
- [x] async/await ✅ (task continuations on green threads; Debug-config state machines)
- [x] interfaces + virtual dispatch + inheritance ✅ (RNX v3 override tables)
- [x] user-defined generics (erased) ✅  exception filters (`catch when`) ✅

## I/O
- [x] Signal control TinyCLR-style: SignalGenerator / SignalCapture / PulseFeedback ✅ (HC-SR04 driver)

## RISC-V & chips
- [x] ESP32-C3 + K210 chip families in secure boot, firmware features, CLI/Workbench/VSCode ✅
- [x] Chip-variant firmware builds compile with full service stack ✅
- [x] Serial (USB-CDC/UART) RNDP transport in tools (`serial:COMx[:baud]`) ✅
- [x] Sample/templates buildable for any chip target ✅
- [x] `no_std + alloc` profile: rustnet-core + rustnet-hal build for `riscv32imc-unknown-none-elf` ✅
- [x] Device-side byte-pipe transport (`--stdio`, `serve_pipe` over any Read+Write) with integration test ✅
- [x] ESP32-C3 board crate: register-level GPIO + cycle delay compile for bare-metal RISC-V ✅
- [x] HIL smoke script (`tools/hil-smoke.sh`, skips without hardware) ✅
- [x] **HIL stage 0 verified on real silicon**: `rustnet probe` identified an
  ESP32-WROOM-32 on COM4 via its ROM bootloader (esptool protocol —
  DTR/RTS download mode, SLIP SYNC, chip magic + eFuse MAC) and captured
  its boot log ✅
- [x] **ESP32 (Xtensa) RUNS RUSTNET on real silicon** ✅ — `runtime/firmware-esp32`
  (std on ESP-IDF): RNDP over UART0, on-chip RSA verification, C# apps
  incl. the full async/await matrix executed on an ESP32-WROOM-32; LED
  blink from managed GPIO
- [x] ESP32 phase 2 on real silicon ✅: FAT persistence across reboots, WiFi
  STA path + TCP RNDP listener, ADC/PWM(LEDC)/I2C from C#, real reboot
- [x] ESP32 phase 3 on real silicon ✅: UART1/2, TWAI→CAN (on-chip self-test
  round trip), RMT→Signal generate, SNTP-backed RTC (real wall-clock time)
- [~] ESP32 phase 4: **build restored** (image codecs made optional — the
  `image` crate had silently broken the Xtensa build since v0.7) + **SPI master**
  (VSPI, compile-verified) ✅; **RMT RX capture / PulseFeedback** (RX channel +
  on-recv-done ISR → queue, symbols decoded to µs) — verified on the
  ESP32-WROOM-32: `Signal.Capture` alloc→receive→timeout→teardown + re-alloc
  clean, `PulseFeedback` times out without panic ✅; **GPIO edge interrupts**
  (`on_edge`: ISR → queue → dispatch thread) compile/link/boot-clean on-chip
  (functional trigger awaits an app-surface binding) ✅; OTA A/B still open
  (partition code — needs on-chip iteration) 🟡
- [x] **M5Stack Tough — first live physical display** ✅: AXP192 PMIC power
  (LDO2/LDO3/DCDC3 + GPIO reset/backlight), ILI9342C 320×240 over SPI2 (manual
  CS/DC, FIFO streaming from PSRAM), PSRAM enabled for the 150 KB framebuffer,
  `Board::present_frame` flush hook on `Present()`. New `graphics-primitives`
  demo (lines/filled shapes/gradients/clipping/matrix-rain) runs **live on the
  M5 Tough** — backlight + full-colour showcase confirmed on-screen 🎉
- [ ] Bare-metal firmware executor (no_std service loop) 🟡
- [ ] ESP32-C3 esp-hal fill-in, Cortex-M boards 🟡

## Tooling
- [x] VSCode simulator panel: display framebuffer, GPIO grid, netif/bus table, log tail ✅
- [x] `rustnet io` + RNDP IoState (0x53) command ✅
- [x] Templates: datalogger-db, can-gateway, ui-dashboard ✅ (run verified on virtual device)
- [x] Package manager `rustnet pkg` (init/pack/publish/list/search/install) ✅;
  **dependency resolution** ✅ (v1.0) — semver-ordered transitive closure,
  minimum-version selection, diamond dedup, deps-first install, `--version` pin
  (`PackageResolver` unit tests)
- [x] **Fleet OTA campaigns** ✅ (v1.0) — `rustnet ota campaign <fw> --fleet
  devices.txt`: canary-first staged rollout, push+confirm per device,
  abort-after-N-failures (remainder skipped), per-device reporting.
  `OtaCampaign` orchestrator + unit tests + 2-virtual-device E2E
- [x] **UI Designer: "Jack The Code Bender" assistant** ✅ — Semantic Kernel
  chat panel in `RustNet.Designer` (OpenAI / Anthropic / Gemini / Ollama,
  picked at runtime, all settings in `App.config`). Kernel functions cover the
  RustNet contracts (UI/graphics/language reference), the canvas
  (validate/apply layout XML), the code pane, the docs and templates,
  `find_managed_api`, Tavily search, page/file fetch, date-time and an
  arithmetic evaluator. Multi-session transcript (create/reset/delete) with
  image + document uploads, markdown→HTML rendering in WebView2 with
  apply-to-canvas / send-to-code actions, and a 41-prompt gallery.
  `--ask "<prompt>"` runs one turn headlessly on stdout; verified live against
  OpenAI gpt-4o + Tavily (streaming, tool calls, layout applied, code generated).
  Keys live in a gitignored `*.secrets.config`, never in the tracked App.config.
  Headless coverage in `rustnet-designer --selftest` — incl. a kernel built for
  every provider. `docs/assistant.md`
- [x] **UI Designer: editors, panels and deploy** ✅ — centre tabs
  (Design / Layout XML / Code) on a shared AvalonEdit pane with cut/copy/paste,
  undo/redo, find, replace (match case, regex, live count, replace-all), go to
  line and a formatter (Roslyn for C#, XDocument for XML — unparseable text is
  left alone). Toolbox, inspector, output and assistant panels each show/hide and
  remember their width. File commands act on the active document with dirty
  markers and a Close. **Run to device**: `Detect` probes the virtual device and
  every serial port and takes the chip family from the device's own `info`;
  code → scratch project → `dotnet build` → RNX → RNSB → flash → start, layout →
  pushed to `/data/ui.xml` (no reflash). Same libraries as the CLI, in process.
  Full path exercised in `--selftest` against the virtual device.
  `docs/designer.md`
- [x] `Ui.ToXml` now round-trips container `pad`/`gap`, stack `orient`,
  `border` colour and radio `group` ✅ — the designer saves through it, so these
  were silently lost before

## Documentation
- [x] PLAN.md (roadmap) ✅  Progress.md (this file) ✅
- [x] docs/: protocols, networking, database, system, serialization, ui, dotnet-support, chips, simulator, designer, assistant ✅
- [x] README.md (English) + README.id.md (Bahasa Indonesia) ✅

---

## Roadmap ahead (v0.6+)

### Cloud connectivity (v0.6) — new `RustNet.Cloud`
- [ ] Azure IoT Hub client (MQTT + SAS, D2C telemetry, C2D, twin)
- [ ] AWS IoT Core client (MQTT/TLS, shadows, pub/sub)
- [ ] Google Cloud IoT Core client (MQTT + JWT, config/state)
- [ ] IFTTT Webhooks (Maker) trigger client (HTTP)
- [ ] `cloud-telemetry` template + on-device E2E

### Rich UI, graphics & designer (v0.7)
- [x] `RustNet.UI` components: window/stack/panel/border/canvas/grid +
  label/button/textbox/checkbox/radio/slider/progress/listbox/image, two-pass
  measure/arrange, hit-test + Tap input, ToXml round-trip ✅ (on-device verified;
  ScrollViewer open)
- [x] `RustNet.Graphics` bitmap blit: `Display.DrawImage` + `FillGradient` (linear) +
  `BlendImage` (alpha) + `SetClip`/`ClearClip` intrinsics ✅ on-device
- [x] Display panel config: `Display.Configure(driver,w,h,rotation)` + rotation-aware
  `Width()`/`Height()`; 0/90/180/270° panel rotation ✅ on-device
- [x] `RustNet.Drawing` + image decoders: **BMP + GIF (LZW)** managed + **PNG + JPEG**
  via runtime intrinsic (`image` crate) → Bitmap, pixel-exact host + on-device (all four
  decoded, blitted, framebuffer verified) ✅
- [x] Display support ✅: `Display.Configure(driver,w,h,rotation)` panel select +
  0/90/180/270° rotation + rotation-aware Width/Height (on-device verified)
- [x] **UI Designer** (`RustNet.Designer`, WPF) ✅: toolbox + WYSIWYG canvas +
  element tree + live property grid; open/edit/save RustNet.UI XML round-trip;
  designer-saved layout verified rendering on the virtual device
- [x] `RustNet.Resources` ✅: assets embedded in the RNX (v4), read by name
  on-device (GIF → GetBytes → decode → draw verified); image-viewer template.
  Also fixed `rustnet new` corrupting binary template assets

### Media & USB — chip-gated (v0.8)
- [x] Camera capture — `RustNet.Media.Camera` + `rustnet-hal` `Camera` trait +
  `SimCamera` colour-bar sim; on-device 8-bar capture verified pixel-exact ✅
- [x] Audio playback — `RustNet.Media.Audio` (Configure/Play/SamplesPlayed) over
  the I2S HAL; LE 16-bit PCM; on-device sample-count verified ✅
- [x] MJPEG video — `Video.EncodeJpeg` (runtime JPEG encoder) + MjpegWriter/
  MjpegReader (length-prefixed frames); on-device record→read→decode→draw ✅
- [x] VNC server — `RustNet.Media.Vnc` (Start/Stop/IsRunning); RFB 3.8 (None,
  32bpp, Raw) over TCP; verified on-device with a real RFB client ✅
- [x] USB — `RustNet.Usb` (UsbClient: BeginCdc/Read/Write; UsbHost:
  Enumerate/BulkOut/BulkIn) over `rustnet-usb` (CDC/HID/MSC + SimBus). Client,
  Host and PC-serial (CDC-ACM) verified on-device (enumerate + PING/PONG) ✅
- v0.8 media & USB **complete**

### .NET completeness (v0.9)
- [x] Reflection ✅ (LangApp E2E, on-device): `object.GetType()` +
  `Type.Name/FullName/Namespace/BaseType/ToString`, **method enumeration**
  (`GetMethods`/`GetMethod`/`MethodInfo.Name`, `==`/`!=`), **`typeof(T)`**
  (`ldtoken`+`GetTypeFromHandle`, identity = `GetType()`), **`MethodInfo.Invoke`**
  (boxed `object[]`, void→null; `MFLAG_HASRET`), **`GetFields`/`FieldInfo`**
  (`Name`/`GetValue`/`SetValue`, RNX **v5** descriptors), **`GetProperties`/
  `PropertyInfo`** (via `get_`/`set_` accessors), **type-level custom attributes**
  (`Type.GetCustomAttributes`, RNX **v6** ctor + const args, instantiated on read).
  Not surfaced: attributes on methods/fields
- [x] inline-array/span params lowering ✅ — `initobj <>y__InlineArrayN<T>`
  → heap array (size marked in operand); `<PrivateImplementationDetails>.
  InlineArray*` helpers are intrinsics (element byref / span = array);
  `string.Concat(span)` concatenates. `string.Concat` 5+ parts works (LangApp E2E)
- [x] VSCode DAP debugger ✅ — MetadataProcessor emits the RNX debug section
  (PDB sequence points → IL/line); RNDP debug cmds continue/step/clear/state/
  locals; `RnxDebugInfo` maps source↔(method,IL); `rustnet-debugger` DAP adapter
  bridges VSCode↔device over RNDP; VSCode extension contributes the `rustnet`
  debug type. On-device breakpoint-cycle E2E + DAP unit tests green.

**v0.9 COMPLETE.**

## Post-1.0 roadmap

### On-device ML & GNSS (v1.1)
- [ ] TensorFlow Lite for Microcontrollers — `RustNet.AI.TensorFlow`: load
  `.tflite` (via Resources), int8/float inference; Rust host bridge to
  TFLite-Micro on capable silicon, NotSupported on tiny targets
- [ ] Lightweight ML — `RustNet.ML`: pure-managed classification (binary +
  multiclass), regression, clustering (k-means), recommendation, small NN;
  models serialize via RustNet.Serialization
- [ ] GPS/GNSS NMEA parser — `RustNet.Sensors.Gnss`: streaming NMEA-0183
  (GGA/RMC/GSV/GSA/VTG) over UART → fix/position/velocity/satellite events

### Alternative UI frontends (v1.2) — packages, alt to RustNet.UI
- [ ] WinForms  [ ] GTK  [ ] Silk.NET + SkiaSharp  [ ] AsciiConsole (terminal)

### Community peripheral library (v1.3+) — shipped via `rustnet pkg`
Managed `RustNet.*` drivers over the HAL buses, contributed/versioned apart
from the core. Categories:
- [ ] **Sensors** — atmospheric (BME280/680/688, SHTx, AHTx, BMPx, Si70xx,
  MS5611, CCS811, SGP40…); motion (MPU6050, LSM6DSOX, BNO055, BMI270,
  ADXL3xx, HMC5883, LIS3MDL…); light (BH1750, TSL2591, VEML7700, SI1145…);
  distance (VL53L0X, HC-SR04, MaxBotix, A02YYUW…); environmental/AQI
  (SCD30/40/41, ENS160, PMSA003I, AGS01DB…); color (TCS3472x); camera/thermal
  (Arducam, VC0706, AMG8833, MLX90640); temperature (LM75, MCP9808, TMP102,
  MAX6675, MCP960x, thermistor); HID/touch (XPT2046, TSC2004, MPR121,
  keyboard, joystick); GNSS modules (MT3339, NEO-M8, BG95); load cell
  (HX711, NAU7802); soil moisture (capacitive, FC-28); power/current
  (INA219/228/260); fuel gauge (MAX17043/44); flow (Hall Gr/YF-B); sound (KY-038)
- [ ] **Displays** — character/segment (HD44780, TM1637, 7/14/16-seg);
  mono OLED (SSD1306/1309, SH1106/1107, CH1115, UC1609C, ST7565); color TFT
  (ILI9341/916x/94xx, ST7735/7789/7796S, SSD1331/1351, GC9A01, HX8357);
  e-paper (IL0373…, SSD1608/1680/1681, UC8151C, Waveshare EPD 1.54"–7.5")
- [ ] **Actuators/motors** — DC (HBridgeMotor, BidirectionalDcMotor); stepper
  (A4988, ULN2003, GpioStepper); ESC (brushless/drone); industrial
  (CerusXDrive VFD, Tb67h420ftg, StepperOnline BLD510B); LEDs (Led, PwmLed,
  RgbLed, RgbPwmLed, LedBarGraph, WS2812, APA102, PCA9633); Relay; PiezoSpeaker
- [ ] **Support ICs** — ADC/DAC (ADS101x/111x, ADS7128, MCP3xxx, MCP492x);
  IO expanders (MCP23xxx, PCF/PCA857x, TCA9535/9555, HT16K33); RTC (DS1307,
  DS3231/3232, PCF8523); digital pot (MCP4xxx)

### NPC — Native Procedure Caller (v1.4, RLP-style)
Call native Rust from C#, loaded at runtime, no firmware rebuild. **Feasible**
— reuses existing byte-array marshalling + RNSB signing + the intrinsic
dispatch seam; the new work is a loader + a stable C ABI
(`fn(args, len, out, cap) -> i32`). Chip-gated on code execution:
- [ ] Host/virtual device (std) via `libloading` — fully feasible; reference
  impl + E2E (build+sign+load+invoke a Rust cdylib, check result)
- [ ] RAM-executable silicon (ESP32 IRAM / Cortex-M SRAM) — PIC blob +
  relocating loader (W^X aware)
- [ ] XIP/pure-Harvard chips — `NotSupported`
- [ ] Toolchain: `rustnet native build`/`push` (RNSB-signed) + `Npc.Load`/
  `Npc.Invoke`; native code runs unsandboxed (signing gates *who* loads)

### RP2040 silicon (v1.5) — Raspberry Pi Pico / Pico W
Bring RustNet to the **RP2040** (dual Cortex-M0+ @ 133 MHz, 264 KB SRAM, XIP
QSPI flash, native USB 1.1, PIO). Same staged path as the ESP32 bring-up (v0.5);
`no_std + alloc` core + HAL traits already exist, so it's a board crate + a
firmware target:
- [ ] Core profile for `thumbv6m-none-eabi` — `portable-atomic`
  (no native CAS) + libm/soft-division, like the RISC-V no_std profile
- [ ] Board crate `rustnet-hal-rp2040` over `rp-hal`/Embassy — GPIO/PWM/ADC/
  I2C/SPI/UART behind the HAL traits (every `RustNet.*` API unchanged)
- [ ] **PIO** state machines → `Signal`/RMT pulse gen/capture + WS2812/APA102
  (a target-unique differentiator)
- [ ] Deploy over **native USB-CDC** — RNDP on the existing byte-pipe transport
  (`serve_pipe`/`--stdio`); reuses the v0.8 `RustNet.Usb` device stack, no extra HW
- [ ] Persistent app+provisioning in XIP QSPI flash → **OTA A/B** slots
- [ ] **Pico W** (CYW43): optional WiFi STA + RNDP TCP :7878 (ESP32 phase-2
  path); plain Pico stays USB-only
- [ ] NPC on RP2040 — SRAM is W+X on M0+, so the v1.4 RAM-executable path applies
- [ ] HIL: Pico target in `tools/hil-smoke.sh` (BOOTSEL/UF2 via `elf2uf2`/
  `picotool`) → on-chip E2E (provision → RSA-verify flash → C# app w/ live GPIO)
- Note: the Linux-class **Pi Zero** (Broadcom SoC) is different — it runs the
  standard `std` host build directly; this targets the RP2040 MCU (Pico family).

## Verification snapshot (2026-07-20, v0.5)
- `cargo test --workspace`: **95 passed** (incl. the stdio-transport pipe test)
- no_std cross-builds green: rustnet-core, rustnet-hal and
  rustnet-hal-esp32c3 for `riscv32imc-unknown-none-elf`
- `dotnet test`: **10 passed** — E2E matrices for SampleApp (core),
  SysApp (device surface) and LangApp (inheritance, interfaces, casts,
  user generics, filters, async/await) on the virtual device
- 3 templates instantiated, built and executed on the virtual device
- **Real hardware**: ESP32-WROOM-32 on COM4 ran LangApp (full v0.4
  language matrix incl. async/await) and a managed GPIO blink — flashed,
  RSA-verified and logged entirely through `rustnet --device serial:COM4`
- **Phase 2 on-chip**: apps + provisioning persist across `rustnet reboot`
  (FAT/wear-levelling); ADC read (GPIO34), LEDC PWM LED sweep and I2C
  NACK handling all exercised from C# on the device
- **Phase 3 on-chip**: TWAI CAN self-test round trip (id 0x123 back),
  SNTP real-time RTC (2026-07-20 06:26 UTC), UART1 write, RMT signal
  generate — all run from C# over WiFi; app-thread stack made
  configurable to survive post-WiFi heap fragmentation
- **Fully wireless operation verified**: device joined the user's WLAN,
  and `rustnet flash/start/logs --device tcp:<ip>:7878` ran the complete
  deploy cycle over WiFi — incl. on-chip RSA verification (heap-budget
  fixes: trimmed WiFi/lwIP buffers, right-sized thread stacks)
