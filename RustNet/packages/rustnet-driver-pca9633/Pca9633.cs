using RustNet.Community.Bus;

namespace RustNet.Community.Pca9633;

/// <summary>
/// NXP PCA9633 4-bit Fm+ I2C LED driver: four 8-bit PWM channels (97 kHz),
/// a group PWM that either dims all channels (190 Hz) or blinks them
/// (period (GFRQ+1)/24 s). Register map:
/// MODE1 0x00, MODE2 0x01, PWM0..3 0x02..0x05, GRPPWM 0x06, GRPFREQ 0x07,
/// LEDOUT 0x08 (2 bits per LED: 00 off, 01 on, 10 PWM, 11 PWM + group).
/// The control byte's top three bits select auto-increment; 101 (0xA0)
/// increments over the four brightness registers only.
/// Common addresses: 0x62 (8-pin parts), 0x60–0x6F with address pins.
/// </summary>
public class Pca9633
{
    public const int DefaultAddress = 0x62;

    public const int RegMode1 = 0x00;
    public const int RegMode2 = 0x01;
    public const int RegPwm0 = 0x02;
    public const int RegGrpPwm = 0x06;
    public const int RegGrpFreq = 0x07;
    public const int RegLedOut = 0x08;

    /// <summary>Auto-increment over the individual brightness registers only.</summary>
    public const int AutoIncrementBrightness = 0xA0;

    public const int LedOff = 0;
    public const int LedOn = 1;
    public const int LedPwm = 2;
    public const int LedPwmGroup = 3;

    private readonly I2cDevice _dev;
    private int _ledOut;
    private int _mode2;

    public Pca9633(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Pca9633(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Pca9633(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>
    /// Wake the oscillator (MODE1: SLEEP=0, ALLCALL kept), select totem-pole
    /// outputs (MODE2 OUTDRV=1, optionally INVRT for external drivers) and put
    /// every channel under individual PWM control.
    /// </summary>
    public void Begin(bool invert = false, bool openDrain = false)
    {
        _dev.WriteRegister(RegMode1, 0x01);
        Timing.SleepMs(1); // oscillator start-up: 500 µs max
        _mode2 = (invert ? 0x10 : 0x00) | (openDrain ? 0x00 : 0x04);
        _dev.WriteRegister(RegMode2, _mode2);
        SetAllOutputModes(LedPwm);
    }

    /// <summary>Put the oscillator to sleep (outputs off, registers kept).</summary>
    public void Sleep() => _dev.WriteRegister(RegMode1, 0x11);

    /// <summary>Output mode of one channel (<see cref="LedOff"/>, <see cref="LedOn"/>, <see cref="LedPwm"/>, <see cref="LedPwmGroup"/>).</summary>
    public void SetOutputMode(int channel, int mode)
    {
        int shift = channel * 2;
        _ledOut = (_ledOut & ~(3 << shift)) | ((mode & 3) << shift);
        _dev.WriteRegister(RegLedOut, _ledOut);
    }

    public void SetAllOutputModes(int mode)
    {
        int m = mode & 3;
        _ledOut = m | (m << 2) | (m << 4) | (m << 6);
        _dev.WriteRegister(RegLedOut, _ledOut);
    }

    /// <summary>Brightness of one channel, 0..255 (duty = value/256).</summary>
    public void SetPwm(int channel, int value) => _dev.WriteRegister(RegPwm0 + (channel & 3), value);

    /// <summary>All four brightness registers in one transfer (auto-increment).</summary>
    public void SetAll(int pwm0, int pwm1, int pwm2, int pwm3)
    {
        byte[] b = new byte[5];
        b[0] = (byte)(AutoIncrementBrightness | RegPwm0);
        b[1] = (byte)pwm0;
        b[2] = (byte)pwm1;
        b[3] = (byte)pwm2;
        b[4] = (byte)pwm3;
        _dev.Write(b);
    }

    /// <summary>Group dimming: GRPPWM scales every channel in group mode (DMBLNK=0).</summary>
    public void SetGroupDimming(int value)
    {
        _mode2 = _mode2 & ~0x20;
        _dev.WriteRegister(RegMode2, _mode2);
        _dev.WriteRegister(RegGrpPwm, value);
        SetAllOutputModes(LedPwmGroup);
    }

    /// <summary>
    /// Group blinking (DMBLNK=1): period = (GRPFREQ+1)/24 s, 41.6 ms..10.73 s;
    /// on-time fraction = GRPPWM/256.
    /// </summary>
    public void SetGroupBlink(double periodSeconds, double onFraction)
    {
        int freq = (int)(periodSeconds * 24.0 - 1.0 + 0.5);
        if (freq < 0)
        {
            freq = 0;
        }
        if (freq > 255)
        {
            freq = 255;
        }
        int duty = (int)(onFraction * 256.0 + 0.5);
        if (duty > 255)
        {
            duty = 255;
        }
        _mode2 = _mode2 | 0x20;
        _dev.WriteRegister(RegMode2, _mode2);
        _dev.WriteRegister(RegGrpFreq, freq);
        _dev.WriteRegister(RegGrpPwm, duty);
        SetAllOutputModes(LedPwmGroup);
    }

    /// <summary>RGB(W) convenience: channels 0..2 = R, G, B.</summary>
    public void SetRgb(int r, int g, int b) => SetAll(r, g, b, 0);
}
