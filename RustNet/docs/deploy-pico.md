# Deploying a C# App to a Raspberry Pi Pico

End-to-end procedure for running a .NET application on a Raspberry Pi Pico
(RP2040, dual Cortex-M0+) — over the board's **own USB socket**, with no
serial adapter, no debug probe and no vendor tool.

There are **two phases**, and they cost very different amounts:

| | How | When |
|---|---|---|
| **A. Flash the firmware** | a UF2 copied onto the board | Once, and again whenever the firmware changes |
| **B. Replace the application** | `rustnet flash` over the same USB port | Every time you change your C# code |

Phase B is the one you do repeatedly, and on this board it is unusually cheap:
the Pico appears as an ordinary COM port, `rustnet flash` replaces the running
application **without a reboot**, and an uploaded app starts again by itself
after a power cycle along with anything it wrote to the filesystem.

Phase A is nearly as cheap, because **this is the only board here that can put
itself into its bootloader**. Once RustNet firmware is on it, nothing has to
touch the BOOTSEL button again — not for the next firmware, not for the one
after that.

Indonesian translation: [`deploy-pico.id.md`](deploy-pico.id.md).

## The board

| | |
|---|---|
| SoC | RP2040 — dual Cortex-M0+ at **125 MHz** |
| RAM | 264 KB SRAM; 128 KB of it is the interpreter heap |
| Flash | 2 MB QSPI NOR, external — the chip has none of its own |
| Console | USB CDC from the SoC itself (`2E8A:000A`) |
| Programming | the ROM's UF2 bootloader, over the same socket |
| User LED | GP25 |

**The RP2040 executes in place out of that external flash.** There is no
internal program memory, so the image, the filesystem and the application all
live in the one QSPI part — which is why programming it needs the care
described under [Storage](#how-the-flash-is-divided) below.

### How the flash is divided

| Range | What |
|---|---|
| `0x000000`… | second-stage bootloader + firmware image (~290 KB) |
| `0x100000`–`0x200000` | filesystem (`rustnet-flashfs`), 1 MB |

The window starts at 1 MB rather than immediately above the image. The image
grows; starting it there leaves room to roughly triple without moving, and
moving it would invalidate everything already stored above.

Inside the filesystem, the firmware keeps four names for itself under
`/.sys/` — the signing key, the application, its name, and the autostart
marker. Everything else in there is yours.

# Phase A — flash the firmware

## A1. One-time: tools

```bash
rustup target add thumbv6m-none-eabi
```

That is the whole list. The UF2 packer is a Python script in the repository
(`runtime/firmware-rp2040/tools/elf2uf2.py`), so there is nothing to
`cargo install` and no vendor SDK to download.

## A2. Build and flash

The easiest way is to let the tool do all of it:

```bash
rustnet firmware build --board pico
rustnet firmware flash --board pico --device serial:COM12
```

`--device` is what makes this hands-free: the running firmware is asked over
RNDP to reboot into its ROM bootloader, the board comes back as a removable
drive labelled `RPI-RP2`, and the UF2 is copied onto it. The board reboots
into the new firmware on its own.

The same thing is a panel in the Workbench — **FIRMWARE ▸ BOARD FIRMWARE**,
pick `pico`, press BUILD + FLASH.

### The first time only

A board that is not yet running RustNet firmware cannot be asked into its
bootloader, so the first flash is manual, exactly once:

1. Unplug the Pico.
2. Hold **BOOTSEL** and plug it back in.
3. `rustnet firmware flash --board pico` — with no `--device`. The tool waits
   for the `RPI-RP2` drive and copies onto it.

The same applies after flashing any *other* firmware onto the board.

### By hand, if you prefer

```bash
cd runtime/firmware-rp2040
cargo build --release
python tools/elf2uf2.py \
    target/thumbv6m-none-eabi/release/rustnet-firmware-rp2040 rustnet-pico.uf2
# then copy rustnet-pico.uf2 onto the RPI-RP2 drive
```

## A3. Confirm it is alive

The board enumerates as a COM port a few seconds after the copy:

```bash
rustnet info --device serial:COM12
{"chip":"rp2040","board":"Raspberry Pi Pico","protocol":1,"heap_used":10176,
 "active_app":"blink (embedded)","running":true,"transport":"usb-cdc",
 "cpu_hz":125000000}

rustnet logs -n 5 --device serial:COM12
RustNet on Raspberry Pi Pico @ 125 MHz (peri 125 MHz), heap 128 KB
app: 67 methods, 19 types, 91 strings
[C#] blinking the user LED
```

The LED blinks twice, pauses, and repeats — that pattern is the compiled-in
C# demo driving GP25 through the HAL, not a native loop.

> The COM number is not stable across machines or ports. Enumerate rather than
> assuming: `rustnet info --device serial:COMn` for the one that answers, or
> look for `USB Serial Device` in Device Manager.

# Phase B — your application

## B1. One-time: provision a signing key

```bash
rustnet keys generate --out keys
rustnet provision --key keys/rustnet-signing.pub --device serial:COM12
```

**A device accepts one key, once.** The second attempt is refused with
`already provisioned`. That is deliberate: a device whose key can be replaced
accepts anything its new owner signs. Recovering from a lost private key means
erasing the storage window from the bootloader — which needs physical access,
and that is the point.

Keep `keys/rustnet-signing.key` private; `keys/` is gitignored.

## B2. Write the app

```bash
rustnet new blinky --template blink
cd blinky
dotnet build
```

The interpreter here is the same one every other port runs, so the language
surface is the same — see [`dotnet-support.md`](dotnet-support.md). What is
particular to this board is size: 128 KB of heap and a 2 MB flash part, so it
suits control and sensor work rather than anything with a framebuffer.

## B3. Flash it

```bash
rustnet flash bin/Debug/net10.0/blinky.dll --name blinky \
    --key ../keys/rustnet-signing.key --chip rp2040 --device serial:COM12
```

`--chip rp2040` matters: the container records the chip family and **a device
refuses an image built for another chip**. Signing for `esp32` and flashing
here fails the check on the board, not in the tool.

The new application replaces the running one immediately, without a reboot:

```
rustnet logs -n 10 --device serial:COM12
[app] switching to blinky
app: 67 methods, 19 types, 91 strings
[C#] blinking the user LED
```

## B4. Make it survive a power cycle

```bash
rustnet apps autostart blinky --device serial:COM12
```

After that the log opens with the application already loaded from flash:

```
RustNet on Raspberry Pi Pico @ 125 MHz (peri 125 MHz), heap 128 KB
[sec] provisioned
[app] blinky from flash (12763 bytes)
```

Without the autostart marker a flashed app is *kept* but not started — start
it with `rustnet apps start blinky`, stop it with `apps stop`. A device whose
application has stopped is still fully reachable, which is what makes a bad
app replaceable over the wire rather than needing a reflash.

## B5. Files

```bash
rustnet data push settings.json /cfg/settings.json --device serial:COM12
rustnet data pull /cfg/settings.json out.json --device serial:COM12
```

These survive a reboot **and a firmware reflash** — the image lives below the
storage window, so replacing it leaves the filesystem untouched.

# What can go wrong

**The port opens and every command times out.** Almost always a board in
BOOTSEL rather than running firmware: check for an `RPI-RP2` drive. A Pico in
its bootloader is a mass-storage device, not a COM port, so if you see *both*
you have two boards.

**`error: already provisioned`.** Expected — see B1. Use the private key you
provisioned with, or erase storage from the bootloader and start over.

**`error: signature check failed`.** The app was signed with a different key,
or for a different chip. `--chip rp2040`, and the `.key` matching the `.pub`
on the device.

**The UF2 copy "fails" at the very end.** That is what success looks like: the
bootloader reboots into the new image the moment the last block lands, so the
drive disappears mid-write. The tool treats it as done.

**Windows says "the semaphore timeout period has expired" when opening the
port.** The firmware stopped servicing USB — historically because something on
its boot path blocked. If a build you made does this, look for a wait that
does not poll; see *Every wait has to serve the bus* in
[`runtime/firmware-rp2040/README.md`](../runtime/firmware-rp2040/README.md).

# What this port does not have

No WiFi, no display, no MQTT — the RP2040 has no radio, and this board has no
panel. The device surface that needs neither (GPIO, timing, files, the whole
language core) is all present.

For the full matrix see [`chips.md`](chips.md) and
[`dotnet-support.md`](dotnet-support.md).
