using RustNet.Community.Bus;

namespace RustNet.Community.Ultrasonic;

/// <summary>
/// Sends a trigger pulse and measures the echo pulse width, in µs. The
/// default implementation uses the HAL's
/// <see cref="RustNet.Hal.Signal.PulseFeedback"/>, which times the echo in
/// the firmware (C# is far too slow to time microseconds itself). Override
/// <see cref="Measure"/> to simulate or to use other timing hardware.
/// </summary>
public class EchoTimer
{
    public int Pin;

    protected EchoTimer()
    {
    }

    public EchoTimer(int pin)
    {
        Pin = pin;
    }

    /// <summary>Pulse high for <paramref name="pulseUs"/>, return the echo width in µs (0 = no echo before the timeout).</summary>
    public virtual int Measure(int pulseUs, int timeoutUs) => RustNet.Hal.Signal.PulseFeedback(Pin, true, pulseUs, timeoutUs);
}

/// <summary>An echo timer returning widths queued by a test.</summary>
public class SimEchoTimer : EchoTimer
{
    public Queue<int> Echoes = new Queue<int>();
    public int LastPulseUs;

    public SimEchoTimer()
    {
    }

    public override int Measure(int pulseUs, int timeoutUs)
    {
        LastPulseUs = pulseUs;
        return Echoes.Count > 0 ? Echoes.Dequeue() : 0;
    }
}

/// <summary>
/// HC-SR04 ultrasonic rangefinder (2 cm .. 4 m). A 10 µs trigger starts a
/// ping; ECHO stays high for the round-trip time. The datasheet's
/// conversion is µs / 58 = cm (µs / 148 = inches); <see cref="SpeedOfSound"/>
/// gives a temperature-compensated alternative.
///
/// Wiring: the HAL's PulseFeedback triggers and listens on one pin, as on
/// 3-pin modules (Parallax PING, Grove ultrasonic, HC-SR04 in one-pin mode).
/// For a 4-pin HC-SR04, join TRIG to the GPIO and ECHO to it through a
/// 1 kΩ resistor (and level-shift ECHO from 5 V on 3.3 V boards).
/// </summary>
public class HcSr04
{
    private readonly EchoTimer _timer;

    /// <summary>Echo wait limit; the module gives up after ~38 ms when nothing is in range.</summary>
    public int TimeoutMicroseconds = 40000;

    /// <summary>Speed of sound in m/s, 343.4 at 20 °C by default.</summary>
    public double SpeedOfSound = 343.4;

    /// <summary>Echo width of the last ping, µs (0 = none).</summary>
    public int LastEchoMicroseconds;

    public HcSr04(int pin) : this(new EchoTimer(pin))
    {
    }

    public HcSr04(EchoTimer timer)
    {
        _timer = timer;
    }

    /// <summary>Speed of sound in dry air at <paramref name="celsius"/>: 331.3 + 0.606·T m/s.</summary>
    public void SetAirTemperature(double celsius) => SpeedOfSound = 331.3 + 0.606 * celsius;

    /// <summary>Ping once and return the echo width in µs (0 = no echo).</summary>
    public int Ping()
    {
        LastEchoMicroseconds = _timer.Measure(10, TimeoutMicroseconds);
        return LastEchoMicroseconds;
    }

    /// <summary>Distance in mm using <see cref="SpeedOfSound"/>; -1 when nothing echoed.</summary>
    public double ReadMillimeters()
    {
        int us = Ping();
        if (us <= 0)
        {
            return -1;
        }
        return us * SpeedOfSound / 2000.0;
    }

    /// <summary>Datasheet formula: echo µs / 58 = cm.</summary>
    public static double DatasheetCentimeters(int echoUs) => echoUs / 58.0;

    /// <summary>Datasheet formula: echo µs / 148 = inches.</summary>
    public static double DatasheetInches(int echoUs) => echoUs / 148.0;

    /// <summary>Median of <paramref name="samples"/> pings in mm (ignores misses; -1 if all missed). Wait ≥ 60 ms between pings.</summary>
    public double ReadMedianMillimeters(int samples, int gapMs)
    {
        List<double> v = new List<double>();
        for (int i = 0; i < samples; i++)
        {
            double d = ReadMillimeters();
            if (d >= 0)
            {
                v.Add(d);
            }
            if (i + 1 < samples)
            {
                Timing.SleepMs(gapMs);
            }
        }
        if (v.Count == 0)
        {
            return -1;
        }
        v.Sort();
        return v[v.Count / 2];
    }
}

/// <summary>
/// MaxBotix MaxSonar rangefinders. The analog output and the serial format
/// both depend on the family:
/// LV-MaxSonar-EZ: AN = Vcc/512 per inch, serial "Rnnn\r" in inches;
/// HRLV-MaxSonar (and other mm models): AN = Vcc/1024 per 5 mm, serial "Rnnnn\r" in mm;
/// XL-MaxSonar: AN = Vcc/1024 per cm, serial "Rnnn\r" in cm.
/// Serial is 9600 8N1; the TX pin is RS-232 polarity (inverted) unless the
/// board's jumper/TTL variant is used, so it may need an inverter or an
/// inverted UART.
/// </summary>
public class MaxBotix
{
    public const int FamilyLvEz = 0;
    public const int FamilyHrlv = 1;
    public const int FamilyXl = 2;

    private readonly AnalogInput? _analog;
    private readonly SerialDevice? _serial;
    private readonly System.Text.StringBuilder _line = new System.Text.StringBuilder();
    private bool _inFrame;

    public int Family;

    /// <summary>Sensor supply in mV (the analog output is ratiometric to it).</summary>
    public double SupplyMillivolts = 5000;

    /// <summary>Last complete serial reading, in mm (-1 until one arrives).</summary>
    public int LastSerialMillimeters = -1;

    public MaxBotix(int family, AnalogInput analog, double supplyMillivolts)
    {
        Family = family;
        _analog = analog;
        SupplyMillivolts = supplyMillivolts;
    }

    public MaxBotix(int family, SerialDevice serial)
    {
        Family = family;
        _serial = serial;
    }

    /// <summary>Analog convenience constructor (ADC channel).</summary>
    public MaxBotix(int family, int adcChannel, double supplyMillivolts) : this(family, new AnalogInput(adcChannel), supplyMillivolts)
    {
    }

    /// <summary>Distance in mm from a pin voltage in mV.</summary>
    public double MillivoltsToMillimeters(double mv)
    {
        if (Family == FamilyLvEz)
        {
            return mv / (SupplyMillivolts / 512.0) * 25.4;
        }
        if (Family == FamilyHrlv)
        {
            return mv / (SupplyMillivolts / 1024.0) * 5.0;
        }
        return mv / (SupplyMillivolts / 1024.0) * 10.0;
    }

    /// <summary>Distance in mm from the analog output.</summary>
    public double ReadAnalogMillimeters()
    {
        if (_analog == null)
        {
            throw new InvalidOperationException("no analog input");
        }
        return MillivoltsToMillimeters(_analog.ReadMillivolts());
    }

    /// <summary>Drain the UART and parse "R" frames. Returns true when a new reading arrived.</summary>
    public bool Poll()
    {
        if (_serial == null)
        {
            return false;
        }
        byte[] b = _serial.Read(64);
        bool got = false;
        for (int i = 0; i < b.Length; i++)
        {
            if (FeedByte(b[i]))
            {
                got = true;
            }
        }
        return got;
    }

    /// <summary>Feed one received byte; true when it completed a reading.</summary>
    public bool FeedByte(int c)
    {
        if (c == 'R')
        {
            _inFrame = true;
            _line.Clear();
            return false;
        }
        if (!_inFrame)
        {
            return false;
        }
        if (c == '\r')
        {
            _inFrame = false;
            if (_line.Length == 0)
            {
                return false;
            }
            int v = 0;
            string s = _line.ToString();
            for (int i = 0; i < s.Length; i++)
            {
                v = v * 10 + (s[i] - '0');
            }
            LastSerialMillimeters = Family == FamilyLvEz ? (int)(v * 25.4 + 0.5) : Family == FamilyXl ? v * 10 : v;
            return true;
        }
        if (c >= '0' && c <= '9' && _line.Length < 5)
        {
            _line.Append((char)c);
        }
        else
        {
            _inFrame = false; // garbage: drop the frame
        }
        return false;
    }
}

/// <summary>
/// DFRobot A02YYUW waterproof ultrasonic sensor (UART 9600 8N1, 30..4500 mm).
/// Each reading is a 4-byte frame 0xFF, Data_H, Data_L, SUM where
/// SUM = (0xFF + Data_H + Data_L) &amp; 0xFF and distance = Data_H·256 + Data_L mm.
/// Pulling RX low (or leaving it floating) selects the processed output; the
/// "controlled" mode answers one frame per falling edge on RX.
/// </summary>
public class A02yyuw
{
    private readonly SerialDevice _serial;
    private readonly byte[] _frame = new byte[4];
    private int _have;

    /// <summary>Last valid distance in mm (-1 until a frame arrives).</summary>
    public int Millimeters = -1;

    public int ChecksumErrors;

    public A02yyuw(int uartPort) : this(new SerialDevice(uartPort, 9600))
    {
    }

    public A02yyuw(SerialDevice serial)
    {
        _serial = serial;
    }

    /// <summary>Drain the UART; true when at least one valid frame was decoded.</summary>
    public bool Poll()
    {
        byte[] b = _serial.Read(64);
        bool got = false;
        for (int i = 0; i < b.Length; i++)
        {
            if (FeedByte(b[i]))
            {
                got = true;
            }
        }
        return got;
    }

    /// <summary>Frame decoder; resynchronises on 0xFF.</summary>
    public bool FeedByte(int c)
    {
        if (_have == 0)
        {
            if (c == 0xFF)
            {
                _frame[0] = 0xFF;
                _have = 1;
            }
            return false;
        }
        _frame[_have++] = (byte)c;
        if (_have < 4)
        {
            return false;
        }
        _have = 0;
        if (((0xFF + _frame[1] + _frame[2]) & 0xFF) != _frame[3])
        {
            ChecksumErrors++;
            // The checksum byte could itself be the next header.
            if (_frame[3] == 0xFF)
            {
                _have = 1;
            }
            return false;
        }
        Millimeters = (_frame[1] << 8) | _frame[2];
        return true;
    }

    /// <summary>Frame checksum: low byte of 0xFF + H + L.</summary>
    public static int Checksum(int high, int low) => (0xFF + high + low) & 0xFF;
}

/// <summary>
/// ME007YS waterproof ultrasonic sensor (DFRobot SEN0312, 280..4500 mm): the
/// same UART 9600 frame as the A02YYUW (0xFF, Data_H, Data_L, SUM).
/// </summary>
public class Me007ys : A02yyuw
{
    public Me007ys(int uartPort) : base(uartPort)
    {
    }

    public Me007ys(SerialDevice serial) : base(serial)
    {
    }
}
