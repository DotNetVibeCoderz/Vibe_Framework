using RustNet.Community.Bus;

namespace RustNet.Community.Ads1x15;

/// <summary>
/// TI ADS1015 (12-bit, up to 3300 SPS) and ADS1115 (16-bit, up to 860 SPS)
/// four-channel I2C ADCs. Registers: 0 conversion, 1 config, 2 Lo_thresh,
/// 3 Hi_thresh (16-bit, MSB first). Config register bits:
/// <code>
/// 15    OS        write 1: start a single conversion; read 1: idle
/// 14:12 MUX       000 AIN0-AIN1 … 011 AIN2-AIN3, 100..111 AINx-GND
/// 11:9  PGA       000 ±6.144 V, 001 ±4.096, 010 ±2.048, 011 ±1.024, 100 ±0.512, 101+ ±0.256
/// 8     MODE      1 single-shot
/// 7:5   DR        data rate (table per chip)
/// 4..0  comparator: mode, polarity, latch, queue (11 = disabled)
/// </code>
/// One LSB is FSR/32768 on the ADS1115 and FSR/2048 on the ADS1015, whose
/// 12-bit result is left-justified in the conversion register.
/// Address 0x48 (ADDR to GND), 0x49 VDD, 0x4A SDA, 0x4B SCL.
/// </summary>
public class Ads1x15
{
    public const int DefaultAddress = 0x48;

    public const int RegConversion = 0;
    public const int RegConfig = 1;
    public const int RegLoThresh = 2;
    public const int RegHiThresh = 3;

    // MUX settings (config bits 14:12).
    public const int MuxDiff01 = 0;
    public const int MuxDiff03 = 1;
    public const int MuxDiff13 = 2;
    public const int MuxDiff23 = 3;
    public const int MuxSingle0 = 4;
    public const int MuxSingle1 = 5;
    public const int MuxSingle2 = 6;
    public const int MuxSingle3 = 7;

    // PGA settings (config bits 11:9).
    public const int Pga6144 = 0;
    public const int Pga4096 = 1;
    public const int Pga2048 = 2;
    public const int Pga1024 = 3;
    public const int Pga512 = 4;
    public const int Pga256 = 5;

    private readonly I2cDevice _dev;

    /// <summary>True for the 12-bit ADS1015.</summary>
    public bool Is12Bit;

    /// <summary>PGA setting used for conversions (default ±2.048 V, the power-on value).</summary>
    public int Gain = Pga2048;

    /// <summary>Data-rate code 0..7 (bits 7:5); default 100 = 128 SPS (ADS1115) / 1600 SPS (ADS1015).</summary>
    public int DataRate = 4;

    /// <summary>Last config word written.</summary>
    public int LastConfig;

    protected Ads1x15(I2cDevice device, bool is12Bit)
    {
        _dev = device;
        Is12Bit = is12Bit;
    }

    /// <summary>Full-scale range in millivolts for a PGA code.</summary>
    public static int FullScaleMillivolts(int pga)
    {
        if (pga == Pga6144)
        {
            return 6144;
        }
        if (pga == Pga4096)
        {
            return 4096;
        }
        if (pga == Pga2048)
        {
            return 2048;
        }
        if (pga == Pga1024)
        {
            return 1024;
        }
        if (pga == Pga512)
        {
            return 512;
        }
        return 256;
    }

    /// <summary>Samples per second of a data-rate code on this chip.</summary>
    public int SamplesPerSecond(int dataRate)
    {
        int d = dataRate & 7;
        if (Is12Bit)
        {
            if (d == 0) { return 128; }
            if (d == 1) { return 250; }
            if (d == 2) { return 490; }
            if (d == 3) { return 920; }
            if (d == 4) { return 1600; }
            if (d == 5) { return 2400; }
            return 3300;
        }
        if (d == 0) { return 8; }
        if (d == 1) { return 16; }
        if (d == 2) { return 32; }
        if (d == 3) { return 64; }
        if (d == 4) { return 128; }
        if (d == 5) { return 250; }
        if (d == 6) { return 475; }
        return 860;
    }

    /// <summary>Config word for a single-shot conversion, comparator disabled.</summary>
    public int SingleShotConfig(int mux, int pga, int dataRate)
        => 0x8000 | ((mux & 7) << 12) | ((pga & 7) << 9) | 0x0100 | ((dataRate & 7) << 5) | 0x0003;

    private void WriteRegister16(int register, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)register;
        b[1] = (byte)(value >> 8);
        b[2] = (byte)value;
        _dev.Write(b);
    }

    /// <summary>Start a single-shot conversion on <paramref name="mux"/>, wait for it and return the signed result in LSBs.</summary>
    public int ReadRaw(int mux)
    {
        LastConfig = SingleShotConfig(mux, Gain, DataRate);
        WriteRegister16(RegConfig, LastConfig);
        int sps = SamplesPerSecond(DataRate);
        Timing.SleepMs(1000 / sps + 1);
        for (int i = 0; i < 20; i++)
        {
            if ((_dev.ReadUInt16BigEndian(RegConfig) & 0x8000) != 0)
            {
                break;
            }
            Timing.SleepMs(1);
        }
        return ReadLastConversion();
    }

    /// <summary>The conversion register, sign-extended (and shifted down 4 bits on the ADS1015).</summary>
    public int ReadLastConversion()
    {
        int v = _dev.ReadInt16BigEndian(RegConversion);
        return Is12Bit ? v >> 4 : v;
    }

    /// <summary>Value of one LSB in microvolts at a PGA setting.</summary>
    public double LsbMicrovolts(int pga) => FullScaleMillivolts(pga) * 1000.0 / (Is12Bit ? 2048.0 : 32768.0);

    /// <summary>Convert a raw result at the current gain to millivolts.</summary>
    public double ToMillivolts(int raw) => raw * LsbMicrovolts(Gain) / 1000.0;

    /// <summary>Single-ended reading of AIN0..AIN3 in millivolts.</summary>
    public double ReadSingleEndedMillivolts(int channel) => ToMillivolts(ReadRaw(MuxSingle0 + (channel & 3)));

    /// <summary>Differential reading (MuxDiff* code) in millivolts.</summary>
    public double ReadDifferentialMillivolts(int mux) => ToMillivolts(ReadRaw(mux));

    /// <summary>Comparator thresholds in raw counts (ADS1015 values are shifted into bits 15:4).</summary>
    public void SetThresholds(int low, int high)
    {
        int shift = Is12Bit ? 4 : 0;
        WriteRegister16(RegLoThresh, (low << shift) & 0xFFFF);
        WriteRegister16(RegHiThresh, (high << shift) & 0xFFFF);
    }
}

/// <summary>TI ADS1015: 12-bit, 128..3300 SPS.</summary>
public class Ads1015 : Ads1x15
{
    public Ads1015(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Ads1015(I2cDevice device) : base(device, true)
    {
    }
}

/// <summary>TI ADS1115: 16-bit, 8..860 SPS.</summary>
public class Ads1115 : Ads1x15
{
    public Ads1115(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Ads1115(I2cDevice device) : base(device, false)
    {
    }
}
