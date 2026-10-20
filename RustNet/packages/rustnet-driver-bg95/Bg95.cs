using RustNet.Community.Bus;
using RustNet.Sensors.Gnss;

namespace RustNet.Community.Bg95;

/// <summary>The reply of one AT command: every line before the final result, and the result.</summary>
public class AtResponse
{
    public List<string> Lines = new List<string>();

    /// <summary>True for "OK".</summary>
    public bool Ok;

    /// <summary>The CME error code of a "+CME ERROR: n" result, -1 otherwise.</summary>
    public int CmeError = -1;

    /// <summary>False when no final result arrived in time.</summary>
    public bool Completed;

    /// <summary>The first line starting with <paramref name="prefix"/> (e.g. "+QGPSLOC:"), with the prefix removed and trimmed; null if none.</summary>
    public string? Find(string prefix)
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            string l = Lines[i];
            if (l.Length >= prefix.Length && l.Substring(0, prefix.Length) == prefix)
            {
                return l.Substring(prefix.Length).Trim();
            }
        }
        return null;
    }
}

/// <summary>
/// Quectel BG95 / BG96 / BG77 GNSS engine driven through the module's AT
/// port (Quectel "GNSS Application Note"): AT+QGPS=1 starts a session,
/// AT+QGPSLOC=2 returns the fix with decimal-degree coordinates, and with
/// AT+QGPSCFG="nmeasrc",1 the NMEA sentences can be fetched with
/// AT+QGPSGNMEA and fed to an <see cref="NmeaParser"/>. CME 516 means "not
/// fixed yet", 504 "session already running".
/// </summary>
public class Bg95Gnss
{
    public const int CmeSessionOngoing = 504;
    public const int CmeNotFixed = 516;

    private readonly SerialDevice _serial;
    private readonly System.Text.StringBuilder _rx = new System.Text.StringBuilder();

    /// <summary>NMEA decoder; also updated from AT+QGPSLOC fixes.</summary>
    public NmeaParser Parser = new NmeaParser();

    /// <summary>Read attempts (10 ms apart) before an AT command times out.</summary>
    public int ResponsePolls = 300;

    public Bg95Gnss(int uartPort, int baud) : this(new SerialDevice(uartPort, baud))
    {
    }

    public Bg95Gnss(SerialDevice serial)
    {
        _serial = serial;
    }

    /// <summary>Send "<paramref name="command"/>\r" and collect lines up to OK / ERROR / +CME ERROR.</summary>
    public AtResponse Command(string command)
    {
        _rx.Clear();
        _serial.WriteText(command + "\r");
        AtResponse r = new AtResponse();
        for (int poll = 0; poll < ResponsePolls; poll++)
        {
            byte[] b = _serial.Read(256);
            for (int i = 0; i < b.Length; i++)
            {
                char c = (char)b[i];
                if (c == '\n' || c == '\r')
                {
                    if (_rx.Length > 0)
                    {
                        string line = _rx.ToString();
                        _rx.Clear();
                        if (Final(r, line))
                        {
                            return r;
                        }
                    }
                }
                else
                {
                    _rx.Append(c);
                }
            }
            if (b.Length == 0)
            {
                Timing.SleepMs(10);
            }
        }
        return r;
    }

    private static bool Final(AtResponse r, string line)
    {
        if (line == "OK")
        {
            r.Ok = true;
            r.Completed = true;
            return true;
        }
        if (line == "ERROR")
        {
            r.Completed = true;
            return true;
        }
        string cme = "+CME ERROR:";
        if (line.Length > cme.Length && line.Substring(0, cme.Length) == cme)
        {
            r.CmeError = ParseInt(line.Substring(cme.Length).Trim());
            r.Completed = true;
            return true;
        }
        r.Lines.Add(line);
        return false;
    }

    /// <summary>Start the GNSS engine (stand-alone). True if started or already running.</summary>
    public bool PowerOn()
    {
        AtResponse r = Command("AT+QGPS=1");
        return r.Ok || r.CmeError == CmeSessionOngoing;
    }

    /// <summary>Stop the GNSS session.</summary>
    public bool PowerOff() => Command("AT+QGPSEND").Ok;

    /// <summary>Route NMEA to the AT port so <see cref="ReadNmea"/> works (AT+QGPSCFG="nmeasrc",1).</summary>
    public bool EnableNmeaSource() => Command("AT+QGPSCFG=\"nmeasrc\",1").Ok;

    /// <summary>Fetch one sentence type ("GGA", "RMC", "GSV", "GSA", "VTG") and feed it to <see cref="Parser"/>. Returns the sentence or null.</summary>
    public string? ReadNmea(string type)
    {
        AtResponse r = Command("AT+QGPSGNMEA=\"" + type + "\"");
        if (!r.Ok)
        {
            return null;
        }
        string? last = null;
        string prefix = "+QGPSGNMEA:";
        for (int i = 0; i < r.Lines.Count; i++)
        {
            string l = r.Lines[i];
            if (l.Length > prefix.Length && l.Substring(0, prefix.Length) == prefix)
            {
                last = l.Substring(prefix.Length).Trim();
                Parser.ParseSentence(last);
            }
        }
        return last;
    }

    /// <summary>
    /// AT+QGPSLOC=2 and update <see cref="NmeaParser.Fix"/>. Returns false
    /// (with <see cref="LastError"/> = 516) while there is no fix.
    /// Reply: +QGPSLOC: UTC,lat,lon,HDOP,altitude,fix,COG,spkm,spkn,date,nsat.
    /// </summary>
    public bool ReadLocation()
    {
        AtResponse r = Command("AT+QGPSLOC=2");
        LastError = r.CmeError;
        string? v = r.Find("+QGPSLOC:");
        if (!r.Ok || v == null)
        {
            return false;
        }
        return ParseLocation(v);
    }

    /// <summary>CME code of the last <see cref="ReadLocation"/> (-1 when none).</summary>
    public int LastError = -1;

    /// <summary>Parse the fields of a +QGPSLOC reply (mode 2, decimal degrees; mode 0/1 ddmm.mmmm[N/S] also accepted).</summary>
    public bool ParseLocation(string v)
    {
        string[] f = v.Split(',');
        if (f.Length < 11)
        {
            return false;
        }
        GnssFix fix = Parser.Fix;
        string t = f[0];
        if (t.Length >= 6)
        {
            fix.Hour = ParseInt(t.Substring(0, 2));
            fix.Minute = ParseInt(t.Substring(2, 2));
            fix.Second = ParseInt(t.Substring(4, 2));
            fix.Millisecond = t.Length > 7 ? (int)(NmeaParser.ParseNumber("0" + t.Substring(6)) * 1000.0 + 0.5) : 0;
        }
        fix.Latitude = Coordinate(f[1]);
        fix.Longitude = Coordinate(f[2]);
        fix.Hdop = NmeaParser.ParseNumber(f[3]);
        fix.AltitudeMeters = NmeaParser.ParseNumber(f[4]);
        fix.FixMode = ParseInt(f[5]);
        // COG is "ddd.mm" (degrees and minutes) in Quectel's format.
        double cog = NmeaParser.ParseNumber(f[6]);
        int cogDeg = (int)cog;
        fix.CourseDegrees = cogDeg + (cog - cogDeg) * 100.0 / 60.0;
        fix.SpeedKmh = NmeaParser.ParseNumber(f[7]);
        fix.SpeedKnots = NmeaParser.ParseNumber(f[8]);
        string d = f[9];
        if (d.Length >= 6)
        {
            fix.Day = ParseInt(d.Substring(0, 2));
            fix.Month = ParseInt(d.Substring(2, 2));
            fix.Year = 2000 + ParseInt(d.Substring(4, 2));
        }
        fix.SatellitesUsed = ParseInt(f[10]);
        fix.Valid = fix.FixMode >= 2;
        if (fix.Valid)
        {
            Parser.PositionReceived?.Invoke(fix);
        }
        return fix.Valid;
    }

    private static double Coordinate(string s)
    {
        if (s.Length == 0)
        {
            return 0;
        }
        char h = s[s.Length - 1];
        if (h == 'N' || h == 'S' || h == 'E' || h == 'W')
        {
            return NmeaParser.Coordinate(s.Substring(0, s.Length - 1), h == 'S' ? "S" : h == 'W' ? "W" : "N");
        }
        return NmeaParser.ParseNumber(s);
    }

    private static int ParseInt(string s)
    {
        int v = 0;
        bool neg = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '-' && i == 0)
            {
                neg = true;
                continue;
            }
            if (c < '0' || c > '9')
            {
                break;
            }
            v = v * 10 + (c - '0');
        }
        return neg ? -v : v;
    }
}
