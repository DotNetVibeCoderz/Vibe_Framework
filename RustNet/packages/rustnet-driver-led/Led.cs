using RustNet.Community.Bus;

namespace RustNet.Community.Led;

/// <summary>A single LED on a GPIO (active-high by default; set active-low for LEDs wired to VCC).</summary>
public class Led
{
    private readonly DigitalOutput _pin;
    private readonly bool _activeLow;

    public bool IsOn;

    public Led(int pin) : this(new DigitalOutput(pin, false), false)
    {
    }

    public Led(int pin, bool activeLow) : this(new DigitalOutput(pin, activeLow), activeLow)
    {
    }

    public Led(DigitalOutput pin, bool activeLow)
    {
        _pin = pin;
        _activeLow = activeLow;
    }

    public void Set(bool on)
    {
        IsOn = on;
        _pin.Write(on != _activeLow);
    }

    public void On() => Set(true);

    public void Off() => Set(false);

    public void Toggle() => Set(!IsOn);

    /// <summary>Blink <paramref name="count"/> times (blocking), ending off.</summary>
    public void Blink(int count, int onMs, int offMs)
    {
        for (int i = 0; i < count; i++)
        {
            Set(true);
            Timing.SleepMs(onMs);
            Set(false);
            Timing.SleepMs(offMs);
        }
    }
}

/// <summary>Gamma correction used by the PWM LED classes.</summary>
public static class Gamma
{
    /// <summary>Perceived brightness 0..1 to PWM duty in permyriad: round(b^gamma × 10000).</summary>
    public static int ToPermyriad(double brightness, double gamma)
    {
        if (brightness <= 0)
        {
            return 0;
        }
        if (brightness >= 1)
        {
            return 10000;
        }
        return (int)(Math.Pow(brightness, gamma) * 10000.0 + 0.5);
    }
}

/// <summary>
/// An LED dimmed by PWM. Brightness is perceptual: the duty is
/// brightness^<see cref="GammaExponent"/> (2.2 by default, close to the eye's
/// response), so 50 % brightness looks half as bright rather than nearly full.
/// </summary>
public class PwmLed
{
    private readonly PwmOutput _pwm;
    private readonly bool _activeLow;

    /// <summary>Gamma exponent; 1.0 makes duty linear in brightness.</summary>
    public double GammaExponent = 2.2;

    public double Brightness;

    public PwmLed(int pwmChannel) : this(new PwmOutput(pwmChannel, 1000), false)
    {
    }

    public PwmLed(PwmOutput pwm, bool activeLow)
    {
        _pwm = pwm;
        _activeLow = activeLow;
    }

    /// <summary>Brightness 0..1.</summary>
    public void SetBrightness(double brightness)
    {
        if (brightness < 0)
        {
            brightness = 0;
        }
        if (brightness > 1)
        {
            brightness = 1;
        }
        Brightness = brightness;
        int duty = Gamma.ToPermyriad(brightness, GammaExponent);
        _pwm.SetDuty(_activeLow ? 10000 - duty : duty);
    }

    public void On() => SetBrightness(1);

    public void Off() => SetBrightness(0);
}

/// <summary>
/// A three-pin RGB LED switched on/off per colour (7 colours plus off).
/// Common-anode parts light a colour by pulling its pin low; common-cathode by
/// driving it high.
/// </summary>
public class RgbLed
{
    private readonly DigitalOutput _r;
    private readonly DigitalOutput _g;
    private readonly DigitalOutput _b;

    public bool CommonAnode;

    public bool Red;
    public bool Green;
    public bool Blue;

    public RgbLed(int redPin, int greenPin, int bluePin, bool commonAnode)
        : this(new DigitalOutput(redPin, commonAnode), new DigitalOutput(greenPin, commonAnode), new DigitalOutput(bluePin, commonAnode), commonAnode)
    {
    }

    public RgbLed(DigitalOutput red, DigitalOutput green, DigitalOutput blue, bool commonAnode)
    {
        _r = red;
        _g = green;
        _b = blue;
        CommonAnode = commonAnode;
    }

    public void SetColor(bool red, bool green, bool blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
        _r.Write(red != CommonAnode);
        _g.Write(green != CommonAnode);
        _b.Write(blue != CommonAnode);
    }

    public void Off() => SetColor(false, false, false);
}

/// <summary>An RGB colour, 0..255 per channel.</summary>
public class Rgb
{
    public int R;
    public int G;
    public int B;

    public Rgb(int r, int g, int b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>
    /// HSV to RGB: hue in degrees (0..360), saturation and value 0..1.
    /// Standard hexcone model: C = V·S, X = C·(1 − |(H/60) mod 2 − 1|), m = V − C.
    /// </summary>
    public static Rgb FromHsv(double hue, double saturation, double value)
    {
        hue = hue % 360.0;
        if (hue < 0)
        {
            hue += 360.0;
        }
        double c = value * saturation;
        double hp = hue / 60.0;
        double mod = hp % 2.0;
        double x = c * (1.0 - Math.Abs(mod - 1.0));
        double r1 = 0;
        double g1 = 0;
        double b1 = 0;
        int sector = (int)hp;
        if (sector == 0)
        {
            r1 = c; g1 = x;
        }
        else if (sector == 1)
        {
            r1 = x; g1 = c;
        }
        else if (sector == 2)
        {
            g1 = c; b1 = x;
        }
        else if (sector == 3)
        {
            g1 = x; b1 = c;
        }
        else if (sector == 4)
        {
            r1 = x; b1 = c;
        }
        else
        {
            r1 = c; b1 = x;
        }
        double m = value - c;
        return new Rgb((int)((r1 + m) * 255.0 + 0.5), (int)((g1 + m) * 255.0 + 0.5), (int)((b1 + m) * 255.0 + 0.5));
    }
}

/// <summary>
/// An RGB LED with each colour on a PWM channel: full-colour mixing, optional
/// gamma correction, and HSV input. Common-anode parts get inverted duty.
/// </summary>
public class RgbPwmLed
{
    private readonly PwmOutput _r;
    private readonly PwmOutput _g;
    private readonly PwmOutput _b;

    public bool CommonAnode;

    /// <summary>Gamma exponent applied per channel (1.0 = linear duty, the default).</summary>
    public double GammaExponent = 1.0;

    public int Red;
    public int Green;
    public int Blue;

    public RgbPwmLed(int redChannel, int greenChannel, int blueChannel, bool commonAnode)
        : this(new PwmOutput(redChannel, 1000), new PwmOutput(greenChannel, 1000), new PwmOutput(blueChannel, 1000), commonAnode)
    {
    }

    public RgbPwmLed(PwmOutput red, PwmOutput green, PwmOutput blue, bool commonAnode)
    {
        _r = red;
        _g = green;
        _b = blue;
        CommonAnode = commonAnode;
    }

    /// <summary>Colour as 0..255 per channel.</summary>
    public void SetColor(int red, int green, int blue)
    {
        Red = Clamp(red);
        Green = Clamp(green);
        Blue = Clamp(blue);
        _r.SetDuty(Duty(Red));
        _g.SetDuty(Duty(Green));
        _b.SetDuty(Duty(Blue));
    }

    public void SetColor(Rgb c) => SetColor(c.R, c.G, c.B);

    /// <summary>Colour from hue (degrees), saturation and value (0..1).</summary>
    public void SetHsv(double hue, double saturation, double value) => SetColor(Rgb.FromHsv(hue, saturation, value));

    public void Off() => SetColor(0, 0, 0);

    private static int Clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

    private int Duty(int level)
    {
        int d = Gamma.ToPermyriad(level / 255.0, GammaExponent);
        return CommonAnode ? 10000 - d : d;
    }
}

/// <summary>A row of LEDs used as a level meter (LED 0 lights first).</summary>
public class LedBarGraph
{
    private readonly Led[] _leds;

    /// <summary>Number of LEDs currently lit.</summary>
    public int Lit;

    public LedBarGraph(Led[] leds)
    {
        _leds = leds;
    }

    /// <summary>Bar graph on consecutive GPIO pins (active high).</summary>
    public static LedBarGraph FromPins(int[] pins)
    {
        Led[] leds = new Led[pins.Length];
        for (int i = 0; i < pins.Length; i++)
        {
            leds[i] = new Led(pins[i]);
        }
        return new LedBarGraph(leds);
    }

    public int Count => _leds.Length;

    /// <summary>Light the first <paramref name="count"/> LEDs, turn the rest off.</summary>
    public void SetLevel(int count)
    {
        if (count < 0)
        {
            count = 0;
        }
        if (count > _leds.Length)
        {
            count = _leds.Length;
        }
        for (int i = 0; i < _leds.Length; i++)
        {
            bool on = i < count;
            // Only touch LEDs whose state changes: one host call each.
            if (_leds[i].IsOn != on)
            {
                _leds[i].Set(on);
            }
        }
        Lit = count;
    }

    /// <summary>Fraction 0..1 of the bar, rounded to the nearest LED.</summary>
    public void SetPercentage(double fraction) => SetLevel((int)(fraction * _leds.Length + 0.5));

    /// <summary>Light a single LED (dot mode).</summary>
    public void SetDot(int index)
    {
        for (int i = 0; i < _leds.Length; i++)
        {
            _leds[i].Set(i == index);
        }
        Lit = index < 0 ? 0 : 1;
    }

    public Led GetLed(int index) => _leds[index];
}
