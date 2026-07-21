# System services: power, RTC, watchdog, external memory, device info, signals

## Power management (`RustNet.Sys.Power`)

```csharp
Power.ArmWakeGpio(pin: 0, rising: true);   // wake on GPIO edge
Power.ArmWakeRtc(seconds: 300);            // wake via RTC alarm
Power.Sleep(Power.Deep, durationMs: 0);    // Light / Deep / Hibernate
Console.WriteLine(Power.WakeReason());     // "power-on" | "rtc-alarm" | "gpio" | ...
Power.ClearWakeSources();
Power.Reset();                             // reboot (virtual device: halts the app)
Power.Shutdown();                          // power off; armed wake sources apply
int mv = Power.BatteryMillivolts();
```

Wake sources accumulate until cleared and apply to the next
sleep/shutdown. On the virtual device `Reset`/`Shutdown` stop the app and
log — real chips reboot/power off in the chip's `PowerManager`.

## RTC (`RustNet.Sys.Rtc`)

Battery-backed calendar clock, epoch = seconds since 1970 UTC.

```csharp
Rtc.Set(1786190400);                       // set from network time
long now = Rtc.Epoch();
string s = Rtc.NowString();                // "2026-08-04 12:00:00"
Rtc.SetAlarm(now + 3600);                  // absolute epoch alarm (wake source)
Rtc.ClearAlarm();
```

## Watchdog (`RustNet.Sys.Watchdog`)

```csharp
Watchdog.Start(5000);                      // reset unless fed within 5 s
while (working) { DoChunk(); Watchdog.Feed(); }
Watchdog.Stop();                           // NotSupported on some real chips
```

## External memory (`RustNet.Sys.ExtMemory`)

Index 0 = QSPI NOR flash (erase-before-write, `SectorSize()` granularity),
index 1 = SDRAM (byte-addressable, no erase). The simulator enforces NOR
semantics (bits only clear on write) so drivers are honest.

```csharp
ExtMemory.Erase(0, address: 0, length: 4096);
ExtMemory.Write(0, 0, data);
byte[] back = ExtMemory.Read(0, 0, data.Length);
Console.WriteLine(ExtMemory.Kind(0));      // "qspi-flash" | "sdram"
```

## Device info (`RustNet.Sys.DeviceInfo`)

```csharp
DeviceInfo.Chip();       // "host-sim" | "esp32" | "esp32c3" | "k210" | ...
DeviceInfo.Board();      // board display name
DeviceInfo.Version();    // firmware version
DeviceInfo.UptimeMs();
DeviceInfo.Json();       // one JSON blob with all of the above
```

## Signal control (`RustNet.Hal.Signal`) — TinyCLR-style

Precise timed edges on a GPIO pin, all timings in microseconds:

```csharp
Signal.Generate(pin, initialHigh: true, new[] { 500, 1500, 500 }); // SignalGenerator
int[] widths = Signal.Capture(pin, maxEdges: 64, timeoutUs: 100000); // SignalCapture
int echoUs = Signal.PulseFeedback(pin, true, 10, 30000);           // PulseFeedback
```

Driver example: `RustNet.Devices.HcSr04` (ultrasonic ranging via
`PulseFeedback`). The simulator lets tests inject capture patterns and
echo widths (`signal_inject_capture`, `signal_set_echo`).
