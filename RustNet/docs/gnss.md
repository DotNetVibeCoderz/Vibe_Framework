# GPS / GNSS

`RustNet.Sensors.Gnss` decodes NMEA-0183 from any GPS/GNSS module.

```csharp
using RustNet.Sensors.Gnss;

var gps = new GnssReceiver(uartPort: 1, baud: 9600);
gps.Parser.PositionReceived = fix =>
    Console.WriteLine($"{fix.Latitude} {fix.Longitude} alt={fix.AltitudeMeters} sats={fix.SatellitesUsed}");
gps.Parser.SatellitesInView = sats => Console.WriteLine($"{sats.Count} in view");

while (true)
{
    gps.Poll();          // drains the UART into the parser
    Sleep.Ms(250);
}
```

`NmeaParser` takes bytes or text in any chunking (`Feed`), verifies checksums
(failures are counted in `ChecksumErrors` and dropped), and understands GGA,
RMC, GSA, GSV (multi-sentence groups, per constellation), VTG and GLL from any
talker (GP, GN, GL, GA, GB/BD, GQ). The latest state is in `Fix`:
position, altitude, geoid separation, UTC time and date, speed (knots and
km/h), course, fix quality and 2D/3D mode, satellites used, and
PDOP/HDOP/VDOP. Sentences it does not interpret (`$PMTK...`, `$GPTXT`) go to
`OtherSentence`. `GnssReceiver.SendCommand(body)` adds the `$`, checksum and
CRLF.

Module-specific configuration lives in driver packages built on this parser:
`rustnet-driver-mt3339` (PMTK), `rustnet-driver-ublox` (UBX for NEO-M8) and
`rustnet-driver-bg95` (Quectel AT commands) — see [drivers.md](drivers.md).
