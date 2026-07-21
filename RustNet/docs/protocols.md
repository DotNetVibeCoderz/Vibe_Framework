# Field-bus protocols: CAN, Modbus, 1-Wire

All three buses follow the same architecture: timing/framing-critical code
lives in the Rust runtime (`rustnet-hal` traits + protocol layers), managed
apps call thin `[InternalCall]` wrappers, and the host simulator provides
loopback/simulated devices so everything runs on the virtual device.

## CAN (`RustNet.Buses.Can`)

Classic CAN 2.0, 11-bit and 29-bit identifiers, 0–8 data bytes.

```csharp
Can.Init(0, 500000, loopback: true);   // bus, bitrate, self-test loopback
Can.SetFilter(0, 0x100, 0x700);        // accept when (id & mask) == (0x100 & mask)
Can.Write(0, 0x123, new byte[] { 1, 2, 3 });
while (Can.Available(0) > 0)
{
    CanFrame f = Can.Read(0);          // null when FIFO empty
    Console.WriteLine($"id={f.Id} len={f.Data.Length}");
}
```

Rust side: `rustnet-hal/src/can.rs` (`CanBus` trait), simulator in
`rustnet-hal-host/src/sim_ext.rs` (loopback + filter + injectable RX).
Frames cross the managed boundary packed (`id u32 LE | flags | len | data`)
— one allocation per read, none per write.

## Modbus (`RustNet.Buses.Modbus`)

Master, RTU framing (CRC-16 poly 0xA001) with a TCP/MBAP layer available in
Rust (`rustnet-net/src/modbus.rs`). Function codes: 1–6, 15, 16. Every
master call on the virtual device round-trips real RTU frames through an
in-firmware slave (unit id 1, 10k coils + 10k registers) — CRC validated
both directions.

```csharp
Modbus.WriteRegister(1, 100, 1234);          // unit, address, value
int[] regs = Modbus.ReadHoldingRegisters(1, 100, 8);
Modbus.WriteCoil(1, 5, true);
byte[] coils = Modbus.ReadCoils(1, 5, 4);    // one byte per coil
Modbus.WriteRegisters(1, 200, new[] { 1, 2, 3 });
```

Exception responses (illegal address, ...) surface as managed exceptions
with the standard Modbus exception names. PDU encode/decode writes into
reusable buffers — no per-frame heap traffic on the request path.

## 1-Wire (`RustNet.Buses.OneWire`)

Dallas/Maxim bus master. Bit timing is the HAL's job; managed code works at
byte/ROM level. `crc8` (Dallas polynomial) lives in the Rust HAL and the
simulated DS18B20 produces CRC-valid scratchpads.

```csharp
if (OneWire.Reset(0))                    // presence pulse
{
    long[] roms = OneWire.Search(0);     // enumerate slaves
    OneWire.Select(0, roms[0]);          // MATCH ROM
    OneWire.WriteByte(0, 0x44);          // CONVERT T
}
```

Driver example: `RustNet.Devices.Ds18b20` (`Find(bus)` +
`ReadCentiCelsius()`). The virtual device ships a simulated DS18B20 at
25.5 °C on bus 0 (ROM family code 0x28 in the low byte).

Template: `rustnet new can-gateway <name>` (CAN → Modbus bridge),
`rustnet new datalogger-db <name>` (1-Wire → SQL database).
