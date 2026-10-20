using RustNet.Community.Bus;
using RustNet.Sensors.Gnss;

namespace RustNet.Community.Ublox;

/// <summary>A UBX-NAV-PVT solution (u-blox protocol ≥ 15, i.e. M8 and later).</summary>
public class UbxPvt
{
    /// <summary>GPS time of week of the epoch, ms.</summary>
    public long ITowMs;

    public int Year, Month, Day, Hour, Minute, Second;

    /// <summary>Fraction of second, ns (may be negative).</summary>
    public int Nanoseconds;

    /// <summary>valid flags: bit 0 date, bit 1 time, bit 2 fully resolved.</summary>
    public int ValidFlags;

    /// <summary>0 none, 1 dead reckoning, 2 = 2D, 3 = 3D, 4 GNSS+DR, 5 time only.</summary>
    public int FixType;

    /// <summary>flags bit 0: gnssFixOK (within DOP/accuracy masks).</summary>
    public bool FixOk;

    public int Satellites;

    /// <summary>Degrees (1e-7 resolution on the wire).</summary>
    public double Longitude, Latitude;

    /// <summary>Height above the ellipsoid / mean sea level, m.</summary>
    public double HeightEllipsoidMeters, HeightMslMeters;

    /// <summary>Horizontal / vertical accuracy estimates, m.</summary>
    public double HorizontalAccuracyMeters, VerticalAccuracyMeters;

    /// <summary>NED velocity, m/s.</summary>
    public double VelocityNorth, VelocityEast, VelocityDown;

    /// <summary>2D ground speed, m/s.</summary>
    public double GroundSpeed;

    /// <summary>Heading of motion, degrees (1e-5 resolution on the wire).</summary>
    public double HeadingDegrees;

    public double Pdop;
}

/// <summary>
/// u-blox NEO-M8 (M8N/M8Q/M8T …, and other UBX receivers) on a UART.
/// Builds UBX frames (0xB5 0x62, class, id, little-endian length, payload,
/// 8-bit Fletcher checksum CK_A/CK_B over class..payload), decodes the UBX
/// replies it cares about (ACK-ACK/NAK, NAV-PVT), and passes NMEA text
/// through to an <see cref="NmeaParser"/>, so both protocols can share the
/// port.
/// </summary>
public class UbloxGnss
{
    public const int ClassNav = 0x01;
    public const int ClassAck = 0x05;
    public const int ClassCfg = 0x06;
    public const int ClassNmea = 0xF0;

    public const int IdNavPvt = 0x07;
    public const int IdAckNak = 0x00;
    public const int IdAckAck = 0x01;
    public const int IdCfgPrt = 0x00;
    public const int IdCfgMsg = 0x01;
    public const int IdCfgRate = 0x08;
    public const int IdCfgCfg = 0x09;

    // NMEA message ids (class 0xF0) for CFG-MSG.
    public const int NmeaGga = 0x00;
    public const int NmeaGll = 0x01;
    public const int NmeaGsa = 0x02;
    public const int NmeaGsv = 0x03;
    public const int NmeaRmc = 0x04;
    public const int NmeaVtg = 0x05;

    private readonly SerialDevice _serial;

    // UBX receive state.
    private int _state;
    private int _class;
    private int _id;
    private int _length;
    private int _index;
    private byte[] _payload = new byte[0];
    private int _ckA;
    private int _ckB;

    public NmeaParser Parser = new NmeaParser();

    /// <summary>The latest NAV-PVT, or null before one arrives.</summary>
    public UbxPvt? Pvt;

    /// <summary>Class/id of the last acknowledged (ACK-ACK) and rejected (ACK-NAK) messages; -1 = none.</summary>
    public int LastAckClass = -1;
    public int LastAckId = -1;
    public int LastNakClass = -1;
    public int LastNakId = -1;

    public int UbxChecksumErrors;

    /// <summary>Largest UBX payload accepted (longer frames are dropped).</summary>
    public int MaxPayload = 512;

    /// <summary>Called for every valid UBX frame: class, id, payload.</summary>
    public Action<int, int, byte[]>? UbxReceived;

    public UbloxGnss(int uartPort, int baud) : this(new SerialDevice(uartPort, baud))
    {
    }

    public UbloxGnss(SerialDevice serial)
    {
        _serial = serial;
    }

    /// <summary>Fletcher-8 over <paramref name="count"/> bytes: returns CK_A | CK_B &lt;&lt; 8.</summary>
    public static int Fletcher(byte[] data, int offset, int count)
    {
        int a = 0;
        int b = 0;
        for (int i = 0; i < count; i++)
        {
            a = (a + data[offset + i]) & 0xFF;
            b = (b + a) & 0xFF;
        }
        return a | (b << 8);
    }

    /// <summary>A complete UBX frame.</summary>
    public static byte[] Frame(int cls, int id, byte[] payload)
    {
        byte[] f = new byte[payload.Length + 8];
        f[0] = 0xB5;
        f[1] = 0x62;
        f[2] = (byte)cls;
        f[3] = (byte)id;
        f[4] = (byte)payload.Length;
        f[5] = (byte)(payload.Length >> 8);
        for (int i = 0; i < payload.Length; i++)
        {
            f[6 + i] = payload[i];
        }
        int ck = Fletcher(f, 2, payload.Length + 4);
        f[payload.Length + 6] = (byte)ck;
        f[payload.Length + 7] = (byte)(ck >> 8);
        return f;
    }

    public void Send(int cls, int id, byte[] payload) => _serial.Write(Frame(cls, id, payload));

    /// <summary>Poll a message (send it with an empty payload), e.g. NAV-PVT.</summary>
    public void Poll(int cls, int id) => Send(cls, id, new byte[0]);

    private static void Put16(byte[] b, int at, int v)
    {
        b[at] = (byte)v;
        b[at + 1] = (byte)(v >> 8);
    }

    private static void Put32(byte[] b, int at, long v)
    {
        b[at] = (byte)v;
        b[at + 1] = (byte)(v >> 8);
        b[at + 2] = (byte)(v >> 16);
        b[at + 3] = (byte)(v >> 24);
    }

    /// <summary>
    /// CFG-RATE: measurement period in ms (e.g. 1000 = 1 Hz, 100 = 10 Hz),
    /// one navigation solution per measurement, aligned to GPS time.
    /// </summary>
    public void SetMeasurementRate(int periodMs)
    {
        byte[] p = new byte[6];
        Put16(p, 0, periodMs);
        Put16(p, 2, 1);
        Put16(p, 4, 1);
        Send(ClassCfg, IdCfgRate, p);
    }

    /// <summary>CFG-MSG: output <paramref name="cls"/>/<paramref name="id"/> every <paramref name="rate"/> solutions on the current port (0 = off).</summary>
    public void SetMessageRate(int cls, int id, int rate)
    {
        byte[] p = new byte[3];
        p[0] = (byte)cls;
        p[1] = (byte)id;
        p[2] = (byte)rate;
        Send(ClassCfg, IdCfgMsg, p);
    }

    /// <summary>
    /// CFG-PRT for UART1: 8N1 at <paramref name="baud"/>, with the in/out
    /// protocol masks (bit 0 UBX, bit 1 NMEA). Reconfigure the local UART
    /// afterwards; the receiver acknowledges at the old rate.
    /// </summary>
    public void ConfigureUart1(long baud, int inProtocols, int outProtocols)
    {
        byte[] p = new byte[20];
        p[0] = 1; // portID UART1
        Put32(p, 4, 0x000008D0); // mode: 8 bits, no parity, 1 stop
        Put32(p, 8, baud);
        Put16(p, 12, inProtocols);
        Put16(p, 14, outProtocols);
        Send(ClassCfg, IdCfgPrt, p);
    }

    /// <summary>CFG-CFG: save the current configuration to battery-backed RAM, flash and EEPROM.</summary>
    public void SaveConfiguration()
    {
        byte[] p = new byte[13];
        Put32(p, 4, 0x0000FFFF); // saveMask: all sections
        p[12] = 0x17; // devBBR | devFlash | devEEPROM | devSpiFlash
        Send(ClassCfg, IdCfgCfg, p);
    }

    /// <summary>Drain the UART; returns the number of bytes consumed.</summary>
    public int PollSerial()
    {
        int total = 0;
        for (int i = 0; i < 64; i++)
        {
            byte[] chunk = _serial.Read(256);
            if (chunk.Length == 0)
            {
                break;
            }
            Feed(chunk);
            total += chunk.Length;
        }
        return total;
    }

    /// <summary>Feed received bytes: UBX frames are decoded here, everything else goes to the NMEA parser.</summary>
    public void Feed(byte[] data)
    {
        byte[] one = new byte[1];
        for (int i = 0; i < data.Length; i++)
        {
            int c = data[i];
            if (_state == 0)
            {
                if (c == 0xB5)
                {
                    _state = 1;
                }
                else
                {
                    one[0] = (byte)c;
                    Parser.Feed(one);
                }
                continue;
            }
            FeedUbx(c);
        }
    }

    private void FeedUbx(int c)
    {
        if (_state == 1)
        {
            _state = c == 0x62 ? 2 : 0;
            return;
        }
        if (_state < 7)
        {
            _ckA = (_ckA + c) & 0xFF;
            _ckB = (_ckB + _ckA) & 0xFF;
        }
        if (_state == 2)
        {
            _class = c;
            _ckA = c;
            _ckB = c;
            _state = 3;
        }
        else if (_state == 3)
        {
            _id = c;
            _state = 4;
        }
        else if (_state == 4)
        {
            _length = c;
            _state = 5;
        }
        else if (_state == 5)
        {
            _length |= c << 8;
            if (_length > MaxPayload)
            {
                _state = 0;
                return;
            }
            _payload = new byte[_length];
            _index = 0;
            _state = _length == 0 ? 7 : 6;
        }
        else if (_state == 6)
        {
            _payload[_index++] = (byte)c;
            if (_index == _length)
            {
                _state = 7;
            }
        }
        else if (_state == 7)
        {
            if (c != _ckA)
            {
                UbxChecksumErrors++;
                _state = 0;
                return;
            }
            _state = 8;
        }
        else
        {
            _state = 0;
            if (c != _ckB)
            {
                UbxChecksumErrors++;
                return;
            }
            Dispatch();
        }
    }

    private void Dispatch()
    {
        if (_class == ClassAck && _payload.Length >= 2)
        {
            if (_id == IdAckAck)
            {
                LastAckClass = _payload[0];
                LastAckId = _payload[1];
            }
            else if (_id == IdAckNak)
            {
                LastNakClass = _payload[0];
                LastNakId = _payload[1];
            }
        }
        else if (_class == ClassNav && _id == IdNavPvt && _payload.Length >= 92)
        {
            ParsePvt(_payload);
        }
        UbxReceived?.Invoke(_class, _id, _payload);
    }

    private static long U32(byte[] p, int i) => (long)(p[i] | (p[i + 1] << 8) | (p[i + 2] << 16)) | ((long)p[i + 3] << 24);

    private static int I32(byte[] p, int i) => p[i] | (p[i + 1] << 8) | (p[i + 2] << 16) | (p[i + 3] << 24);

    /// <summary>Decode a 92-byte NAV-PVT payload into <see cref="Pvt"/> and mirror it into <see cref="NmeaParser.Fix"/>.</summary>
    public void ParsePvt(byte[] p)
    {
        UbxPvt v = new UbxPvt();
        v.ITowMs = U32(p, 0);
        v.Year = p[4] | (p[5] << 8);
        v.Month = p[6];
        v.Day = p[7];
        v.Hour = p[8];
        v.Minute = p[9];
        v.Second = p[10];
        v.ValidFlags = p[11];
        v.Nanoseconds = I32(p, 16);
        v.FixType = p[20];
        v.FixOk = (p[21] & 0x01) != 0;
        v.Satellites = p[23];
        v.Longitude = I32(p, 24) * 1e-7;
        v.Latitude = I32(p, 28) * 1e-7;
        v.HeightEllipsoidMeters = I32(p, 32) / 1000.0;
        v.HeightMslMeters = I32(p, 36) / 1000.0;
        v.HorizontalAccuracyMeters = U32(p, 40) / 1000.0;
        v.VerticalAccuracyMeters = U32(p, 44) / 1000.0;
        v.VelocityNorth = I32(p, 48) / 1000.0;
        v.VelocityEast = I32(p, 52) / 1000.0;
        v.VelocityDown = I32(p, 56) / 1000.0;
        v.GroundSpeed = I32(p, 60) / 1000.0;
        v.HeadingDegrees = I32(p, 64) * 1e-5;
        v.Pdop = (p[76] | (p[77] << 8)) * 0.01;
        Pvt = v;

        GnssFix f = Parser.Fix;
        f.Valid = v.FixOk && v.FixType >= 2 && v.FixType <= 4;
        f.FixMode = v.FixType == 2 ? 2 : v.FixType == 3 || v.FixType == 4 ? 3 : 1;
        f.Latitude = v.Latitude;
        f.Longitude = v.Longitude;
        f.AltitudeMeters = v.HeightMslMeters;
        f.GeoidSeparation = v.HeightEllipsoidMeters - v.HeightMslMeters;
        f.SatellitesUsed = v.Satellites;
        f.Pdop = v.Pdop;
        f.SpeedKmh = v.GroundSpeed * 3.6;
        f.SpeedKnots = f.SpeedKmh / 1.852;
        f.CourseDegrees = v.HeadingDegrees;
        if ((v.ValidFlags & 0x01) != 0)
        {
            f.Year = v.Year;
            f.Month = v.Month;
            f.Day = v.Day;
        }
        if ((v.ValidFlags & 0x02) != 0)
        {
            f.Hour = v.Hour;
            f.Minute = v.Minute;
            f.Second = v.Second;
            f.Millisecond = v.Nanoseconds > 0 ? v.Nanoseconds / 1000000 : 0;
        }
        if (f.Valid)
        {
            Parser.PositionReceived?.Invoke(f);
        }
    }
}
