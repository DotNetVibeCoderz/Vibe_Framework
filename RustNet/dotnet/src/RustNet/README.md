# RustNet

**Write C# for microcontrollers. Run it on a Rust runtime.**

RustNet compiles a .NET application into a compact **RNX** module, signs it,
flashes it over its own protocol, and executes it with an IL interpreter
written in Rust. This package is the managed API those applications are
written against.

```
C# app ──dotnet build──▶ DLL ──MetadataProcessor──▶ .rnx ──sign(RSA)──▶ RNSB
                                                                        │
      RNDP protocol (TCP / USB-CDC / UART)                              ▼
Tools (CLI ▪ Workbench ▪ VSCode) ◀──────────────▶ Firmware (Rust) ─▶ IL interpreter
```

## Install

```bash
dotnet add package RustNet
```

One package, sixteen assemblies — they version together and an application
that touches one usually touches several.

## Hello, blinking world

```csharp
using RustNet.Hal;
using RustNet.Threading;

Gpio.SetMode(2, PinMode.Output);
while (true)
{
    Gpio.Toggle(2);
    Sleep.Ms(500);
}
```

Then, against a board or the virtual device:

```bash
rustnet flash MyApp.dll --name blinky --key keys/rustnet-signing.key --start
rustnet logs --follow
```

## What is in the box

| Namespace | |
|---|---|
| `RustNet.Hal` | GPIO, ADC, PWM, I²C, SPI, UART |
| `RustNet.Net` | WiFi, MQTT, HTTP, Ethernet, PPP, cellular |
| `RustNet.Graphics` · `RustNet.Drawing` · `RustNet.UI` | display drawing, bitmaps, an XML-loadable element tree |
| `RustNet.IO` · `RustNet.Data` | filesystem, embedded SQL |
| `RustNet.Buses` | CAN, Modbus RTU/TCP, 1-Wire |
| `RustNet.Serialization` | JSON, XML, binary — reflection-free, runs on-device |
| `RustNet.Devices` | sensor, actuator and display drivers |
| `RustNet.Cloud` | Azure IoT Hub, AWS IoT Core, Google Cloud IoT |
| `RustNet.Media` · `RustNet.Usb` | camera capture, USB device/host (chip-gated) |
| `RustNet.Core` · `RustNet.Diagnostics` · `RustNet.Resources` | threading, logging, embedded assets |

Nearly every member is an `[InternalCall]` façade: calling one off-device
throws `RuntimeOnlyException`, because the behaviour lives in the interpreter
on the other side. The XML documentation shipped in this package is where that
behaviour is described.

## Language support

The interpreter runs the language core, not a subset you have to think about
constantly: `try`/`catch`/`finally` with `when` filters, inheritance,
interfaces and virtual dispatch, generics, `async`/`await`, delegates and
lambdas, the BCL collections, a LINQ subset, `StringBuilder`, `Regex` and
string interpolation. Reflection is partial and deliberately so.

The full matrix, and the current limits, are in
[`docs/dotnet-support.md`](https://github.com/DotNetVibeCoderz/Vibe_Framework/blob/main/RustNet/docs/dotnet-support.md).

## Boards verified on real silicon

ESP32 (WROOM-32, M5Stack Tough, M5Stack Core2) · ESP32-C3 (Seeed XIAO) ·
K210 RISC-V (Sipeed Maix Go) · STM32F4 (Nucleo-F401RE) · STM32F7 (Wilderness
Labs Meadow F7 Micro) · RP2040 (Raspberry Pi Pico) — plus a host "virtual
device" every tool and test runs against, so none of this needs hardware to
try.

## Links

- [Repository and docs](https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/RustNet)
- [Getting started](https://github.com/DotNetVibeCoderz/Vibe_Framework/blob/main/RustNet/README.md)
- [Architecture](https://github.com/DotNetVibeCoderz/Vibe_Framework/blob/main/RustNet/docs/architecture.md)

MIT licensed.
