using RustNet.Community.Bg95;
using RustNet.Community.Bus;
using RustNet.Community.Mt3339;
using RustNet.Community.Ublox;

namespace DriversApp;

/// <summary>A UART that answers each command line ("…\r") with a scripted reply.</summary>
public class ScriptedSerial : SimSerialDevice
{
    public List<string> Commands = new List<string>();
    public List<string> Replies = new List<string>();
    public List<string> Sent = new List<string>();
    private readonly System.Text.StringBuilder _line = new System.Text.StringBuilder();

    public void On(string command, string reply)
    {
        Commands.Add(command);
        Replies.Add(reply);
    }

    public override void Write(byte[] data)
    {
        base.Write(data);
        for (int i = 0; i < data.Length; i++)
        {
            char c = (char)data[i];
            if (c == '\r')
            {
                string cmd = _line.ToString();
                _line.Clear();
                Sent.Add(cmd);
                for (int k = 0; k < Commands.Count; k++)
                {
                    if (Commands[k] == cmd)
                    {
                        Receive(Replies[k]);
                        break;
                    }
                }
            }
            else
            {
                _line.Append(c);
            }
        }
    }
}

public static class GnssSmoke
{
    public static void RunMt3339()
    {
        SimSerialDevice uart = new SimSerialDevice();
        Mt3339 gps = new Mt3339(uart);
        // Checksums as published in MTK/Adafruit command tables.
        gps.SetUpdateRate(1000);
        Check.True("mt3339 1 Hz", Hex.Text(uart.Outbox) == "$PMTK220,1000*1F\r\n");
        uart.Outbox.Clear();
        gps.SetUpdateRate(200);
        Check.True("mt3339 5 Hz", Hex.Text(uart.Outbox) == "$PMTK220,200*2C\r\n");
        uart.Outbox.Clear();
        gps.OutputRmcGga();
        Check.True("mt3339 rmc+gga", Hex.Text(uart.Outbox) == "$PMTK314,0,1,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0*28\r\n");
        uart.Outbox.Clear();
        gps.SetBaudRate(115200);
        Check.True("mt3339 baud", Hex.Text(uart.Outbox) == "$PMTK251,115200*1F\r\n");
        uart.Outbox.Clear();
        gps.HotStart();
        Check.True("mt3339 hot start", Hex.Text(uart.Outbox) == "$PMTK101*32\r\n");
        Check.True("mt3339 sentence helper", Mt3339.Sentence("PMTK605") == "$PMTK605*31\r\n");

        // NMEA 0183 reference sentences (GGA/RMC examples) plus an ack and a release reply.
        uart.Receive("$PMTK001,220,3*30\r\n");
        uart.Receive("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\r\n");
        uart.Receive("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A\r\n");
        uart.Receive("$PMTK705,AXN_2.31_3339_13101700,5632,PA6H,1.0*6B\r\n");
        gps.Poll();
        Check.True("mt3339 ack 220", gps.Acknowledged(220));
        Check.True("mt3339 release", gps.Release == "AXN_2.31_3339_13101700");
        Check.True("mt3339 fix valid", gps.Parser.Fix.Valid);
        Check.Near("mt3339 lat", 48.1173, gps.Parser.Fix.Latitude, 1e-9);
        Check.Near("mt3339 lon", 11.516666666666667, gps.Parser.Fix.Longitude, 1e-9);
        Check.Near("mt3339 alt", 545.4, gps.Parser.Fix.AltitudeMeters, 1e-9);
        Check.Equal("mt3339 year", 1994, gps.Parser.Fix.Year);
        Check.Equal("mt3339 sats", 8, gps.Parser.Fix.SatellitesUsed);
        uart.Receive("$PMTK001,314,1*36\r\n"); // bad checksum (the real one for flag 1 is 34) -> dropped
        gps.Poll();
        Check.True("mt3339 corrupt ack ignored", gps.LastAckCommand == 220);
    }

    public static void RunUblox()
    {
        SimSerialDevice uart = new SimSerialDevice();
        UbloxGnss g = new UbloxGnss(uart);
        // Widely published reference frames.
        g.SetMeasurementRate(1000);
        Check.True("ublox cfg-rate 1 Hz", Hex.Same(Hex.Bytes("B5 62 06 08 06 00 E8 03 01 00 01 00 01 39"), Hex.FromList(uart.Outbox)));
        uart.Outbox.Clear();
        g.SetMeasurementRate(100);
        Check.True("ublox cfg-rate 10 Hz", Hex.Same(Hex.Bytes("B5 62 06 08 06 00 64 00 01 00 01 00 7A 12"), Hex.FromList(uart.Outbox)));
        uart.Outbox.Clear();
        g.SetMessageRate(UbloxGnss.ClassNmea, UbloxGnss.NmeaGsv, 0);
        Check.True("ublox cfg-msg gsv off", Hex.Same(Hex.Bytes("B5 62 06 01 03 00 F0 03 00 FD 15"), Hex.FromList(uart.Outbox)));
        uart.Outbox.Clear();
        // CFG-PRT UART1 115200 8N1, in UBX+NMEA, out UBX (frame computed independently in Python).
        g.ConfigureUart1(115200, 3, 1);
        Check.True("ublox cfg-prt", Hex.Same(Hex.Bytes("B5620600140001000000D008000000C201000300010000000000BA52"), Hex.FromList(uart.Outbox)));

        // A mixed stream: NMEA text, ACK-ACK for CFG-RATE, NAV-PVT, ACK-NAK for CFG-MSG.
        uart.Receive("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\r\n");
        AddHex(uart, "B562050102000608163F");
        AddHex(uart, "B56201075C0000CA5B07E807030F0C223837190000002EFBFFFF0301E009CBB5A63F32AE48FC50C30000BCB10000DC050000C4090000E80300000CFEFFFF14000000DC0500004054890000000000000000007D000000000000000000000000000000949C");
        AddHex(uart, "B5620500020006010E33");
        AddHex(uart, "B562050102000608163E"); // corrupt CK_B
        g.PollSerial();
        Check.Equal("ublox nmea passthrough", 1, g.Parser.SentencesParsed);
        Check.Equal("ublox ack class", 0x06, g.LastAckClass);
        Check.Equal("ublox ack id", 0x08, g.LastAckId);
        Check.Equal("ublox nak id", 0x01, g.LastNakId);
        Check.Equal("ublox checksum errors", 1, g.UbxChecksumErrors);
        UbxPvt? p = g.Pvt;
        Check.True("ublox pvt", p != null);
        if (p != null)
        {
            Check.Equal("ublox year", 2024, p.Year);
            Check.Equal("ublox second", 56, p.Second);
            Check.Equal("ublox fix type", 3, p.FixType);
            Check.Equal("ublox sats", 9, p.Satellites);
            Check.Near("ublox lon", 106.7890123, p.Longitude, 1e-9);
            Check.Near("ublox lat", -6.2345678, p.Latitude, 1e-9);
            Check.Near("ublox msl", 45.5, p.HeightMslMeters, 1e-9);
            Check.Near("ublox speed", 1.5, p.GroundSpeed, 1e-9);
            Check.Near("ublox heading", 90.0, p.HeadingDegrees, 1e-9);
            Check.Near("ublox pdop", 1.25, p.Pdop, 1e-9);
            Check.Near("ublox vel down", 0.02, p.VelocityDown, 1e-12);
        }
        Check.True("ublox fix mirrored", g.Parser.Fix.Valid && g.Parser.Fix.FixMode == 3);
        Check.Near("ublox fix lat", -6.2345678, g.Parser.Fix.Latitude, 1e-9);
        Check.Near("ublox fix kmh", 5.4, g.Parser.Fix.SpeedKmh, 1e-9);
        Check.Equal("ublox fix day", 15, g.Parser.Fix.Day);
    }

    private static void AddHex(SimSerialDevice u, string hex)
    {
        byte[] b = Hex.Bytes(hex);
        for (int i = 0; i < b.Length; i++)
        {
            u.Inbox.Add(b[i]);
        }
    }

    public static void RunBg95()
    {
        ScriptedSerial uart = new ScriptedSerial();
        uart.On("AT+QGPS=1", "\r\n+CME ERROR: 504\r\n");
        uart.On("AT+QGPSLOC=2", "\r\n+CME ERROR: 516\r\n");
        uart.On("AT+QGPSCFG=\"nmeasrc\",1", "\r\nOK\r\n");
        uart.On("AT+QGPSGNMEA=\"GGA\"", "\r\n+QGPSGNMEA: $GPGGA,061951.000,3150.7223,N,11711.9293,E,1,09,0.7,62.2,M,0.0,M,,*5B\r\n\r\nOK\r\n");
        Bg95Gnss g = new Bg95Gnss(uart);
        g.ResponsePolls = 3;
        Check.True("bg95 power on (504 = running)", g.PowerOn());
        Check.True("bg95 no fix yet", !g.ReadLocation());
        Check.Equal("bg95 cme 516", 516, g.LastError);
        // Quectel GNSS application note example, mode 2 (decimal degrees).
        uart.Commands[1] = "unused";
        uart.On("AT+QGPSLOC=2", "\r\n+QGPSLOC: 061951.000,31.84537,117.19882,0.7,62.2,2,0.00,0.0,0.0,110513,09\r\n\r\nOK\r\n");
        Check.True("bg95 location", g.ReadLocation());
        Check.Near("bg95 lat", 31.84537, g.Parser.Fix.Latitude, 1e-9);
        Check.Near("bg95 lon", 117.19882, g.Parser.Fix.Longitude, 1e-9);
        Check.Near("bg95 hdop", 0.7, g.Parser.Fix.Hdop, 1e-9);
        Check.Near("bg95 alt", 62.2, g.Parser.Fix.AltitudeMeters, 1e-9);
        Check.Equal("bg95 fix 2d", 2, g.Parser.Fix.FixMode);
        Check.Equal("bg95 year", 2013, g.Parser.Fix.Year);
        Check.Equal("bg95 month", 5, g.Parser.Fix.Month);
        Check.Equal("bg95 sats", 9, g.Parser.Fix.SatellitesUsed);
        Check.Equal("bg95 hour", 6, g.Parser.Fix.Hour);
        // Same example in mode 0 (ddmm.mmmm + hemisphere).
        Check.True("bg95 mode 0", g.ParseLocation("061951.000,3150.7223N,11711.9293E,0.7,62.2,2,90.30,0.0,0.0,110513,09"));
        Check.Near("bg95 lat mode 0", 31.845371666666665, g.Parser.Fix.Latitude, 1e-9);
        Check.Near("bg95 lon mode 0", 117.19882166666666, g.Parser.Fix.Longitude, 1e-9);
        Check.Near("bg95 cog ddd.mm", 90.5, g.Parser.Fix.CourseDegrees, 1e-9);
        // NMEA through the AT port.
        Check.True("bg95 nmea source", g.EnableNmeaSource());
        string? gga = g.ReadNmea("GGA");
        Check.True("bg95 gga", gga != null && g.Parser.SentencesParsed == 1);
        Check.Equal("bg95 gga quality", 1, g.Parser.Fix.Quality);
        Check.True("bg95 command sent", uart.Sent[0] == "AT+QGPS=1");
        uart.On("AT+QGPSEND", "\r\nOK\r\n");
        Check.True("bg95 power off", g.PowerOff());
    }
}
