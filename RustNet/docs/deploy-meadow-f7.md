# Deploying a C# App to a Wilderness Labs Meadow F7 Micro

End-to-end procedure for running a .NET application on a **Meadow F7 Micro v1.0**
(STM32F777, Cortex-M7 at 216 MHz) — over the board's own USB socket, with no
probe.

There are **two phases**:

| | How | When |
|---|---|---|
| **A. Flash the firmware** | USB DFU — buttons only the very first time | Once, and again whenever the firmware changes |
| **B. Replace the application** | `rustnet flash` over the same USB port | Every time you change your C# code |

Phase B is the one you do repeatedly, and it is cheap: the application lives in
the module's 32 MB QSPI flash, is replaced without a reboot, and survives both
a power cycle **and a firmware reflash** — because it is not in the image.

> **Flashing this replaces Meadow OS.** DFU into internal flash is the only way
> in without a probe, and it overwrites Wilderness Labs' own runtime. It is
> reversible — their `meadow` CLI puts it back.

## The board

| | |
|---|---|
| MCU | STM32F777, Cortex-M7 at **216 MHz** |
| RAM | 512 KB; this port uses SRAM1+SRAM2 (384 KB) and leaves DTCM out |
| Flash | 2 MB internal for the image; **32 MB QSPI** (S25FL256L) for storage |
| SDRAM | 32 MB on the module — not yet driven by this port |
| Clock | **25 MHz crystal** (X401, Abracon ABM12W-25) on PH0/PH1 |
| Console | USB CDC from the MCU, and UART4 on the `D0`/`D1` header pins |
| RGB LED | **PA2 red, PA1 green, PA0 blue** |
| Coprocessor | ESP32-PICO-D4, running **ESP-AT built for this module** — WiFi works |

Every one of those pin facts comes from Wilderness Labs' published schematic,
`Meadow_Hardware_Designs/Meadow_F7v1/Micro_Dev_Module/MeadowF7Micro_REVD.pdf`,
and none of them is in the developer-portal documentation.

### Why RAM starts at SRAM1

The 128 KB below `0x20020000` is DTCM: the fastest memory on the part, tightly
coupled to the core and **not reachable by DMA**. Handing it to the allocator
would work until the first driver DMAs into a buffer that happened to land
there. The Netduino's map leaves its CCM out for the same reason.

# Phase A — flash the firmware

## A1. One-time: tools

```bash
rustup target add thumbv7em-none-eabihf
cargo install cargo-binutils --locked
rustup component add llvm-tools
# plus dfu-util
```

## A2. Enter DFU and flash

**The first time only**, put the board into DFU by hand: hold **BOOT**, tap
**RESET**, release BOOT. It enumerates as `STM32 BOOTLOADER` (`0483:df11`).

```bash
rustnet firmware build --board meadow-f7
rustnet firmware flash --board meadow-f7
```

**Afterwards, name the port and the board puts itself into DFU:**

```bash
rustnet firmware flash --board meadow-f7 --device serial:COM17
```

A RustNet image already on the board answers an RNDP reboot-to-bootloader
request; the ESP32 bridge image, which speaks no protocol at all, is asked
instead by opening its port at **1200 baud** — the same trigger Arduino boards
have used for a decade. Between them, nothing has to touch the buttons again.

> The mechanism arms a magic word in a RAM location the startup code does not
> clear and then performs a **real reset**, rather than jumping to the ROM from
> a running image. A jump hands the bootloader a chip whose clocks, USB core
> and caches are configured for the firmware that jumped — which is the same
> mistake `dfu-util --leave` makes, and the reason PH0/PH1 have to be returned
> to analog on the way in.

Or the Workbench: **FIRMWARE ▸ BOARD FIRMWARE**, pick `meadow-f7`.

## A3. Confirm it is alive

The board comes back as an ordinary COM port:

```bash
rustnet info --device serial:COM17
{"chip":"stm32f7","board":"Meadow F7 Micro","protocol":1,"cpu_hz":216000000,
 "hse_hz":25000000,"transport":"usb-cdc+uart4",
 "chip_id":"chip: STM32F76x/F77x (dev 0x451 rev 0x1001), 2048 KB flash",
 "chip_expected":true}

rustnet logs -n 20 --device serial:COM17
--- RustNet on Meadow F7 ---
RustNet on Meadow F7 Micro @ 216 MHz, heap 256 KB
hse: 25 MHz crystal (X401, ABM12W-25)
chip: STM32F76x/F77x (dev 0x451 rev 0x1001), 2048 KB flash, uid 00400020
app: 67 methods, 19 types, 91 strings
[C#] blinking the user LED
```

The green LED responds — but **inverted**, and that is expected. This board's
RGB LED is common-anode: its shared pin sits at VCC and each colour returns
through a resistor to the MCU, so a pin driven **low** lights it. The Blink
demo is shared with the STM32F4 port, where the LED is active-high, and writes
`true` to mean "lit". On a Meadow that reads as mostly-on with two brief dark
flickers rather than two blinks and a pause.

The application is genuinely running either way — the pin changes on cue, and
`[C#] blinking the user LED` in the log comes from the interpreter. If you want
the intended pattern, invert the writes in your own app: `Gpio.Write` is a raw
pin API and deliberately does not know about board polarity.

`chip_expected` is worth reading. The firmware asks the silicon what it is
rather than asserting it, because being wrong there is quiet: a memory map
sized for the wrong part does not fail at link time or at boot, it fails later
in whatever code was running when the allocator first ran past real memory.

## A4. The serial console

Not required, and invaluable when something is wrong. A USB-serial adapter on
the header:

| Adapter | Meadow |
|---|---|
| RX | `D1` (UART4 TX, PH13) |
| TX | `D0` (UART4 RX, PI9) |
| GND | GND — **not optional**, and the most commonly forgotten wire |

115200 8N1. The board prints its banner at boot and a line every second after
that, so you can attach at any time and see something within a second — reading
a boot banner is otherwise a race you lose silently.

`D0`/`D1` are **UART4**, which the schematic labels `COM1` and the Meadow
documentation calls `COM4`. `COM2` carries USART1 on PB14/PB15. Guessing from
the name would put you on the wrong peripheral.

The link is a console until a tool speaks RNDP at it, and a protocol link
thereafter: one wire cannot carry human text and binary frames at the same
time without the frames being corrupted by the text.

# Phase B — your application

The application is compiled to RNX on the PC and embedded in the image:

```bash
dotnet build runtime/firmware-stm32/demo/Blink/Blink.csproj
rustnet build runtime/firmware-stm32/demo/Blink/bin/Debug/net10.0/Blink.dll \
    -o runtime/firmware-stm32/demo/Blink.rnx
rustnet firmware build --board meadow-f7   # then flash as above
```

The interpreter is the same one every other port runs, so the language surface
is the same — see [`dotnet-support.md`](dotnet-support.md). The heap is 256 KB,
which is the most generous of any bare-metal port here.

What the firmware exposes to C# today is GPIO (`RustNet.Hal.Gpio::SetMode`,
`Write`, `Read`, `Toggle`), the console and the clock. Flashing apps over the
wire, provisioning and a filesystem all need storage this port does not have
yet; the 32 MB QSPI flash on the module is the obvious home for it.

# What can go wrong

**Everything times out and the LED shows red once.** The clock tree did not come
up on the crystal. The red/green stage codes are in `src/main.rs`; red means
`HSERDY` never asserted.

**The board is flashed and behaves oddly, but a power cycle fixes it.**
`dfu-util --leave` makes the ROM bootloader *jump* to the application rather
than resetting the chip, so peripherals arrive still configured for the
bootloader's own session. The firmware handles the two cases this bit us on —
it returns PH0/PH1 to analog before starting the oscillator, and resets the OTG
core through `RCC_AHB2RSTR` rather than merely clocking it — but a power cycle
remains the cleanest way to reproduce a real boot.

**Nothing on the serial console at all.** Check GND first. If the adapter is a
Prolific PL2303, check that it opens: a counterfeit chip enumerates, reports no
problem in Device Manager, and fails every open with `ERROR_NO_SUCH_DEVICE`. A
CP210x or CH340 avoids the question.

# The bug that cost this port a day

Every wait returned instantly, USB never enumerated, timed blinks were
invisible, and the serial console produced not one byte. Four symptoms, one
cause: **the Cortex-M7's DWT ships locked.**

`DwtDelay::start` enabled `TRCENA` and `CYCCNTENA`, which is all a Cortex-M4
needs and had worked on the F4 boards for a year. The M7's DWT is CoreSight and
requires the key `0xC5ACCE55` in `DWT_LAR` first; without it the write to
`DWT_CTRL` is discarded and the cycle counter never leaves zero. Nothing
reports an error — the delay source simply answers that no time has passed.

The clue was that the console and the timed blinks went quiet *together*. One
dead peripheral cannot do that; one dead clock can. It is the same shape as the
K210's `mstatus.FS` trap: a core facility the previous architecture left
enabled and this one does not.

# WiFi, through the ESP32 coprocessor

The module carries an ESP32-PICO-D4 wired to the STM32 two ways: SPI2, which is
how Meadow OS talks to it with firmware and a protocol of Wilderness Labs' own,
and **UART5** — PB13/PD2 on the STM32, GPIO1/GPIO3 on the ESP32 — which is the
one anything else can use. `ESP32_RESET_L` is PF7 and `ESP32_BOOT_L` is PI10.

`--features esp-bridge` builds a firmware that is a transparent USB-to-ESP32
bridge: bytes forwarded verbatim, the host's `DTR`/`RTS` driven onto the
ESP32's `GPIO0`/`EN`, and the host's baud rate followed. That makes the board
look like the USB-serial chip `esptool` expects, and it works:

```
$ esptool --port COM17 --chip esp32 flash-id
Chip type:          ESP32-PICO-D4 (revision v1.0)
Crystal frequency:  40MHz
MAC:                d8:a0:1d:69:74:10
Detected flash size: 4MB
```

That also confirms every pin above independently — `esptool` cannot sync if any
one of them is wrong.

> **The bridge and the normal firmware are the same output file.** Cargo writes
> both to `target/thumbv7em-none-eabihf/release/rustnet-firmware-meadow-f7`, and
> `rustnet firmware flash` flashes what is already there rather than rebuilding.
> After any bridge work, run `cargo build --release` with no features **before**
> flashing, or the board comes back as a bridge again: enumerating happily,
> answering no tool, saying nothing on the console.

## Back up the coprocessor before touching it

```bash
esptool --port COM17 --chip esp32 --baud 460800 read-flash 0 0x400000 esp32-original.bin
```

Wilderness Labs do not publish their coprocessor firmware, so that image is the
only way back. A copy taken from this board lives in
`CodeSandbox/meadow-f7-esp32-restore/`, with the restore command in its README.

## Why the published ESP-AT binaries cannot work here

The stock images are built for the **ESP32-WROOM-32**, and on this board they
are silent: flashed, hash-verified, erased and rewritten from a clean slate,
then probed at every baud rate from 9600 to 921600 with nothing to show for it
but the single byte of noise a reset makes.

Patching the `mfg_nvs` partition to move the AT port onto UART0 — the only
ESP32 UART wired to the STM32 here — did not help, and the reason is that
**the thing in the way is a build-time setting, not a runtime one**. ESP-IDF
puts its console on UART0 by default; ESP-AT's own documentation says the
console must be disabled to use UART0 for AT, and `CONFIG_ESP_CONSOLE_NONE`
cannot be changed after the image is built. No amount of NVS editing reaches
it.

> An earlier version of this page blamed GPIO16/17 — the WROOM-32 default AT
> pins, which on a PICO-D4 belong to the module's embedded flash. That is a
> real hazard and worth knowing, but it does not explain this failure: the
> patched NVS already pointed the port at GPIO1/3, so those pins were never
> muxed. Espressif's own table has had a `PICO-D4` row using GPIO22/19 all
> along, precisely because they know 16/17 are unusable there.

## Building ESP-AT for this module

So it takes a source build. ESP-AT's `factory_param_data.csv` already carries a
`PICO-D4` row; what this board needs on top is that row pointed at UART0
(`uart_port 0`, `tx 1`, `rx 3`) and a module config that turns the console off.

```bash
git clone --recursive https://github.com/espressif/esp-at.git C:/esp-at
cd C:/esp-at
python build.py install     # pulls the ESP-IDF that module_config/*/IDF_VERSION pins
python build.py build
```

Two Windows-specific traps on that first command: clone somewhere short —
`MAX_PATH` bites long submodule paths, and `git config --system core.longpaths
true` is worth setting anyway — and run it from **PowerShell**, because the
installer refuses an MSys/Mingw shell outright.

`factory_param_data.csv`, PICO-D4 row:

```
PLATFORM_ESP32,PICO-D4,"4MB, Wi-Fi + BLE, OTA, TX:1 RX:3 (UART0, for Meadow F7)",4,78,0,1,13,CN,115200,1,3,-1,-1,1
```

`module_config/module_pico-d4/sdkconfig.defaults`:

```
CONFIG_ESP_CONSOLE_NONE=y
CONFIG_ESP_CONSOLE_UART_DEFAULT=n
CONFIG_BOOTLOADER_LOG_LEVEL_NONE=y
CONFIG_AT_UART_DEFAULT_FLOW_CONTROL=0
CONFIG_BT_ENABLED=n
CONFIG_ESP_COEX_SW_COEXIST_ENABLE=n
```

**The Bluetooth line is not optional.** The stock PICO-D4 configuration
produces an image `0xd010` bytes larger than the 1.5 MB `ota_0`/`ota_1`
partitions its own table defines — the shipped configuration does not fit its
own partition layout, and the build fails at `check_sizes.py` rather than
producing something flashable. This board wants a WiFi radio, so dropping the
Bluetooth stack costs nothing and leaves 26% of the partition free.

## Flashing it, through the bridge

```bash
cd runtime/firmware-meadow-f7
cargo build --release --features esp-bridge
rustnet firmware flash --board meadow-f7 --device serial:COM17

esptool --chip esp32 --port COM17 --baud 460800 write-flash \
    --flash-mode dio --flash-freq 40m --flash-size 4MB \
    0x0 C:/esp-at/build/factory/factory_PICO-D4_unfilled.bin

cargo build --release
rustnet firmware flash --board meadow-f7 --device serial:COM17
```

Both `firmware flash` steps use the automatic DFU entry described in A2, so
this whole sequence needs no buttons. Verify before switching back — the bridge
is transparent, so a terminal on COM17 at 115200 talks straight to the radio:

```
AT
OK
AT+GMR
AT version:4.1.1.0(ba4dd0e - ESP32 - Jul 31 2025 08:37:48)
Bin version:v4.1.1.0(PICO-D4)
```

## Using it

Credentials are configured on the device, never compiled into an application:

```bash
rustnet wifi --device serial:COM17 --ssid <name> --psk <password>
```

That joins immediately *and* stores the pair in QSPI, so the firmware re-joins
on every boot. `rustnet info` then reports it:

```json
{"wifi":true,"wifi_ssid":"...","wifi_ip":"192.168.18.247", ...}
```

From C#, the same `RustNet.Net.Wifi` API as every other target:

```csharp
if (Wifi.IsConnected())
    Console.WriteLine($"on '{Wifi.GetSsid()}' as {Wifi.GetIp()}");
```

`Connect(ssid, psk)` works too, for an application that manages its own
networks. `demo/WifiJoin` is a complete example, and it contains no SSID and no
password — it asks the device what it is on. A flashed `.rnx` gets copied,
mailed and committed; anything baked into it travels with it.

The AT client lives in `src/espat.rs`. Two details that matter if you extend
it: the UART has no receive FIFO, so the reply loop polls every 20 µs and
cannot use `serviced_delay` (servicing USB takes long enough to lose bytes),
and `AT+CWJAP` gets a 20-second timeout because a join is an association plus
DHCP plus whatever the access point feels like.
