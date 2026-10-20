using RustNet.Community.Bus;

namespace RustNet.Community.Input;

/// <summary>
/// A scanned key matrix (3×4 / 4×4 membrane keypads and the like). Rows are
/// outputs driven low one at a time; columns are inputs with pull-ups, so a
/// pressed key reads low on its column while its row is driven. Add
/// series diodes for reliable multi-key (n-key rollover) detection.
/// </summary>
public class MatrixKeypad
{
    private readonly DigitalOutput[] _rows;
    private readonly DigitalInput[] _cols;
    private int _lastKey = -1;
    private int _stableKey = -1;
    private int _stableCount;

    /// <summary>Characters row by row; length must be rows × columns.</summary>
    public string Keys;

    /// <summary>Consecutive identical scans before a change is accepted.</summary>
    public int DebounceScans = 2;

    /// <summary>Called with the key character when a key is pressed (after debouncing).</summary>
    public Action<char>? KeyPressed;

    public MatrixKeypad(DigitalOutput[] rows, DigitalInput[] cols, string keys)
    {
        _rows = rows;
        _cols = cols;
        Keys = keys;
        for (int r = 0; r < rows.Length; r++)
        {
            rows[r].Write(true);
        }
    }

    /// <summary>Convenience constructor from pin numbers (columns get pull-ups).</summary>
    public static MatrixKeypad FromPins(int[] rowPins, int[] colPins, string keys)
    {
        DigitalOutput[] rows = new DigitalOutput[rowPins.Length];
        for (int i = 0; i < rowPins.Length; i++)
        {
            rows[i] = new DigitalOutput(rowPins[i], true);
        }
        DigitalInput[] cols = new DigitalInput[colPins.Length];
        for (int i = 0; i < colPins.Length; i++)
        {
            cols[i] = new DigitalInput(colPins[i], RustNet.Hal.PinMode.InputPullUp);
        }
        return new MatrixKeypad(rows, cols, keys);
    }

    /// <summary>The standard 4×4 layout "123A456B789C*0#D".</summary>
    public const string Layout4x4 = "123A456B789C*0#D";

    /// <summary>The standard 4×3 layout "123456789*0#".</summary>
    public const string Layout4x3 = "123456789*0#";

    /// <summary>Scan once; returns every pressed key index (row × columns + column).</summary>
    public List<int> ScanAll()
    {
        List<int> pressed = new List<int>();
        for (int r = 0; r < _rows.Length; r++)
        {
            _rows[r].Write(false);
            for (int c = 0; c < _cols.Length; c++)
            {
                if (!_cols[c].Read())
                {
                    pressed.Add(r * _cols.Length + c);
                }
            }
            _rows[r].Write(true);
        }
        return pressed;
    }

    /// <summary>Scan once; the first pressed key's index or -1.</summary>
    public int Scan()
    {
        List<int> p = ScanAll();
        return p.Count > 0 ? p[0] : -1;
    }

    /// <summary>
    /// Scan with debouncing; returns a key character once per press (on the
    /// press edge), else '\0'. Call periodically (every ~10-20 ms).
    /// </summary>
    public char Poll()
    {
        int k = Scan();
        if (k == _lastKey)
        {
            _stableCount++;
        }
        else
        {
            _lastKey = k;
            _stableCount = 1;
        }
        if (_stableCount == DebounceScans && k != _stableKey)
        {
            _stableKey = k;
            if (k >= 0 && k < Keys.Length)
            {
                char ch = Keys[k];
                KeyPressed?.Invoke(ch);
                return ch;
            }
        }
        return '\0';
    }
}

/// <summary>
/// An analog thumb joystick: two potentiometers on ADC channels and an
/// optional push button (active low). Positions are normalised to -1..+1
/// around a calibrated centre with a deadzone.
/// </summary>
public class AnalogJoystick
{
    private readonly AnalogInput _x;
    private readonly AnalogInput _y;
    private readonly DigitalInput? _button;

    public int CenterX;
    public int CenterY;

    /// <summary>Raw extremes reached at full deflection (default: the ADC's 0..MaxRaw).</summary>
    public int MinRaw;
    public int MaxRaw;

    /// <summary>Fraction of travel around the centre reported as 0 (default 0.08).</summary>
    public double Deadzone = 0.08;

    public bool InvertX;
    public bool InvertY;

    /// <summary>Last normalised position, -1..+1.</summary>
    public double X;
    public double Y;
    public bool Pressed;

    public AnalogJoystick(int adcX, int adcY, int buttonPin) : this(new AnalogInput(adcX), new AnalogInput(adcY),
        buttonPin >= 0 ? new DigitalInput(buttonPin, RustNet.Hal.PinMode.InputPullUp) : null)
    {
    }

    public AnalogJoystick(AnalogInput x, AnalogInput y, DigitalInput? button)
    {
        _x = x;
        _y = y;
        _button = button;
        MinRaw = 0;
        MaxRaw = x.MaxRaw;
        CenterX = MaxRaw / 2;
        CenterY = MaxRaw / 2;
    }

    /// <summary>Take the current (released) position as the centre.</summary>
    public void CalibrateCenter()
    {
        CenterX = _x.ReadRaw();
        CenterY = _y.ReadRaw();
    }

    /// <summary>Map a raw reading to -1..+1 with the deadzone removed and the remaining travel rescaled.</summary>
    public double Normalize(int raw, int center)
    {
        double v = raw >= center
            ? (double)(raw - center) / (MaxRaw - center)
            : (double)(raw - center) / (center - MinRaw);
        if (v > 1)
        {
            v = 1;
        }
        if (v < -1)
        {
            v = -1;
        }
        double a = v < 0 ? -v : v;
        if (a <= Deadzone)
        {
            return 0;
        }
        double scaled = (a - Deadzone) / (1 - Deadzone);
        return v < 0 ? -scaled : scaled;
    }

    /// <summary>Sample both axes and the button.</summary>
    public void Read()
    {
        X = Normalize(_x.ReadRaw(), CenterX);
        Y = Normalize(_y.ReadRaw(), CenterY);
        if (InvertX)
        {
            X = -X;
        }
        if (InvertY)
        {
            Y = -Y;
        }
        Pressed = _button != null && !_button.Read();
    }

    /// <summary>Direction of the last reading in degrees (0 = +X, counter-clockwise), or -1 when centred.</summary>
    public double AngleDegrees()
    {
        if (X == 0 && Y == 0)
        {
            return -1;
        }
        double a = Math.Atan2(Y, X) * 180.0 / Math.PI;
        return a < 0 ? a + 360.0 : a;
    }

    /// <summary>Deflection magnitude 0..1 (clamped).</summary>
    public double Magnitude()
    {
        double m = Math.Sqrt(X * X + Y * Y);
        return m > 1 ? 1 : m;
    }
}
