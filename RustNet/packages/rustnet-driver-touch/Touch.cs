using RustNet.Community.Bus;

namespace RustNet.Community.Touch;

/// <summary>One touch sample: raw 12-bit ADC values, screen coordinates and touch resistance.</summary>
public class TouchPoint
{
    public bool Touched;
    public int RawX;
    public int RawY;
    public int Z1;
    public int Z2;

    /// <summary>Calibrated screen position in pixels (raw values when uncalibrated).</summary>
    public double X;
    public double Y;

    /// <summary>Touch resistance in Ω (lower = firmer press); 0 when not touched.</summary>
    public double ResistanceOhms;
}

/// <summary>
/// Raw-to-screen mapping by three-point affine calibration (TI application
/// report SLYT277): screenX = A·rawX + B·rawY + C, screenY = D·rawX + E·rawY + F.
/// Handles any rotation, mirroring, scale and offset. The identity mapping
/// is the default.
/// </summary>
public class TouchCalibration
{
    public double A = 1, B, C, D, E = 1, F;

    /// <summary>Solve the coefficients from three raw/screen point pairs (not collinear).</summary>
    public bool Solve(double rx0, double ry0, double sx0, double sy0,
        double rx1, double ry1, double sx1, double sy1,
        double rx2, double ry2, double sx2, double sy2)
    {
        double det = (rx0 - rx2) * (ry1 - ry2) - (rx1 - rx2) * (ry0 - ry2);
        if (det == 0)
        {
            return false;
        }
        A = ((sx0 - sx2) * (ry1 - ry2) - (sx1 - sx2) * (ry0 - ry2)) / det;
        B = ((rx0 - rx2) * (sx1 - sx2) - (sx0 - sx2) * (rx1 - rx2)) / det;
        C = sx0 - A * rx0 - B * ry0;
        D = ((sy0 - sy2) * (ry1 - ry2) - (sy1 - sy2) * (ry0 - ry2)) / det;
        E = ((rx0 - rx2) * (sy1 - sy2) - (sy0 - sy2) * (rx1 - rx2)) / det;
        F = sy0 - D * rx0 - E * ry0;
        return true;
    }

    /// <summary>Two-corner (min/max) linear calibration without rotation.</summary>
    public void SetLinear(double rawXAtLeft, double rawXAtRight, double rawYAtTop, double rawYAtBottom, int width, int height)
    {
        A = (width - 1) / (rawXAtRight - rawXAtLeft);
        B = 0;
        C = -A * rawXAtLeft;
        D = 0;
        E = (height - 1) / (rawYAtBottom - rawYAtTop);
        F = -E * rawYAtTop;
    }

    public double MapX(double rx, double ry) => A * rx + B * ry + C;

    public double MapY(double rx, double ry) => D * rx + E * ry + F;

    /// <summary>
    /// Touch resistance from the datasheet's first method:
    /// R = R_X-plate × (X / 4096) × (Z2 / Z1 − 1).
    /// </summary>
    public static double Resistance(double xPlateOhms, int rawX, int z1, int z2)
    {
        if (z1 <= 0)
        {
            return 0;
        }
        return xPlateOhms * (rawX / 4096.0) * ((double)z2 / z1 - 1.0);
    }
}

/// <summary>
/// XPT2046 / ADS7843 / TSC2046 4-wire resistive touch controller on SPI
/// (mode 0, ≤ 2 MHz). Each conversion is a control byte (S, A2..A0,
/// 12-bit, differential, PD1..PD0) followed by 16 clocks returning the
/// result in bits 14:3. All samples of one reading go out as a single SPI
/// transaction; the last command powers down with PENIRQ re-enabled.
/// </summary>
public class Xpt2046
{
    // Control bytes, 12-bit differential, PD = 01 (ADC on, IRQ off) while sampling.
    public const int CmdX = 0xD1;
    public const int CmdY = 0x91;
    public const int CmdZ1 = 0xB1;
    public const int CmdZ2 = 0xC1;

    /// <summary>Y conversion with PD = 00: power down between conversions, PENIRQ enabled.</summary>
    public const int CmdPowerDown = 0x90;

    private readonly SpiDevice _spi;

    public TouchCalibration Calibration = new TouchCalibration();

    /// <summary>X-plate resistance in Ω (from the panel's datasheet; ~300-600 Ω typical).</summary>
    public double XPlateOhms = 400;

    /// <summary>Minimum Z1 to count as touched.</summary>
    public int MinZ1 = 100;

    /// <summary>X/Y samples averaged per reading (after dropping the first, which is noisy).</summary>
    public int Samples = 3;

    public Xpt2046(int bus, int chipSelectPin) : this(new SpiDevice(bus, chipSelectPin, 2000000, 0))
    {
    }

    public Xpt2046(SpiDevice spi)
    {
        _spi = spi;
    }

    private static int Value(byte[] rx, int group) => (((rx[group * 3 + 1] << 8) | rx[group * 3 + 2]) >> 3) & 0xFFF;

    /// <summary>One reading: pressure, then <see cref="Samples"/>+1 X/Y pairs (the first discarded).</summary>
    public TouchPoint Read()
    {
        int pairs = Samples + 1;
        int groups = 2 + pairs * 2 + 1;
        byte[] tx = new byte[groups * 3];
        tx[0] = CmdZ1;
        tx[3] = CmdZ2;
        for (int i = 0; i < pairs; i++)
        {
            tx[(2 + i * 2) * 3] = CmdX;
            tx[(3 + i * 2) * 3] = CmdY;
        }
        tx[(groups - 1) * 3] = CmdPowerDown;
        byte[] rx = _spi.Transfer(tx);

        TouchPoint p = new TouchPoint();
        p.Z1 = Value(rx, 0);
        p.Z2 = Value(rx, 1);
        int sx = 0;
        int sy = 0;
        for (int i = 1; i < pairs; i++)
        {
            sx += Value(rx, 2 + i * 2);
            sy += Value(rx, 3 + i * 2);
        }
        p.RawX = sx / Samples;
        p.RawY = sy / Samples;
        p.Touched = p.Z1 >= MinZ1;
        if (p.Touched)
        {
            p.ResistanceOhms = TouchCalibration.Resistance(XPlateOhms, p.RawX, p.Z1, p.Z2);
        }
        p.X = Calibration.MapX(p.RawX, p.RawY);
        p.Y = Calibration.MapY(p.RawX, p.RawY);
        return p;
    }
}

/// <summary>
/// TI TSC2004 I2C 4-wire touch controller (address 0x48..0x4B) with
/// on-chip averaging/median filters. Register access as in the Linux
/// tsc2004 driver: register address in bits 6:3, read flag 0x01, write
/// flag (PND0) 0x02, 16-bit big-endian values; converter commands have
/// bit 7 set (0x84 = scan X/Y/Z1/Z2 at 12 bits, 0x81 stop, 0x82 reset).
/// </summary>
public class Tsc2004
{
    public const int DefaultAddress = 0x48;

    public const int RegX = 0x0;
    public const int RegY = 0x1;
    public const int RegZ1 = 0x2;
    public const int RegZ2 = 0x3;
    public const int RegAux = 0x4;
    public const int RegTemp1 = 0x5;
    public const int RegTemp2 = 0x6;
    public const int RegStatus = 0x7;
    public const int RegCfr0 = 0xC;
    public const int RegCfr1 = 0xD;
    public const int RegCfr2 = 0xE;

    private const int ReadFlag = 0x01;
    private const int WriteFlag = 0x02;
    private const int Cmd = 0x80;
    private const int Cmd12Bit = 0x04;
    private const int CmdStop = 0x01;
    private const int CmdSwReset = 0x02;

    /// <summary>CFR0: pen-initiated mode, 12-bit, 1 MHz clock, 1 ms stabilisation, 276 µs precharge (Linux defaults).</summary>
    public const int Cfr0Default = 0xABA0;

    /// <summary>CFR1: 4 ms batch delay.</summary>
    public const int Cfr1Default = 0x0003;

    /// <summary>CFR2: pen interrupt, X/Y/Z median-average filter (15-sample median, 7-sample average).</summary>
    public const int Cfr2Default = 0xB01C;

    private readonly I2cDevice _dev;

    public TouchCalibration Calibration = new TouchCalibration();
    public double XPlateOhms = 400;

    public Tsc2004(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Tsc2004(I2cDevice device)
    {
        _dev = device;
    }

    public void Command(int function) => _dev.WriteByte(Cmd | Cmd12Bit | function);

    public int ReadRegister(int reg)
    {
        byte[] a = new byte[1];
        a[0] = (byte)((reg << 3) | ReadFlag);
        byte[] d = _dev.WriteRead(a, 2);
        return (d[0] << 8) | d[1];
    }

    public void WriteRegister(int reg, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)((reg << 3) | WriteFlag);
        b[1] = (byte)(value >> 8);
        b[2] = (byte)value;
        _dev.Write(b);
    }

    /// <summary>Reset, configure (CFR0..2) and start pen-triggered X/Y/Z scans.</summary>
    public void Begin()
    {
        Command(CmdSwReset);
        Timing.SleepMs(1);
        Command(CmdStop);
        WriteRegister(RegCfr0, Cfr0Default);
        WriteRegister(RegCfr1, Cfr1Default);
        WriteRegister(RegCfr2, Cfr2Default);
        Command(0x00); // scan X, Y, Z1, Z2
    }

    /// <summary>Read the latest X, Y, Z1, Z2 conversion. Not touched when Z1 is 0.</summary>
    public TouchPoint Read()
    {
        TouchPoint p = new TouchPoint();
        p.RawX = ReadRegister(RegX) & 0xFFF;
        p.RawY = ReadRegister(RegY) & 0xFFF;
        p.Z1 = ReadRegister(RegZ1) & 0xFFF;
        p.Z2 = ReadRegister(RegZ2) & 0xFFF;
        p.Touched = p.Z1 != 0;
        if (p.Touched)
        {
            p.ResistanceOhms = TouchCalibration.Resistance(XPlateOhms, p.RawX, p.Z1, p.Z2);
        }
        p.X = Calibration.MapX(p.RawX, p.RawY);
        p.Y = Calibration.MapY(p.RawX, p.RawY);
        return p;
    }
}
