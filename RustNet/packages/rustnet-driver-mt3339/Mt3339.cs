using RustNet.Community.Bus;
using RustNet.Sensors.Gnss;

namespace RustNet.Community.Mt3339;

/// <summary>
/// MediaTek MT3339 GPS modules (GlobalTop PA6H/PA1616S, Adafruit Ultimate
/// GPS, Quectel L80 …) on a UART, 9600 baud by default. NMEA output goes
/// into an <see cref="NmeaParser"/> (from RustNet.Sensors.Gnss); commands are
/// MTK "$PMTKnnn,…*hh" sentences, acknowledged with "$PMTK001,cmd,flag".
/// </summary>
public class Mt3339
{
    // PMTK001 flags.
    public const int AckInvalid = 0;
    public const int AckUnsupported = 1;
    public const int AckFailed = 2;
    public const int AckSuccess = 3;

    private readonly SerialDevice _serial;

    /// <summary>The NMEA decoder fed by <see cref="Poll"/>.</summary>
    public NmeaParser Parser = new NmeaParser();

    /// <summary>Command number and flag of the last $PMTK001 acknowledgement (-1 = none yet).</summary>
    public int LastAckCommand = -1;
    public int LastAckFlag = -1;

    /// <summary>Firmware release string from $PMTK705 (after <see cref="QueryRelease"/>).</summary>
    public string Release = "";

    /// <summary>Called for every $PMTK sentence received (acks, query replies, $PMTK010 system messages).</summary>
    public Action<string>? PmtkReceived;

    public Mt3339(int uartPort, int baud) : this(new SerialDevice(uartPort, baud))
    {
    }

    public Mt3339(SerialDevice serial)
    {
        _serial = serial;
        Parser.OtherSentence = OnOtherSentence;
    }

    /// <summary>Drain the UART into the parser; returns the number of bytes consumed.</summary>
    public int Poll()
    {
        int total = 0;
        for (int i = 0; i < 64; i++)
        {
            byte[] chunk = _serial.Read(256);
            if (chunk.Length == 0)
            {
                break;
            }
            Parser.Feed(chunk);
            total += chunk.Length;
        }
        return total;
    }

    /// <summary>The full command line for <paramref name="body"/> ("PMTK220,1000" → "$PMTK220,1000*1F\r\n").</summary>
    public static string Sentence(string body)
    {
        int sum = NmeaParser.Checksum(body);
        // Substring, not hex[i]: concatenating a char needs Char.ToString, which the interpreter lacks.
        string hex = "0123456789ABCDEF";
        return "$" + body + "*" + hex.Substring((sum >> 4) & 0xF, 1) + hex.Substring(sum & 0xF, 1) + "\r\n";
    }

    /// <summary>Send a raw command body (without '$' and checksum).</summary>
    public void Send(string body) => _serial.WriteText(Sentence(body));

    /// <summary>Position fix interval / NMEA rate in ms (100 = 10 Hz .. 10000). Raise the baud rate first above 1-5 Hz.</summary>
    public void SetUpdateRate(int intervalMs) => Send("PMTK220," + intervalMs);

    /// <summary>Change the UART baud rate (PMTK251). Reconfigure the local UART afterwards.</summary>
    public void SetBaudRate(int baud) => Send("PMTK251," + baud);

    /// <summary>
    /// Choose which sentences are output, each value being "every Nth fix"
    /// (0 = off, 1 = every fix). PMTK314 fields: GLL, RMC, VTG, GGA, GSA, GSV,
    /// then 13 reserved fields.
    /// </summary>
    public void SetSentenceOutput(int gll, int rmc, int vtg, int gga, int gsa, int gsv)
    {
        string s = "PMTK314," + gll + "," + rmc + "," + vtg + "," + gga + "," + gsa + "," + gsv;
        for (int i = 0; i < 13; i++)
        {
            s = s + ",0";
        }
        Send(s);
    }

    /// <summary>Only RMC and GGA — everything <see cref="NmeaParser"/> needs for position, time and speed.</summary>
    public void OutputRmcGga() => SetSentenceOutput(0, 1, 0, 1, 0, 0);

    /// <summary>Back to the factory sentence set (PMTK314,-1).</summary>
    public void OutputDefault() => Send("PMTK314,-1");

    public void HotStart() => Send("PMTK101");

    public void WarmStart() => Send("PMTK102");

    public void ColdStart() => Send("PMTK103");

    /// <summary>Cold start that also clears all system and user configuration.</summary>
    public void FullColdStart() => Send("PMTK104");

    /// <summary>Standby (PMTK161,0); any byte on RX wakes the module.</summary>
    public void Standby() => Send("PMTK161,0");

    /// <summary>Enable/disable SBAS (WAAS/EGNOS) ranging and DGPS correction from it.</summary>
    public void SetSbas(bool enabled)
    {
        Send(enabled ? "PMTK313,1" : "PMTK313,0");
        Send(enabled ? "PMTK301,2" : "PMTK301,0");
    }

    /// <summary>Ask for the firmware release (answer: $PMTK705 → <see cref="Release"/>).</summary>
    public void QueryRelease() => Send("PMTK605");

    /// <summary>True when the last ack was for <paramref name="command"/> and reported success.</summary>
    public bool Acknowledged(int command) => LastAckCommand == command && LastAckFlag == AckSuccess;

    private void OnOtherSentence(string s)
    {
        if (s.Length < 6 || s[1] != 'P' || s[2] != 'M' || s[3] != 'T' || s[4] != 'K')
        {
            return;
        }
        int star = s.IndexOf('*');
        string body = star > 0 ? s.Substring(1, star - 1) : s.Substring(1);
        string[] f = body.Split(',');
        if (f[0] == "PMTK001" && f.Length >= 3)
        {
            LastAckCommand = ParseInt(f[1]);
            LastAckFlag = ParseInt(f[2]);
        }
        else if (f[0] == "PMTK705" && f.Length >= 2)
        {
            Release = f[1];
        }
        PmtkReceived?.Invoke(s);
    }

    private static int ParseInt(string s)
    {
        int v = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
            {
                break;
            }
            v = v * 10 + (c - '0');
        }
        return v;
    }
}
