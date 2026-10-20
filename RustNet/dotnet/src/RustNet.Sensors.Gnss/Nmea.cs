namespace RustNet.Sensors.Gnss;

/// <summary>
/// The navigation state assembled from the sentences seen so far. GGA, RMC,
/// GSA, VTG and GLL each fill in the part they carry, so one object holds the
/// latest of everything a receiver reports.
/// </summary>
public class GnssFix
{
    /// <summary>True when the receiver reports a usable position.</summary>
    public bool Valid;

    /// <summary>GGA quality: 0 none, 1 GPS, 2 DGPS, 4 RTK fixed, 5 RTK float, 6 dead reckoning.</summary>
    public int Quality;

    /// <summary>GSA mode: 1 no fix, 2 = 2D, 3 = 3D.</summary>
    public int FixMode = 1;

    /// <summary>Decimal degrees, south negative.</summary>
    public double Latitude;

    /// <summary>Decimal degrees, west negative.</summary>
    public double Longitude;

    /// <summary>Metres above mean sea level (GGA).</summary>
    public double AltitudeMeters;

    /// <summary>Geoid separation in metres (GGA).</summary>
    public double GeoidSeparation;

    public int SatellitesUsed;
    public double Hdop;
    public double Pdop;
    public double Vdop;

    public double SpeedKnots;
    public double SpeedKmh;

    /// <summary>True course over ground in degrees.</summary>
    public double CourseDegrees;

    /// <summary>UTC time of the fix.</summary>
    public int Hour;
    public int Minute;
    public int Second;
    public int Millisecond;

    /// <summary>UTC date (RMC); zero until an RMC has been seen.</summary>
    public int Year;
    public int Month;
    public int Day;

    /// <summary>"hh:mm:ss" UTC.</summary>
    public string TimeText() => $"{Two(Hour)}:{Two(Minute)}:{Two(Second)}";

    /// <summary>"yyyy-mm-dd" UTC, or empty when no date is known yet.</summary>
    public string DateText() => Year == 0 ? "" : $"{Year}-{Two(Month)}-{Two(Day)}";

    private static string Two(int v) => v < 10 ? "0" + v : v.ToString();
}

/// <summary>One satellite from a GSV sentence.</summary>
public class SatelliteInfo
{
    /// <summary>Talker of the GSV it came from: GP (GPS), GL (GLONASS), GA (Galileo), GB/BD (BeiDou), GQ (QZSS).</summary>
    public string System = "";
    public int Prn;
    public int ElevationDegrees;
    public int AzimuthDegrees;

    /// <summary>Signal-to-noise in dB-Hz; -1 when the satellite is not tracked.</summary>
    public int Snr = -1;
}

/// <summary>
/// Streaming NMEA-0183 decoder. Hand it bytes or text in whatever pieces the
/// UART delivers — <see cref="Feed(byte[])"/> assembles lines itself — and it
/// updates <see cref="Fix"/>, keeps the satellites in view, and calls the
/// handlers below. Checksums are verified when present; a sentence that fails
/// is counted and dropped.
///
/// Driver-agnostic: MT3339, NEO-M8, BG95 and anything else that speaks NMEA.
/// Pure managed code, so it runs the same on the device and on a desktop.
/// </summary>
public class NmeaParser
{
    /// <summary>The latest navigation state.</summary>
    public GnssFix Fix = new GnssFix();

    /// <summary>Satellites from the last complete GSV group of each system.</summary>
    public List<SatelliteInfo> Satellites = new List<SatelliteInfo>();

    /// <summary>Called after a GGA, RMC or GLL that carries a valid position.</summary>
    public Action<GnssFix>? PositionReceived;

    /// <summary>Called after a VTG or RMC updates speed/course.</summary>
    public Action<GnssFix>? VelocityReceived;

    /// <summary>Called when a GSV group completes, with every satellite currently in view.</summary>
    public Action<List<SatelliteInfo>>? SatellitesInView;

    /// <summary>Called for every well-formed sentence this decoder does not interpret (e.g. $PMTK, $GPTXT).</summary>
    public Action<string>? OtherSentence;

    public int SentencesParsed;
    public int ChecksumErrors;

    private readonly System.Text.StringBuilder _line = new System.Text.StringBuilder();
    private readonly List<SatelliteInfo> _gsvPending = new List<SatelliteInfo>();
    private string _gsvSystem = "";

    /// <summary>Feed raw UART bytes; complete lines are parsed as they arrive.</summary>
    public void Feed(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            FeedChar((char)data[i]);
        }
    }

    /// <summary>Feed text (any chunking, any line ending).</summary>
    public void Feed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            FeedChar(text[i]);
        }
    }

    private void FeedChar(char c)
    {
        if (c == '\r' || c == '\n')
        {
            if (_line.Length > 0)
            {
                ParseSentence(_line.ToString());
                _line.Clear();
            }
            return;
        }
        if (c == '$')
        {
            // A new start always resynchronises, even mid-line.
            _line.Clear();
        }
        // NMEA caps a sentence at 82 characters; anything longer is noise.
        if (_line.Length < 120)
        {
            _line.Append(c);
        }
    }

    /// <summary>Parse one complete sentence ("$GPGGA,...*hh"). Returns true if it was understood.</summary>
    public bool ParseSentence(string sentence)
    {
        string s = sentence.Trim();
        if (s.Length < 6 || s[0] != '$')
        {
            return false;
        }
        int star = s.IndexOf('*');
        string body;
        if (star >= 0)
        {
            body = s.Substring(1, star - 1);
            if (star + 3 > s.Length)
            {
                ChecksumErrors++;
                return false;
            }
            int expected = HexValue(s[star + 1]) * 16 + HexValue(s[star + 2]);
            if (expected < 0 || Checksum(body) != expected)
            {
                ChecksumErrors++;
                return false;
            }
        }
        else
        {
            body = s.Substring(1);
        }

        string[] f = body.Split(',');
        string head = f[0];
        if (head.Length != 5)
        {
            OtherSentence?.Invoke(s);
            return false;
        }
        string talker = head.Substring(0, 2);
        string type = head.Substring(2, 3);
        bool handled = true;
        if (type == "GGA")
        {
            ParseGga(f);
        }
        else if (type == "RMC")
        {
            ParseRmc(f);
        }
        else if (type == "GSA")
        {
            ParseGsa(f);
        }
        else if (type == "GSV")
        {
            ParseGsv(talker, f);
        }
        else if (type == "VTG")
        {
            ParseVtg(f);
        }
        else if (type == "GLL")
        {
            ParseGll(f);
        }
        else
        {
            handled = false;
        }
        if (!handled)
        {
            OtherSentence?.Invoke(s);
            return false;
        }
        SentencesParsed++;
        return true;
    }

    /// <summary>XOR of every character between '$' and '*'.</summary>
    public static int Checksum(string body)
    {
        int sum = 0;
        for (int i = 0; i < body.Length; i++)
        {
            sum = sum ^ body[i];
        }
        return sum & 0xFF;
    }

    // $--GGA,time,lat,N,lon,E,quality,numSV,HDOP,alt,M,sep,M,diffAge,diffStation
    private void ParseGga(string[] f)
    {
        if (f.Length < 10)
        {
            return;
        }
        ParseTime(Field(f, 1));
        Fix.Quality = ParseInt(Field(f, 6));
        Fix.SatellitesUsed = ParseInt(Field(f, 7));
        Fix.Hdop = ParseNumber(Field(f, 8));
        Fix.AltitudeMeters = ParseNumber(Field(f, 9));
        Fix.GeoidSeparation = ParseNumber(Field(f, 11));
        if (Fix.Quality == 0 || Field(f, 2).Length == 0)
        {
            Fix.Valid = false;
            return;
        }
        Fix.Latitude = Coordinate(Field(f, 2), Field(f, 3));
        Fix.Longitude = Coordinate(Field(f, 4), Field(f, 5));
        Fix.Valid = true;
        PositionReceived?.Invoke(Fix);
    }

    // $--RMC,time,status,lat,N,lon,E,sog,cog,date,magvar,E,mode
    private void ParseRmc(string[] f)
    {
        if (f.Length < 10)
        {
            return;
        }
        ParseTime(Field(f, 1));
        string date = Field(f, 9);
        if (date.Length >= 6)
        {
            Fix.Day = ParseInt(date.Substring(0, 2));
            Fix.Month = ParseInt(date.Substring(2, 2));
            // Two-digit year: GPS did not exist before 1980, so 80-99 is the 1900s.
            int yy = ParseInt(date.Substring(4, 2));
            Fix.Year = yy >= 80 ? 1900 + yy : 2000 + yy;
        }
        Fix.SpeedKnots = ParseNumber(Field(f, 7));
        Fix.SpeedKmh = Fix.SpeedKnots * 1.852;
        Fix.CourseDegrees = ParseNumber(Field(f, 8));
        VelocityReceived?.Invoke(Fix);
        if (Field(f, 2) != "A" || Field(f, 3).Length == 0)
        {
            Fix.Valid = false;
            return;
        }
        Fix.Latitude = Coordinate(Field(f, 3), Field(f, 4));
        Fix.Longitude = Coordinate(Field(f, 5), Field(f, 6));
        Fix.Valid = true;
        PositionReceived?.Invoke(Fix);
    }

    // $--GSA,mode,fixType,sv1..sv12,PDOP,HDOP,VDOP
    private void ParseGsa(string[] f)
    {
        if (f.Length < 18)
        {
            return;
        }
        int mode = ParseInt(Field(f, 2));
        if (mode > 0)
        {
            Fix.FixMode = mode;
        }
        Fix.Pdop = ParseNumber(Field(f, 15));
        Fix.Hdop = ParseNumber(Field(f, 16));
        Fix.Vdop = ParseNumber(Field(f, 17));
    }

    // $--GSV,numMsg,msgNum,numSV,{prn,elev,az,snr}x(1..4)
    private void ParseGsv(string talker, string[] f)
    {
        if (f.Length < 4)
        {
            return;
        }
        int total = ParseInt(Field(f, 1));
        int index = ParseInt(Field(f, 2));
        if (index == 1)
        {
            _gsvPending.Clear();
            _gsvSystem = talker;
        }
        else if (talker != _gsvSystem)
        {
            return; // a fragment of a group whose start we missed
        }
        int i = 4;
        while (i + 3 < f.Length + 1 && i < f.Length)
        {
            string prn = Field(f, i);
            if (prn.Length > 0)
            {
                var sat = new SatelliteInfo();
                sat.System = talker;
                sat.Prn = ParseInt(prn);
                sat.ElevationDegrees = ParseInt(Field(f, i + 1));
                sat.AzimuthDegrees = ParseInt(Field(f, i + 2));
                string snr = Field(f, i + 3);
                sat.Snr = snr.Length == 0 ? -1 : ParseInt(snr);
                _gsvPending.Add(sat);
            }
            i += 4;
        }
        if (index == total)
        {
            // Replace this system's satellites, keep the other constellations.
            var merged = new List<SatelliteInfo>();
            for (int k = 0; k < Satellites.Count; k++)
            {
                if (Satellites[k].System != talker)
                {
                    merged.Add(Satellites[k]);
                }
            }
            for (int k = 0; k < _gsvPending.Count; k++)
            {
                merged.Add(_gsvPending[k]);
            }
            Satellites = merged;
            _gsvPending.Clear();
            SatellitesInView?.Invoke(Satellites);
        }
    }

    // $--VTG,cogt,T,cogm,M,sogn,N,sogk,K,mode
    private void ParseVtg(string[] f)
    {
        if (f.Length < 8)
        {
            return;
        }
        Fix.CourseDegrees = ParseNumber(Field(f, 1));
        Fix.SpeedKnots = ParseNumber(Field(f, 5));
        string kmh = Field(f, 7);
        Fix.SpeedKmh = kmh.Length > 0 ? ParseNumber(kmh) : Fix.SpeedKnots * 1.852;
        VelocityReceived?.Invoke(Fix);
    }

    // $--GLL,lat,N,lon,E,time,status,mode
    private void ParseGll(string[] f)
    {
        if (f.Length < 7)
        {
            return;
        }
        ParseTime(Field(f, 5));
        if (Field(f, 6) != "A" || Field(f, 1).Length == 0)
        {
            Fix.Valid = false;
            return;
        }
        Fix.Latitude = Coordinate(Field(f, 1), Field(f, 2));
        Fix.Longitude = Coordinate(Field(f, 3), Field(f, 4));
        Fix.Valid = true;
        PositionReceived?.Invoke(Fix);
    }

    private void ParseTime(string t)
    {
        if (t.Length < 6)
        {
            return;
        }
        Fix.Hour = ParseInt(t.Substring(0, 2));
        Fix.Minute = ParseInt(t.Substring(2, 2));
        Fix.Second = ParseInt(t.Substring(4, 2));
        Fix.Millisecond = 0;
        if (t.Length > 7 && t[6] == '.')
        {
            // Fraction of a second, to millisecond resolution.
            Fix.Millisecond = (int)(ParseNumber("0" + t.Substring(6)) * 1000.0 + 0.5);
        }
    }

    private static string Field(string[] f, int i) => i < f.Length ? f[i] : "";

    /// <summary>"ddmm.mmmm" / "dddmm.mmmm" plus hemisphere to signed decimal degrees.</summary>
    public static double Coordinate(string value, string hemisphere)
    {
        double raw = ParseNumber(value);
        int degrees = (int)(raw / 100.0);
        double result = degrees + (raw - degrees * 100.0) / 60.0;
        if (hemisphere == "S" || hemisphere == "W")
        {
            result = -result;
        }
        return result;
    }

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }
        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }
        return -1000;
    }

    /// <summary>Culture-free decimal parser (NMEA never uses exponents); empty or malformed gives 0.</summary>
    public static double ParseNumber(string s)
    {
        double whole = 0.0;
        double frac = 0.0;
        double scale = 0.1;
        bool inFrac = false;
        bool negative = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '-' && i == 0)
            {
                negative = true;
            }
            else if (c == '.' && !inFrac)
            {
                inFrac = true;
            }
            else if (c >= '0' && c <= '9')
            {
                if (inFrac)
                {
                    frac = frac + (c - '0') * scale;
                    scale = scale * 0.1;
                }
                else
                {
                    whole = whole * 10.0 + (c - '0');
                }
            }
            else
            {
                return 0.0;
            }
        }
        double v = whole + frac;
        return negative ? -v : v;
    }

    private static int ParseInt(string s)
    {
        int result = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
            {
                break;
            }
            result = result * 10 + (c - '0');
        }
        return result;
    }
}

/// <summary>
/// A GNSS module on a UART: configures the port and feeds whatever has arrived
/// into an <see cref="NmeaParser"/> each time <see cref="Poll"/> is called.
/// Call Poll from the application loop (or a timer) at least as often as the
/// module's UART buffer would otherwise overflow — once per second is plenty
/// at 9600 baud with the default 1 Hz output.
/// </summary>
public class GnssReceiver
{
    private readonly int _port;

    public NmeaParser Parser = new NmeaParser();

    public GnssReceiver(int uartPort, int baud)
    {
        _port = uartPort;
        RustNet.Hal.Uart.Configure(uartPort, baud);
    }

    /// <summary>Drain the UART into the parser. Returns how many bytes were consumed.</summary>
    public int Poll()
    {
        int total = 0;
        while (true)
        {
            byte[] chunk = RustNet.Hal.Uart.Read(_port, 256);
            if (chunk.Length == 0)
            {
                return total;
            }
            Parser.Feed(chunk);
            total += chunk.Length;
        }
    }

    /// <summary>Send a command sentence; the "$" and "*hh\r\n" checksum are added.</summary>
    public void SendCommand(string body)
    {
        int sum = NmeaParser.Checksum(body);
        string hex = "0123456789ABCDEF";
        string line = "$" + body + "*" + hex[(sum >> 4) & 0xF] + hex[sum & 0xF] + "\r\n";
        RustNet.Hal.Uart.Write(_port, System.Text.Encoding.UTF8.GetBytes(line));
    }
}
