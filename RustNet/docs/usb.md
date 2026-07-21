# USB (`RustNet.Usb`)

USB device (client) and host support (v0.8, chip-gated). Where the silicon
has a USB controller these bind to it; the virtual device wires the device
and host stacks together through an in-memory simulator (`rustnet-usb`), so
enumeration and bulk transfers are exercisable without hardware.

## Device (client) — CDC-ACM / PC communication

The board presents itself to a PC/host as a USB peripheral. `BeginCdc` exposes
a **CDC-ACM virtual serial port** — the standard USB-to-PC channel.

```csharp
using RustNet.Usb;
using System.Text;

UsbClient.BeginCdc(0x1234, 0x5678, "RustNetSerial");   // vendor, product, name
byte[] fromPc = UsbClient.Read();                       // bytes the PC sent
UsbClient.Write(Encoding.UTF8.GetBytes("ready\n"));     // bytes for the PC
```

| Member | Effect |
|---|---|
| `BeginCdc(vid, pid, product)` | present a CDC-ACM device |
| `Read()` | bytes the host/PC has sent to the device |
| `Write(data)` | queue bytes for the host/PC to read |

## Host — enumerate + bulk transfer

The board acts as a USB host, enumerating an attached device (matching a
CDC/HID/MSC class driver) and moving bulk data.

```csharp
string info = UsbHost.Enumerate();          // "1234:5678:cdc:RustNetSerial" or ""
UsbHost.BulkOut(Encoding.UTF8.GetBytes("PING"));
byte[] reply = UsbHost.BulkIn();
```

| Member | Effect |
|---|---|
| `Enumerate()` | `"vid:pid:class:product"` (hex ids, class cdc/hid/msc/vendor) or empty |
| `BulkOut(data)` | bulk-OUT transfer to the attached device |
| `BulkIn()` | bulk-IN transfer from the attached device |

## Architecture

`rustnet-usb` holds the device classes (CDC-ACM, HID keyboard, mass storage),
the host class drivers and a `SimBus` linking them. The firmware keeps a
device stack, a host stack (CDC/HID/MSC drivers registered) and the bus in
`SharedState`; intrinsics `RustNet.Usb.UsbClient::BeginCdc/Read/Write` and
`RustNet.Usb.UsbHost::Enumerate/BulkOut/BulkIn` bridge managed calls onto it.
Real controllers plug their own device/host stack into the same seam.

## Verified path

The `rustnet-usb` crate unit-tests the CDC round-trip through the sim bus
(enumerate → bulk-out → bulk-in). On-device, one app plays both roles: it
presents a CDC device, the host enumerates it
(`1234:5678:cdc:RustNetSerial`), sends `PING` (device reads it) and reads back
`PONG` the device queued — proving USB client, host, and PC-serial
communication over the simulator.
