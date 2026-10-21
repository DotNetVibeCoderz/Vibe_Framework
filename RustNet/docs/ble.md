# RNDP over Bluetooth LE

Every RustNet tool talks to a device over RNDP (`docs/protocol.md`). Over
Bluetooth LE the frames are the same; only the pipe changes. The ESP32
firmware runs a GATT server, and the .NET tools connect to it with
`--device ble:<address|name>`.

> **Status:** verified on an M5Stack Core2 from a Windows 11 host
> (2026-10-21): `ble scan`, repeated `info`, `logs`, a 7 KB app flashed over
> BLE in 5.7 s and run, and `ota confirm`. The BLE firmware went onto the
> board by OTA. Host side is Windows only; Linux (BlueZ) and macOS are not
> implemented.
>
> One Windows quirk found on the way: discovering the service *by UUID*
> failed on first contact with `0x80070016` (`ERROR_BAD_COMMAND`, and an
> empty message). The host now falls back to discovering every service and
> picking the RNDP one, and BLE errors name the step that failed.

## The GATT service

Nordic UART Service, UUIDs unchanged, so a generic BLE console (nRF Connect,
a Web Bluetooth UART page) can open the same link:

| | UUID | Properties |
|---|---|---|
| Service | `6e400001-b5a3-f393-e0a9-e50e24dcca9e` | |
| RX (host → device) | `6e400002-b5a3-f393-e0a9-e50e24dcca9e` | write, write-without-response |
| TX (device → host) | `6e400003-b5a3-f393-e0a9-e50e24dcca9e` | notify |

The device advertises as **`RustNet-XXXX`**, where `XXXX` is the last two bytes
of its Bluetooth MAC. The 128-bit service UUID goes in the advertisement and
the name goes in the scan response. The two together are more than the
31 bytes one advertisement can carry.

RNDP frames are carried across packets unchanged. A frame may span many
packets, and a packet may end partway through a frame. Each side cuts what it
sends into pieces of **ATT MTU − 3** bytes, the ATT header's share. The device
asks for an MTU of 517, and Windows usually settles on 247 or 517. On the
device, the reassembly and the chunking live in `runtime/firmware/src/ble.rs`
(host-tested). The host's equivalents are `BleTransport` and
`WinRtBleLink.Mtu` (from `GattSession.MaxPduSize`).

One host at a time: NimBLE is configured for one connection, like the serial
port. BLE requests are serialised against UART and WiFi/TCP by the same
`DeviceService` lock, so all three transports can be used together.

## Building the firmware with BLE

Bluetooth has to be switched on in ESP-IDF's own configuration, and a cargo
feature cannot do that. esp-idf-sys reads the sdkconfig files named in
`ESP_IDF_SDKCONFIG_DEFAULTS`, and a build script only ever sees its own crate's
features. So a BLE build names the extra defaults file and turns the feature
on:

```powershell
. $HOME\export-esp.ps1
cd runtime\firmware-esp32
# ABSOLUTE paths: a relative name from the shell is resolved somewhere else
# by esp-idf-sys, and then *no* defaults file is applied at all.
$env:ESP_IDF_SDKCONFIG_DEFAULTS = "$PWD\sdkconfig.defaults;$PWD\sdkconfig.defaults.ble"
$env:CARGO_TARGET_DIR = "C:/rnesp-ble"     # optional, see below
cargo build --release --features board-m5core2,ble
```

```bash
cd runtime/firmware-esp32
export ESP_IDF_SDKCONFIG_DEFAULTS="$PWD/sdkconfig.defaults;$PWD/sdkconfig.defaults.ble"
cargo build --release --features ble
```

If `--features ble` is used without the variable, `build.rs` stops the build
and prints what to set. It checks ESP-IDF's resulting configuration for
NimBLE, not the variable's text. Builds without the feature do not change at
all.

The separate `CARGO_TARGET_DIR` is optional. Without it, every switch between
BLE and non-BLE builds reconfigures and rebuilds ESP-IDF in the shared target
directory.

Bluetooth is not enabled globally because of its RAM cost. On the classic
ESP32, an enabled controller reserves `CONFIG_BTDM_RESERVE_DRAM` (0xdb5c,
56 KB) of internal DRAM at link time. The non-BLE firmware cannot spare that,
because it needs the heap headroom for WiFi and RSA image verification. See
`sdkconfig.defaults.ble` for what the BLE build sets: BLE-only controller,
one connection, NimBLE logging off (UART0 carries RNDP), and host pools that
follow the malloc policy so that PSRAM boards put them in SPI RAM.

### Image size

`espflash save-image` sizes, ESP-IDF v5.2.3, release profile:

| Build | App image | 2 MB `factory` (partitions.csv) | 3 MB OTA slot (partitions-ota-16mb.csv) |
|---|---|---|---|
| default (generic DevKit) | 1,858,576 B | 88.6 % | 59.1 % |
| `board-m5core2` | 1,862,720 B | 88.8 % | 59.2 % |
| `board-m5core2,ble` | 2,082,368 B (+219,648) | **99.3 %, 14,784 B left** | 66.2 % |

**The BLE image only just fits the 4 MB layout's 2 MB `factory` partition.**
Anything that adds more than about 14 KB to a BLE build will overflow it.
BLE builds that are expected to grow should use the 16 MB OTA table
(`partitions-ota-16mb.csv`, 3 MB slots) on boards that have the flash.
Dropping NimBLE's central and observer roles saved only 4.5 KB. The bulk of
the size is the controller, the host and WiFi/BT coexistence.

Internal RAM: the start of the heap moves up by 72,776 B. That is the
controller's 56,156 B reservation plus 16,616 B of NimBLE/coexistence
`.data`/`.bss`. The classic-BT part of the reservation goes back to the heap
when the stack starts, and NimBLE's own pools come out of the heap after that.
The net free heap has to be read on the chip: `rustnet logs` shows the
`free heap` boot lines.

### Flash

Flashing works the same as for any other build. Use the BLE build's ELF and
the same partition table as before:

```powershell
espflash flash C:/rnesp-ble/xtensa-esp32-espidf/release/rustnet-firmware-esp32 `
    --partition-table partitions.csv --port COM4
```

## Host side (Windows)

```bash
rustnet ble scan                       # RustNet-* devices with address and RSSI
rustnet ble scan --timeout 10
rustnet info --device ble:24:0A:C4:12:AB:0F
rustnet info --device ble:RustNet-AB0F     # by advertised name (scans for it)
rustnet logs -n 50 --device ble:           # empty / '*' = first RustNet device found
```

The spec after `ble:` can be any of these:

- an address, written `AA:BB:CC:DD:EE:FF`, `AA-BB-…`, or as 12 hex digits
- an advertised name (case-insensitive)
- nothing at all, or `*`, which means the first device whose name starts with
  `RustNet-`

`RUSTNET_DEVICE=ble:…` works as it does for the other transports.

The backend is `dotnet/tools/RustNet.Deploy.Ble.Windows`
(`net10.0-windows10.0.19041.0`, WinRT GATT client), and it is set up this way:

1. `RustNet.Deploy` stays plain `net10.0`. It defines the seams:
   `IBlePacketLink`, `BleTransport`, `TransportFactory.BleLinkProvider` /
   `BleScanProvider`, `BleTarget` (spec parsing) and `BlePlatform`.
2. The CLI also stays `net10.0`, so it and `RustNet.Tests`, which references
   it, still build on Linux CI. When it is built on Windows, a conditional
   `ProjectReference` (`ReferenceOutputAssembly=false`, `PrivateAssets=all`)
   builds the backend as a plugin (`EnableDynamicLoading`). An after-build
   step then copies `RustNet.Deploy.Ble.Windows.dll`,
   `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` next to
   `rustnet.exe`.
3. At startup the CLI calls `BlePlatform.TryRegister()`. On Windows 10 2004
   or later this loads the plugin by reflection and calls
   `WinRtBle.Register()`. On other systems it registers nothing, and using
   `ble:` fails with a message saying BLE is Windows-only for now.

The Windows link writes with write-without-response, which the device offers,
and subscribes to TX notifications. It keeps the connection with
`GattSession.MaintainConnection`. If a device named by address has not been
heard yet, it scans for up to 10 s first.

## Troubleshooting

- **`rustnet ble scan` finds nothing.** Check that the firmware was built
  with `--features ble` *and* the defaults variable. If it was, the boot log
  (`rustnet logs` over serial) shows `RNDP server ready (ble, advertising as
  RustNet-XXXX)`. A device that already has a host connected stops
  advertising until that host disconnects.
- **`BLE unavailable: …` in the log.** NimBLE failed to come up. The message
  says which step failed.
- **Pairing prompts.** None are expected. The link is unencrypted and
  unbonded, and RNDP's own security (signed images, an encrypted config
  store) works the same over any transport.
