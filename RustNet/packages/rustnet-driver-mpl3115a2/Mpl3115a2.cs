using RustNet.Community.Bus;

namespace RustNet.Community.Mpl3115a2;

/// <summary>
/// NXP MPL3115A2 precision pressure / altitude / temperature sensor at I2C
/// address 0x60. Uses one-shot (OST) conversions. Pressure is reported in
/// Q18.2 Pa, altitude in Q16.4 m and temperature in Q8.4 °C.
/// </summary>
public class Mpl3115a2
{
    public const int DefaultAddress = 0x60;
    public const int WhoAmIValue = 0xC4;

    private const int RegStatus = 0x00;
    private const int RegOutPMsb = 0x01;
    private const int RegWhoAmI = 0x0C;
    private const int RegPtDataCfg = 0x13;
    private const int RegBarInMsb = 0x14;
    private const int RegOffP = 0x2B;
    private const int RegOffT = 0x2C;
    private const int RegOffH = 0x2D;
    private const int RegCtrl1 = 0x26;

    private const int Ctrl1Alt = 0x80;
    private const int Ctrl1Reset = 0x04;
    private const int Ctrl1Ost = 0x02;

    /// <summary>STATUS bit 3: new pressure/altitude and temperature data ready.</summary>
    public const int StatusPtdr = 0x08;

    private readonly I2cDevice _dev;

    /// <summary>Oversampling code 0..7 (ratio 2^code: 1..128, conversion time 6..512 ms).</summary>
    public int Oversampling = 7;

    /// <summary>Last pressure in Pa.</summary>
    public double PressurePascal;

    /// <summary>Last altitude in m (relative to the sea-level pressure in BAR_IN).</summary>
    public double AltitudeMeters;

    /// <summary>Last temperature in °C.</summary>
    public double TemperatureCelsius;

    public Mpl3115a2(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Mpl3115a2(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check WHO_AM_I (0xC4), reset, enable data-ready flags and set oversampling. False when no MPL3115A2 answers.</summary>
    public bool Begin()
    {
        try
        {
            if (_dev.ReadRegister(RegWhoAmI) != WhoAmIValue)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
        try
        {
            // The device does not acknowledge the write that resets it.
            _dev.WriteRegister(RegCtrl1, Ctrl1Reset);
        }
        catch (Exception)
        {
        }
        Timing.SleepMs(10);
        _dev.WriteRegister(RegCtrl1, (Oversampling & 7) << 3);
        _dev.WriteRegister(RegPtDataCfg, 0x07); // DREM | PDEFE | TDEFE
        return true;
    }

    /// <summary>One-shot conversion in barometer mode: updates pressure (Pa) and temperature (°C).</summary>
    public void MeasurePressure()
    {
        byte[] d = OneShot(false);
        int raw = (d[0] << 16) | (d[1] << 8) | d[2];
        PressurePascal = DecodePressure(raw);
        TemperatureCelsius = DecodeTemperature((d[3] << 8) | d[4]);
    }

    /// <summary>One-shot conversion in altimeter mode: updates altitude (m) and temperature (°C).</summary>
    public void MeasureAltitude()
    {
        byte[] d = OneShot(true);
        AltitudeMeters = DecodeAltitude((d[0] << 16) | (d[1] << 8) | d[2]);
        TemperatureCelsius = DecodeTemperature((d[3] << 8) | d[4]);
    }

    private byte[] OneShot(bool altimeter)
    {
        int ctrl = ((Oversampling & 7) << 3) | (altimeter ? Ctrl1Alt : 0);
        // OST is self-clearing; write it with the mode bits for this conversion.
        _dev.WriteRegister(RegCtrl1, ctrl);
        _dev.WriteRegister(RegCtrl1, ctrl | Ctrl1Ost);
        Timing.SleepMs((1 << (Oversampling & 7)) * 4 + 2);
        for (int i = 0; i < 100 && (_dev.ReadRegister(RegStatus) & StatusPtdr) == 0; i++)
        {
            Timing.SleepMs(10);
        }
        return _dev.ReadRegisters(RegOutPMsb, 5);
    }

    /// <summary>Barometer data: unsigned 20-bit Q18.2 Pa, left-justified in OUT_P (24 bits).</summary>
    public static double DecodePressure(int raw24) => (raw24 >> 4) / 4.0;

    /// <summary>Altimeter data: signed 20-bit Q16.4 m, left-justified in OUT_P (24 bits).</summary>
    public static double DecodeAltitude(int raw24) => BitOps.SignExtend(raw24 >> 4, 20) / 16.0;

    /// <summary>Temperature: signed 12-bit Q8.4 °C, left-justified in OUT_T (16 bits).</summary>
    public static double DecodeTemperature(int raw16) => BitOps.SignExtend(raw16 >> 4, 12) / 16.0;

    /// <summary>Set the sea-level reference for altitude, in Pa (stored in BAR_IN in 2 Pa units; default 101326 Pa).</summary>
    public void SetSeaLevelPressure(double pascal)
    {
        int v = (int)(pascal / 2.0 + 0.5);
        byte[] b = new byte[2];
        b[0] = (byte)(v >> 8);
        b[1] = (byte)v;
        _dev.WriteRegisters(RegBarInMsb, b);
    }

    /// <summary>User offsets: pressure in 4 Pa steps, temperature in 0.0625 °C steps, altitude in 1 m steps (each -128..127).</summary>
    public void SetOffsets(int pressureSteps, int temperatureSteps, int altitudeSteps)
    {
        _dev.WriteRegister(RegOffP, pressureSteps & 0xFF);
        _dev.WriteRegister(RegOffT, temperatureSteps & 0xFF);
        _dev.WriteRegister(RegOffH, altitudeSteps & 0xFF);
    }
}
