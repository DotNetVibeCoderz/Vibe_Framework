# RustNet on the RP2040 (Raspberry Pi Pico)

Bare metal, Cortex-M0+, no vendor SDK. The IL interpreter runs on-chip and the
demo application is the *same* `Blink.rnx` the STM32 and K210 ports carry.

The smallest target in this repository: 264 KB of SRAM, no external RAM, and
a 128 KB heap for the interpreter.

## Status

| Surface | State |
|---|---|
| Boots from flash | **runs on chip** — the board leaves BOOTSEL and stays out of it, so the second stage, the vector table and the UF2 are all right |
| Clocks (XOSC + PLL to 125 MHz) | **runs on chip** — via `rp2040-hal`, proven by a working USB device, which needs an exact 48 MHz |
| GPIO | **written** — SIO, with the pad and function muxing a pin needs. `Gpio.Write(pin, true)` from C# was a silent no-op until now: a C# `bool` reaches the host as an int, and the arm matched only `HostValue::Bool`. Fixed with the widening the other ports use; on-chip check pending |
| UART0 (PL011) | **written**, divisor arithmetic host-tested against the vendor SDK's values |
| 64-bit microsecond timer | **written**, including the watchdog tick it needs |
| IL interpreter | **written** — `rustnet-core`, `no_std + alloc`, 128 KB heap |
| USB CDC | **runs on chip** — the board enumerates as its own COM port (`2E8A:000A`), no serial adapter needed |
| IL interpreter running | **runs on chip** — the port stays enumerated, and a device that stops being polled drops off the bus |
| RNDP over USB | **runs on chip** — `rustnet info` and `rustnet logs` answer over the board's own port |
| Flash filesystem | **runs on chip** — 1 MB above the image, `rustnet-flashfs`, survives both reboot and a firmware reflash |
| Provisioning + secure boot | **runs on chip** — RSA/SHA-256 over RNSB, `ChipFamily::Rp2040`; a second key is refused |
| Flashing applications | **runs on chip** — `rustnet flash` replaces the running app without a reboot; `autostart` runs it on power-up |
| Reboot into BOOTSEL | **runs on chip** — `reboot` with a payload of `01`, so reflashing needs no finger on the button |
| PWM (8 slices × A/B) | **written + host-tested** — divider/TOP/compare arithmetic; `Pwm.Configure`; on-chip test pending |
| ADC (GP26–29 + temperature sensor) | **written + host-tested** — `Adc.ReadRaw/ReadMillivolts`; on-chip test pending |
| I2C0/I2C1 (DW_apb_i2c) | **written + host-tested** — SCL counts match the SDK; NACK → exception; `I2c.Write/Read`; on-chip test pending |
| SPI0/SPI1 (PL022) | **written + host-tested** — dividers never exceed the request, modes 0–3; `Spi.*`; on-chip test pending |
| UART1 for applications | **written** — `Uart.*` on port 1 (GP8/GP9); port 0 stays the console and is refused; on-chip test pending |
| Signal control on PIO | **written + host-tested** — hand-assembled PIO programs (checked against `pioasm` words), `Signal.Generate/Capture/PulseFeedback`; on-chip test pending |
| `RustNet.IO.FileSystem` | **written** — the full set over the flashfs window; `/.sys` refused to applications, 32 KB budget for their files; on-chip test pending |
| `data push` | **written** — through the same gate as app writes: `/.sys` refused (a push could otherwise replace the provisioning key), files budget and RAM check applied; on-chip test pending |
| NPC (`RustNet.Native`) | **runs on chip** — a `thumbv6m` RNPC module placed in SRAM (`src/npc.rs`), sealed with `--chip rp2040`; the NpcDemo's sum / CRC-32 / reverse / error-code checks all pass on a Pico |
| Crash reports + safe mode | **runs on chip** — a hard fault or panic notes where in the watchdog scratch registers and resets; the next boot logs it and does not start the app, so a crashing app cannot take the USB link down for good (`src/crash.rs`) |
| OTA A/B | **runs on chip** — slot picker (`boot/`, 2.2 KB) + two firmware slots + an append-only boot-state log; push (680 KB in ~20 s over USB), confirm across a reboot, and the 3-boot rollback all seen on a Pico — [below](#ota-ab) |

The board is its own console now. Plug it in and it appears as a COM port
like any other serial device.

"Host-tested" means every piece of arithmetic and register packing is a unit
test in `cargo test -p rustnet-hal-rp2040` (64 tests); "on-chip test pending"
means nobody has watched it run on a Pico yet. The checklist for that pass is
[below](#on-hardware-checklist).

## Bench run, 2026-10-21 (Pico, no jumpers fitted)

The PeripheralTour on the chip: **20 passed, 7 failed, 1 skipped**, and every
failure is a loopback whose jumper was not fitted (GP15→GP14 capture and
echo, GP19→GP16 SPI in four modes, GP8→GP9 UART). Seen working:

- ADC: temperature sensor 26.5 °C (raw 878), GP26 read, channel 5 refused
- PWM: the GP25 breathe, the GP15 1 kHz source, 1 Hz refused
- Signal: `Generate` three flashes in 752 ms (want 750 ± 20); pulse
  feedback with nothing wired returns 0 after its timeout, without hanging
- SPI0 write + read at 8 MHz; I2C0 scan with NACK reported as an error; UART0
  refused
- the full `FileSystem` set, and all three `/.sys` attempts refused

Three firmware faults the run found and fixed: `flash --start` did not
replace a sleeping app until it ended its fuel slice — up to a minute (the
interpreter now yields after every sleep); the RNDP receive buffer kept the
capacity of the largest frame, 25 KB out of a 128 KB heap, and polling `logs`
during the tour then ran the heap dry (it is released once drained); and the
resulting panic left the port listed but dead, which is what `src/crash.rs`
now turns into a reported safe-mode boot. With the tour loaded the heap
stands at ~105 KB of 128 KB, so the Pico's headroom for large apps is thin.

Still to see with jumpers: capture/echo widths, SPI loopback, UART1.

## Peripherals and pins

The bus, port and channel numbers C# passes, and where they come out on a
Pico. Pins are muxed the first time a bus is used, so an application that
never touches a bus keeps its pins for `Gpio`. None of the defaults overlap.

| `RustNet.Hal` | Number means | Pico pins |
|---|---|---|
| `I2c.Write/Read(bus, …)` | 0 = I2C0, 1 = I2C1 | I2C0: SDA **GP4**, SCL **GP5** · I2C1: SDA **GP6**, SCL **GP7** (internal pull-ups on; fit 4.7 kΩ for real buses) |
| `Spi.*(bus, …)` | 0 = SPI0, 1 = SPI1 | SPI0: SCK **GP18**, MOSI **GP19**, MISO **GP16** · SPI1: SCK **GP10**, MOSI **GP11**, MISO **GP12**. CS is the app's, by `Gpio` (GP17 / GP13 by convention) |
| `Uart.*(port, …)` | 1 = UART1 | TX **GP8**, RX **GP9**. Port 0 is the console (GP0/GP1) and is refused |
| `Adc.*(channel)` | 0..3 = GP26..GP29, 4 = temperature | GP26–GP28 free; GP29 is VSYS/3 on a Pico |
| `Pwm.Configure(channel, hz, duty)` | the GPIO number | any pin; **GP25 is the LED**. Pins 2n and 2n+1 share a slice and so a frequency (GP24/GP25, GP14/GP15…) |
| `Signal.*(pin, …)` | the GPIO number | any pin, through PIO0 state machine 0 |

The limits worth knowing before a bench session:

- **PWM** runs from `clk_sys`: about 8 Hz up to 62.5 MHz, duty 0..10000.
  `TOP` is chosen as large as the divider allows, so 1 kHz has ~16 bits of
  duty resolution; above the audio range the steps get coarse (10 MHz comes
  out as 9.6 MHz). `disable` holds the output low rather than stopping the
  slice, because the slice's other output belongs to another pin.
- **I2C** always uses the controller's fast-mode counts, as the SDK does —
  100 kHz with fast-mode counts is standard mode on the wire. 10 kHz–1 MHz.
  An empty `Write` (a probe) is sent as a one-byte read, because this
  controller cannot address a device without moving a byte and a one-byte
  write would move a register pointer.
- **SPI** rates are never above the request: 1 MHz gives 992 kHz, the
  slowest is 1.92 kHz, the fastest `clk_peri / 2` = 62.5 MHz.
- **Signal** runs PIO at 5 MHz (exactly `/25` from 125 MHz): generated edges
  are exact to the microsecond (segments 1 µs to 429 s), captured widths are
  good to the 0.4 µs capture loop. `Capture` waits up to `timeout` for the
  first edge, then stops at `maxEdges` or at an edge-free stretch longer than
  `timeout` — the same rule the ESP32's RMT capture uses. `PulseFeedback`
  drives the trigger, releases the line with a pull toward idle, and times the
  first whole pulse at the trigger's polarity; 0 means no echo. **Every
  Signal call blocks the whole board, USB service included** — keep timeouts
  to what the sensor needs.
- **Files**: `/.sys` (provisioning key, the flashed app, autostart) is the
  firmware's. Any path that normalises into it — `/.sys/x`, `\.sys\x`,
  `./.sys/x` — is refused, and `List("/")` hides it. Application files are
  capped at 32 KB in total: when the 1 MB window fills, flashfs compacts by
  holding every live file in RAM, and `rustnet flash` must always be able to.
  Every write is also checked against free heap before anything is touched.

## Build and flash

```bash
cd runtime/firmware-rp2040
cargo build --release                          # the firmware, linked for slot A
(cd boot && cargo build --release)             # the slot picker, at offset 0
python tools/elf2uf2.py boot/target/thumbv6m-none-eabi/release/rustnet-boot-rp2040 \
    target/thumbv6m-none-eabi/release/rustnet-firmware-rp2040 \
    --blank 0xFE000:0x2000 rustnet-pico.uf2
# Hold BOOTSEL while plugging the board in, then:
cp rustnet-pico.uf2 /d/          # whatever letter RPI-RP2 mounts as
```

`rustnet firmware flash --board pico --device serial:COMn` does all of that,
and asks a running board into BOOTSEL itself. The UF2 must carry **both**
programs: the firmware alone has nothing at offset 0 for the ROM to run, and
the packer refuses it. `--blank` erases the OTA boot state, so the picker
boots the slot A it was just given.

The board reboots as soon as the last block lands, and the RPI-RP2 drive
disappears. **If the drive comes back, the image was rejected** — that is the
ROM falling back, and it is the only failure signal this board gives you.

## Three things that are not obvious

**Every peripheral starts held in reset.** There is no clock-enable register to
forget; there is a `RESETS` block containing all of them, and a write to a held
peripheral is discarded with no fault and no flag. A UART configured before it
is released reads back as zeros and transmits nothing, which looks exactly like
a wrong pin assignment.

**The chip boots on a ring oscillator at roughly 6 MHz**, not on its crystal —
"roughly" because it is an on-die RC whose rate moves with temperature. Nothing
in the datasheet's timing tables is true until the crystal and the PLL are up,
so a UART configured before `clocks::init` runs at whatever the oscillator
drifted to that day.

**The microsecond timer needs the watchdog.** Its tick is generated by the
watchdog block and is off at reset. Without it the timer reads zero forever, so
every delay returns instantly and every timeout fires at once — which presents
as a hung peripheral somewhere else entirely.

## The UF2 packer, and the bug it now documents

`tools/elf2uf2.py` exists so the port's toolchain is a Rust target and a Python
interpreter, with nothing to `cargo install`.

It got this wrong once, in a way worth keeping: a linked RP2040 image has
segments at addresses like `0x100001c0` — neither 256-aligned nor abutting —
and the first version emitted blocks per segment. That produces two faults at
once. A short final block that declares a full 256 bytes writes padding over
the start of the next segment, and a block whose address is not page-aligned is
not where the bootloader puts it.

The board's response to both is identical and silent: it accepts the file and
comes straight back as BOOTSEL. Flattening the segments into one image first,
padding to a whole page, and declaring 256 bytes on every block — which is what
`picotool` and `elf2uf2-rs` do — fixed it.

## The USB stack, and the bug that was not in it

A hand-written CDC stack reached "the host sees a device" and no further:
*Device Descriptor Request Failed*. Four fixes went in — an unconfigured USB
PLL, the DPRAM write hazard on `AVAILABLE`, control transfers longer than one
packet, an unarmed EP0 status stage — each a real fault, none of them the
cause. Every one was flashed blind, because this port had no console until USB
worked.

Swapping in `usb-device` and `usbd-serial` produced *exactly the same failure*,
and that was the finding. Two independent implementations failing identically
is not two bugs; it is something neither of them owns.

What they shared was the schedule around them. Enumeration must be answered
within milliseconds and is abandoned after a couple of seconds, and boot had
several holes far longer than that: a 200 ms LED signal, two 240 ms blinks, the
banner over a 115200 UART, and parsing the RNX module — none of them servicing
the bus. An unanswered enumeration is reported with the same message a corrupt
descriptor gives, which is what kept sending the search back to the
descriptors.

So the device enumerates fully before anything slow runs, and every wait on the
boot path goes through a delay that keeps polling. That is also why a live COM
port is evidence the interpreter is running: a device that stops being polled
drops off the bus, and this one stays.

## Console

The board's own USB, as a CDC-ACM port. Console output also goes to UART0 on
GP0 (TX) and GP1 (RX) at 115200 for anyone with an adapter attached, but
nothing needs one.

The banner is printed once at boot and not buffered, so opening the port
afterwards shows nothing until the application writes something — the Blink
demo is silent by design.

RNDP runs over this port. `rustnet info` and `rustnet logs` work against a
Pico with no adapter:

```console
$ rustnet provision --key keys/rustnet-signing.pub --device serial:COM12
device provisioned with signing public key

$ rustnet flash LanguageTour.dll --name tour --key keys/rustnet-signing.key       --chip rp2040 --device serial:COM12
flashed 'tour' (13035 bytes, signed, chip=Rp2040)

$ rustnet apps autostart tour --device serial:COM12
$ rustnet reboot --device serial:COM12
$ rustnet logs -n 20 --device serial:COM12
RustNet on Raspberry Pi Pico @ 125 MHz (peri 125 MHz), heap 128 KB
[sec] provisioned
[app] tour from flash (12763 bytes)
app: 117 methods, 24 types, 185 strings
RustNet C# language tour, interpreted on bare metal
  [ok] string interpolation
  ...
passed 9, failed 0
```

## Storage, and the two things flash makes you learn

The window starts 1 MB into the QSPI part. The image is about 325 KB
and grows; starting there leaves room for it to double without moving, and
moving it would invalidate everything already stored above.

**Programming flash stops execute-in-place, and the program is executing from
that flash.** Every write runs from `.data.ram_func` with interrupts masked and
goes through the boot ROM's routines, which live in mask ROM rather than in the
part being written. The XIP cache has to be flushed afterwards too, or a
read-back returns the old contents and reads as a write that silently did
nothing.

**The ROM insists on whole 256-byte pages; a log-structured store does not
produce them.** The first `data push` worked and the second failed, because
only the first record starts at offset 0. So the driver absorbs it: an
unaligned write widens to the pages containing it and carries the surrounding
bytes over from what is already there. Reprogramming a byte with the value it
already holds is a no-op in the silicon — which is also why this can only ever
rewrite those bytes identically. Programming clears bits; nothing here sets one
back.

## Every wait has to serve the bus

Three separate failures on this port turned out to be one rule, and it is the
thing to remember before adding anything that blocks:

- Enumeration failed because boot had holes of hundreds of milliseconds — LED
  signals, the banner, parsing the module — with nothing servicing USB.
- Opening the port failed with *the semaphore timeout period has expired*,
  because `sleep_ms` slept, and an application spends nearly all its time
  there.
- Frames arrived and were never answered, because `sleep_ms` serviced the bus
  but not the protocol: the endpoint filled with the first frame and the tool's
  next write blocked.

Each looked like a different subsystem — descriptors, a driver, the protocol —
and each was the same thing. USB is not a stream that waits.

## On-hardware checklist

Everything in the peripheral rows of the status table is written against the
datasheet and checked arithmetically on the host, and none of it has run on a
Pico yet. This is the pass that changes those rows to **runs on chip**.

### Wiring (one Pico, five jumpers)

| Jumper | Exercises |
|---|---|
| GP15 —[**1 kΩ**]— GP14 | PWM out on GP15 into Signal capture and pulse feedback on GP14. The resistor matters: pulse feedback *drives* GP14 for its trigger while GP15 is driving too |
| GP19 — GP16 | SPI0 loopback, MOSI into MISO |
| GP8 — GP9 | UART1 loopback, TX into RX |
| GP26 — 3V3 *or* GND (or a pot wiper) | ADC0; leave it open if you like, it is report-only |
| GP4/GP5 to a sensor (optional) | I2C0: a BME280/BMP280 (0x76/0x77, chip id 0x60/0x58), an MPU-6050 (0x68, WHO_AM_I 0x68), or anything on a breakout with pull-ups |

The LED (GP25) and the temperature sensor need no wiring.

### Run it

```bash
rustnet firmware flash --board pico --device serial:COMn     # or the manual steps above

cd demo/PeripheralTour && dotnet build && cd ../../../..
rustnet=dotnet/tools/RustNet.Cli/bin/Debug/net10.0/rustnet.exe
$rustnet flash runtime/firmware-rp2040/demo/PeripheralTour/bin/Debug/net10.0/PeripheralTour.dll \
    --name tour --key keys/rustnet-signing.key --chip rp2040 --device serial:COMn
$rustnet logs -n 60 --device serial:COMn
```

The tour logs one `[ok]` / `[FAIL]` / `[skip]` line per check and finishes
with `passed N, failed M, skipped K`; then the LED pulses calmly at 1 Hz if
nothing failed, or blinks the failure count. With everything wired, expect
**29 passed** (fewer `BMx280`/`MPU-6050` lines without those sensors).

### What each check proves, and what to look at if it fails

| Check | Pass | If it fails |
|---|---|---|
| temperature sensor (ch 4) | raw 300..1500, 0..70 °C (≈ 0.70 V at room temp) | `READY` timeout → `clk_adc` not running (needs `PLL_USB` at 48 MHz); a floor reading → `TS_EN` |
| ADC0 on GP26 | ≈ 3300 mV to 3V3, ≈ 0 to GND | stuck mid-scale → pad still digital (`set_analog`) |
| ADC channel 5 is refused | throws | — |
| LED breathes on GP25 | **watch**: smooth up/down over 2 s | steady or dark → slice 4 B, `CSR.EN`, funcsel 4 |
| PWM source on GP15 | no throw; a scope on GP15 shows 1 kHz, 250 µs high | — |
| a 1 Hz PWM is refused | throws | — |
| capture GP14 | 8 widths, each 250±3 / 750±3 µs | empty → jumper, or PIO never saw an edge (`JMP_PIN`); widths ×n off → `SMx_CLKDIV` |
| pulse feedback on GP14 | 250±3 µs | 0 → line not released after the trigger (`set pindirs, 0`) or the 1 kΩ missing |
| pulse feedback with no echo | 0, after ~5 ms, no hang | a hang → a deadline wait not bounded |
| generate 3 flashes on GP25 | **watch** three 150 ms flashes; call takes 749..770 ms | much longer → generator underrun/stall detection; no flashes → funcsel 6 / `pindirs` |
| SPI0 loopback, modes 0–3 | 16/16 bytes echoed in every mode | all 0x00/0xFF → jumper or pin mux; only some modes fail → CPOL/CPHA in `SSPCR0` |
| SPI0 write + read at 8 MHz | 4 zero bytes | timeout → `SSPCR1.SSE` / reset not released |
| UART1 loopback | `RustNet!` back | empty → GP8/GP9 mux or baud; garbage → divisor |
| UART0 (console) is refused | throws | — |
| I2C0 scan, NACK is an error | completes in well under a second; found devices listed | every address "found" → stuck bus / SDA held low; a hang → abort handling |
| BMx280 / MPU-6050 id | 0x60/0x58 · 0x68 | address acked but wrong id → STOP/RESTART sequencing |
| files: write/append/read/bytes/list/delete | all ok | — |
| `/.sys` write, read, `\.sys` delete | all three throw "reserved for the firmware" | **stop**: an app can reach the signing key |
| root listing hides .sys | `.sys` absent | — |

Also worth one look with a scope or logic analyser, since the tour can only
check what it can measure itself: SCL at 100 kHz should show ~4 µs high /
6 µs low, and SPI0 at 1 MHz should clock at 992 kHz.

After the pass, `rustnet apps autostart` / `reboot` and run `rustnet info`:
the console must still answer — every Signal call blocks USB for its
duration, and none in the tour is longer than ~0.8 s.

## OTA A/B

The RP2040 executes in place and knows nothing of slots, so OTA brings its own
first stage. Flash, by offset:

| Range | What |
|---|---|
| `0x000000`–`0x004000` | second stage (256 B) + the **slot picker** (`boot/`) |
| `0x004000`–`0x080000` | **slot A** — the firmware linked at `0x10004000` |
| `0x080000`–`0x0FC000` | **slot B** — the same firmware linked at `0x10080000` (`--features slot-b`) |
| `0x0FE000`–`0x100000` | the **boot-state log**, two sectors |
| `0x100000`–`0x200000` | the flashfs storage window, as before |

An image runs only at the address it was linked for, so an update carries
both builds and the device keeps the half for the slot it is not running
from:

```bash
python tools/make_ota.py                       # builds both -> target/rustnet-pico.ota
rustnet ota push target/rustnet-pico.ota --chip rp2040 \
    --key keys/rustnet-signing.key --device serial:COMn
# the board reboots into the other slot as a *trial*:
rustnet logs -n 20 --device serial:COMn        # "[ota] slot B is a trial update (boot 1 of 3)"
rustnet ota confirm --device serial:COMn       # keep it — or `ota rollback`
```

The update streams in 4 KB frames; the signature is checked over the stream
and the slot is written (erased a sector ahead, read back after) as it
arrives, so the 680 KB image is never in RAM. Only a fully received, correctly
signed update is recorded as a trial. The picker records an attempt **before**
each trial boot, so an image that faults before it can say anything still
uses one up; after three unconfirmed boots it boots the old slot, which logs
the rollback. The state is an append-only log of 16-byte records — never
rewritten in place — compacted into the other sector *records first, header
last*, so a power cut at any point leaves one valid sector in charge
(`rustnet_hal_rp2040::ota`, host-tested including the power cut).

If neither slot holds a plausible vector table the picker drops into the
ROM's USB bootloader rather than hanging, so the worst case is the usual
BOOTSEL drive.

**Seen on a Pico (2026-10-21):** `rustnet firmware flash` boots slot A;
`ota push` lands in the other slot as a trial (`boot 1 of 3`); `ota confirm`
survives a reboot; pushes alternate A↔B; and an unconfirmed update reset three
times logs `boot 2 of 3`, `boot 3 of 3`, then *not confirmed within 3 boots;
rolled back to slot A*.

Two things only the board showed. **A UF2 with gaps does not land intact**:
the first image skipped from the picker straight to slot A, the ROM accepted
it and rebooted, and the picker found no valid vector table in slot A — so
`elf2uf2.py` now emits one contiguous run. And **a reset must wait for USB**:
the reply to `OTA_END` is only queued when the firmware resets, so the
firmware keeps polling the bus for 100 ms first, or the tool reports a failed
update that in fact succeeded.

## Layout

```
runtime/rustnet-hal-rp2040/    register-level HAL, in the host workspace so its
                              arithmetic is covered by `cargo test`
runtime/firmware-rp2040/       this crate: a standalone bare-metal workspace
    build.rs                   memory.x for slot A, or slot B with `--features slot-b`
    boot/                      the slot picker: second stage + OTA slot choice (own workspace)
    src/ota.rs                 OTA over RNDP: stream, verify, arm, confirm, rollback
    src/npc.rs                 NPC: RNPC modules placed in SRAM
    src/storage.rs             the QSPI window, as an `ExtMemory` for flashfs
    src/rndp.rs                the protocol, including flash/provision/autostart
    src/main.rs                the host: every `RustNet.*` call this board binds
    demo/PeripheralTour/       C# check of every bound peripheral API, for the
                               on-hardware pass above
    tools/elf2uf2.py           ELF(s) -> UF2
    tools/make_ota.py          both slot builds -> an RNP2 update image
```

The HAL is a workspace member on purpose. Its PLL search, UART/I2C/SPI/PWM
dividers, pin maps and PIO programs are ordinary arithmetic, and arithmetic that only ever runs on a board nobody can
watch is arithmetic nobody has checked — so those parts are unit-tested on the
host, against the values the vendor SDK produces.
