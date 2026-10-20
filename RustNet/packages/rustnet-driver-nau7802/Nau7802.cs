using RustNet.Community.Bus;

namespace RustNet.Community.Nau7802;

/// <summary>
/// Nuvoton NAU7802 24-bit bridge-sensor (load cell) ADC at I2C 0x2A, with
/// on-chip LDO and PGA (gain 1..128). Follows the datasheet power-on
/// sequence: reset, power up digital (PUD) and wait for power-up ready
/// (PUR), power up analog (PUA), set LDO/gain/rate, disable the clock
/// chopper (ADC register 0x15 = 0x30), enable the PGA output capacitor,
/// run the internal offset calibration and start conversions (CS).
/// </summary>
public class Nau7802
{
    public const int DefaultAddress = 0x2A;

    // CTRL1 VLDO codes.
    public const int Ldo4V5 = 0;
    public const int Ldo4V2 = 1;
    public const int Ldo3V9 = 2;
    public const int Ldo3V6 = 3;
    public const int Ldo3V3 = 4;
    public const int Ldo3V0 = 5;
    public const int Ldo2V7 = 6;
    public const int Ldo2V4 = 7;

    // CTRL2 CRS codes.
    public const int Rate10 = 0;
    public const int Rate20 = 1;
    public const int Rate40 = 2;
    public const int Rate80 = 3;
    public const int Rate320 = 7;

    private const int RegPuCtrl = 0x00;
    private const int RegCtrl1 = 0x01;
    private const int RegCtrl2 = 0x02;
    private const int RegAdcoB2 = 0x12;
    private const int RegAdc = 0x15;
    private const int RegPgaPwr = 0x1C;
    private const int RegRevisionId = 0x1F;

    private const int PuRr = 0x01;
    private const int PuPud = 0x02;
    private const int PuPua = 0x04;
    private const int PuPur = 0x08;
    private const int PuCs = 0x10;
    private const int PuCr = 0x20;
    private const int PuAvdds = 0x80;

    private readonly I2cDevice _dev;

    public int Ldo = Ldo3V3;

    /// <summary>PGA gain as a power of two: 0 = ×1 .. 7 = ×128.</summary>
    public int GainCode = 7;

    public int Rate = Rate80;

    /// <summary>Raw value treated as zero load.</summary>
    public long Offset;

    /// <summary>Raw counts per unit of weight.</summary>
    public double Scale = 1.0;

    public int ReadyPolls = 200;

    public Nau7802(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Nau7802(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Run the power-on sequence and internal calibration. False if the chip does not answer as a NAU7802.</summary>
    public bool Begin()
    {
        if ((_dev.ReadRegister(RegRevisionId) & 0x0F) != 0x0F)
        {
            return false;
        }
        _dev.WriteRegister(RegPuCtrl, PuRr);
        Timing.SleepMs(1);
        _dev.WriteRegister(RegPuCtrl, PuPud);
        int polls = 0;
        while ((_dev.ReadRegister(RegPuCtrl) & PuPur) == 0)
        {
            if (++polls > ReadyPolls)
            {
                return false;
            }
            Timing.SleepMs(1);
        }
        _dev.WriteRegister(RegPuCtrl, PuPud | PuPua | PuAvdds);
        _dev.WriteRegister(RegCtrl1, ((Ldo & 7) << 3) | (GainCode & 7));
        _dev.WriteRegister(RegCtrl2, (Rate & 7) << 4);
        _dev.WriteRegister(RegAdc, 0x30); // CLK_CHP off
        _dev.UpdateRegister(RegPgaPwr, 0x80, 0x80); // PGA output bypass capacitor
        Timing.SleepMs(250); // LDO and analog settle
        if (!CalibrateOffset())
        {
            return false;
        }
        _dev.WriteRegister(RegPuCtrl, PuPud | PuPua | PuAvdds | PuCs);
        return true;
    }

    /// <summary>Internal offset calibration (CALMOD = 00, CALS), waiting for completion. False on CAL_ERR.</summary>
    public bool CalibrateOffset()
    {
        int c2 = _dev.ReadRegister(RegCtrl2) & 0xF0;
        _dev.WriteRegister(RegCtrl2, c2 | 0x04);
        int polls = 0;
        while ((_dev.ReadRegister(RegCtrl2) & 0x04) != 0)
        {
            if (++polls > ReadyPolls)
            {
                return false;
            }
            Timing.SleepMs(1);
        }
        return (_dev.ReadRegister(RegCtrl2) & 0x08) == 0;
    }

    /// <summary>Select input channel 1 or 2 (CTRL2.CHS); recalibrate after switching.</summary>
    public void SelectChannel(int channel) => _dev.UpdateRegister(RegCtrl2, 0x80, channel == 2 ? 0x80 : 0x00);

    /// <summary>Cycle ready (PU_CTRL.CR).</summary>
    public bool IsReady() => (_dev.ReadRegister(RegPuCtrl) & PuCr) != 0;

    /// <summary>Wait for a conversion and return it as a signed 24-bit value.</summary>
    public int ReadRaw()
    {
        int polls = 0;
        while (!IsReady())
        {
            if (++polls > ReadyPolls)
            {
                throw new InvalidOperationException("nau7802 not ready");
            }
            Timing.SleepMs(1);
        }
        byte[] d = _dev.ReadRegisters(RegAdcoB2, 3);
        return BitOps.SignExtend((d[0] << 16) | (d[1] << 8) | d[2], 24);
    }

    public long ReadAverage(int times)
    {
        long sum = 0;
        for (int i = 0; i < times; i++)
        {
            sum += ReadRaw();
        }
        return sum / times;
    }

    public void Tare(int times) => Offset = ReadAverage(times);

    public void CalibrateScale(double knownWeight, int times) => Scale = (ReadAverage(times) - Offset) / knownWeight;

    public double ReadWeight(int times) => (ReadAverage(times) - Offset) / Scale;

    /// <summary>Input voltage in mV for a raw value: full scale ±0.5·VREF/gain over ±2^23 counts.</summary>
    public double RawToMillivolts(int raw, double vrefMillivolts) => raw * (0.5 * vrefMillivolts / (1 << GainCode)) / 8388608.0;
}
