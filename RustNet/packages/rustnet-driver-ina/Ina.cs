using RustNet.Community.Bus;

namespace RustNet.Community.Ina;

/// <summary>Big-endian register access shared by the INA2xx parts (register pointer, then 2..5 data bytes).</summary>
internal static class InaRegs
{
    public static long ReadUnsigned(I2cDevice dev, int register, int bytes)
    {
        byte[] d = dev.ReadRegisters(register, bytes);
        long v = 0;
        for (int i = 0; i < bytes; i++)
        {
            v = (v << 8) | d[i];
        }
        return v;
    }

    public static void Write16(I2cDevice dev, int register, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)register;
        b[1] = (byte)(value >> 8);
        b[2] = (byte)value;
        dev.Write(b);
    }
}

/// <summary>
/// TI INA219 high-side current/power monitor (I2C 0x40..0x4F) with an
/// external shunt. Shunt voltage LSB 10 µV, bus voltage LSB 4 mV. Current
/// and power need the calibration register:
/// CAL = trunc(0.04096 / (Current_LSB × R_shunt)), Power_LSB = 20 × Current_LSB.
/// </summary>
public class Ina219
{
    public const int DefaultAddress = 0x40;

    /// <summary>Power-on CONFIG: 32 V range, /8 gain (±320 mV), 12-bit, continuous shunt+bus.</summary>
    public const int ConfigDefault = 0x399F;

    private const int RegConfig = 0x00;
    private const int RegShunt = 0x01;
    private const int RegBus = 0x02;
    private const int RegPower = 0x03;
    private const int RegCurrent = 0x04;
    private const int RegCalibration = 0x05;

    private readonly I2cDevice _dev;

    /// <summary>Amps per LSB of the current register (set by <see cref="Calibrate"/>).</summary>
    public double CurrentLsb;

    /// <summary>The calibration value written to register 0x05.</summary>
    public int CalibrationValue;

    /// <summary>Last bus-voltage register's conversion-ready (CNVR) and overflow (OVF) flags.</summary>
    public bool ConversionReady;
    public bool MathOverflow;

    public Ina219(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Ina219(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Reset (CONFIG.RST) and write a configuration (default <see cref="ConfigDefault"/>).</summary>
    public void Begin(int config)
    {
        InaRegs.Write16(_dev, RegConfig, 0x8000);
        InaRegs.Write16(_dev, RegConfig, config);
    }

    /// <summary>
    /// Program the calibration register for a shunt and a chosen current LSB
    /// (amps; pick ≥ max expected current / 32768, rounded up to a convenient
    /// value).
    /// </summary>
    public void Calibrate(double shuntOhms, double currentLsbAmps)
    {
        CurrentLsb = currentLsbAmps;
        CalibrationValue = (int)(0.04096 / (currentLsbAmps * shuntOhms));
        if (CalibrationValue > 0xFFFE)
        {
            CalibrationValue = 0xFFFE;
        }
        CalibrationValue &= 0xFFFE; // bit 0 is not used
        InaRegs.Write16(_dev, RegCalibration, CalibrationValue);
    }

    /// <summary>Shunt voltage in mV (10 µV/LSB, signed).</summary>
    public double ReadShuntMillivolts() => BitOps.ToInt16((int)InaRegs.ReadUnsigned(_dev, RegShunt, 2)) * 0.01;

    /// <summary>Bus voltage in V (bits 15:3, 4 mV/LSB); also updates the CNVR/OVF flags.</summary>
    public double ReadBusVolts()
    {
        int r = (int)InaRegs.ReadUnsigned(_dev, RegBus, 2);
        ConversionReady = (r & 0x02) != 0;
        MathOverflow = (r & 0x01) != 0;
        return (r >> 3) * 0.004;
    }

    /// <summary>Current in A (current register × Current_LSB).</summary>
    public double ReadCurrentAmps() => BitOps.ToInt16((int)InaRegs.ReadUnsigned(_dev, RegCurrent, 2)) * CurrentLsb;

    /// <summary>Power in W (power register × 20 × Current_LSB).</summary>
    public double ReadPowerWatts() => InaRegs.ReadUnsigned(_dev, RegPower, 2) * 20.0 * CurrentLsb;
}

/// <summary>
/// TI INA228 85 V, 20-bit power/energy/charge monitor (I2C 0x40..0x4F).
/// CURRENT_LSB = max current / 2^19; SHUNT_CAL = 13107.2·10^6 × CURRENT_LSB × R_shunt
/// (×4 with ADCRANGE = 1). Shunt LSB 312.5 nV (78.125 nV with ADCRANGE = 1),
/// bus LSB 195.3125 µV, die temperature 7.8125 m°C, power 3.2 × CURRENT_LSB,
/// energy 16 × 3.2 × CURRENT_LSB (J), charge CURRENT_LSB (C).
/// </summary>
public class Ina228
{
    public const int DefaultAddress = 0x40;
    public const int ManufacturerIdTi = 0x5449;
    public const int DeviceIdValue = 0x228;

    private const int RegConfig = 0x00;
    private const int RegAdcConfig = 0x01;
    private const int RegShuntCal = 0x02;
    private const int RegVShunt = 0x04;
    private const int RegVBus = 0x05;
    private const int RegDieTemp = 0x06;
    private const int RegCurrent = 0x07;
    private const int RegPower = 0x08;
    private const int RegEnergy = 0x09;
    private const int RegCharge = 0x0A;
    private const int RegDiagAlrt = 0x0B;
    private const int RegManufacturerId = 0x3E;
    private const int RegDeviceId = 0x3F;

    private readonly I2cDevice _dev;

    public double CurrentLsb;
    public int ShuntCalValue;

    /// <summary>True for the ±40.96 mV shunt range (ADCRANGE = 1), false for ±163.84 mV.</summary>
    public bool LowShuntRange;

    public Ina228(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Ina228(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check the TI manufacturer ID and the 0x228 device ID, then reset. False if absent.</summary>
    public bool Begin()
    {
        if (InaRegs.ReadUnsigned(_dev, RegManufacturerId, 2) != ManufacturerIdTi)
        {
            return false;
        }
        if ((InaRegs.ReadUnsigned(_dev, RegDeviceId, 2) >> 4) != DeviceIdValue)
        {
            return false;
        }
        InaRegs.Write16(_dev, RegConfig, 0x8000); // RST
        Timing.SleepMs(1);
        // Continuous bus+shunt+temperature, 1052 µs conversions, 1 sample (power-on ADC_CONFIG).
        InaRegs.Write16(_dev, RegAdcConfig, 0xFB68);
        return true;
    }

    /// <summary>Calibrate for a shunt and the largest expected current (A).</summary>
    public void Calibrate(double shuntOhms, double maxCurrentAmps, bool lowShuntRange)
    {
        LowShuntRange = lowShuntRange;
        InaRegs.Write16(_dev, RegConfig, lowShuntRange ? 0x0010 : 0x0000);
        CurrentLsb = maxCurrentAmps / 524288.0;
        double cal = 13107.2e6 * CurrentLsb * shuntOhms;
        if (lowShuntRange)
        {
            cal *= 4;
        }
        ShuntCalValue = (int)cal & 0x7FFF;
        InaRegs.Write16(_dev, RegShuntCal, ShuntCalValue);
    }

    private static long Signed20(long reg24) => BitOps.SignExtend((int)(reg24 >> 4), 20);

    /// <summary>Shunt voltage in mV.</summary>
    public double ReadShuntMillivolts() => Signed20(InaRegs.ReadUnsigned(_dev, RegVShunt, 3)) * (LowShuntRange ? 78.125e-6 : 312.5e-6);

    /// <summary>Bus voltage in V.</summary>
    public double ReadBusVolts() => (InaRegs.ReadUnsigned(_dev, RegVBus, 3) >> 4) * 195.3125e-6;

    /// <summary>Die temperature in °C.</summary>
    public double ReadDieTemperatureCelsius() => BitOps.ToInt16((int)InaRegs.ReadUnsigned(_dev, RegDieTemp, 2)) * 7.8125e-3;

    /// <summary>Current in A.</summary>
    public double ReadCurrentAmps() => Signed20(InaRegs.ReadUnsigned(_dev, RegCurrent, 3)) * CurrentLsb;

    /// <summary>Power in W.</summary>
    public double ReadPowerWatts() => InaRegs.ReadUnsigned(_dev, RegPower, 3) * 3.2 * CurrentLsb;

    /// <summary>Accumulated energy in J (40-bit).</summary>
    public double ReadEnergyJoules() => InaRegs.ReadUnsigned(_dev, RegEnergy, 5) * 16.0 * 3.2 * CurrentLsb;

    /// <summary>Accumulated charge in C (40-bit two's complement).</summary>
    public double ReadChargeCoulombs()
    {
        long v = InaRegs.ReadUnsigned(_dev, RegCharge, 5);
        if ((v & 0x8000000000L) != 0)
        {
            v -= 0x10000000000L;
        }
        return v * CurrentLsb;
    }

    /// <summary>Clear the energy and charge accumulators (CONFIG.RSTACC).</summary>
    public void ResetAccumulators() => InaRegs.Write16(_dev, RegConfig, 0x4000 | (LowShuntRange ? 0x0010 : 0));

    /// <summary>DIAG_ALRT flags (bit 1 CNVRF conversion ready, bit 0 MEMSTAT…).</summary>
    public int ReadDiagnostics() => (int)InaRegs.ReadUnsigned(_dev, RegDiagAlrt, 2);
}

/// <summary>
/// TI INA260 current/voltage/power monitor with an integrated 2 mΩ shunt
/// (I2C 0x40..0x4F): current 1.25 mA/LSB (signed), bus 1.25 mV/LSB, power
/// 10 mW/LSB. No calibration needed.
/// </summary>
public class Ina260
{
    public const int DefaultAddress = 0x40;
    public const int ManufacturerIdTi = 0x5449;
    public const int DieIdValue = 0x227;

    private const int RegConfig = 0x00;
    private const int RegCurrent = 0x01;
    private const int RegBus = 0x02;
    private const int RegPower = 0x03;
    private const int RegManufacturerId = 0xFE;
    private const int RegDieId = 0xFF;

    private readonly I2cDevice _dev;

    public Ina260(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Ina260(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check the TI and die IDs, reset, and set continuous current+voltage with 1.1 ms conversions.</summary>
    public bool Begin()
    {
        if (InaRegs.ReadUnsigned(_dev, RegManufacturerId, 2) != ManufacturerIdTi)
        {
            return false;
        }
        if ((InaRegs.ReadUnsigned(_dev, RegDieId, 2) >> 4) != DieIdValue)
        {
            return false;
        }
        InaRegs.Write16(_dev, RegConfig, 0x8000);
        InaRegs.Write16(_dev, RegConfig, 0x6127); // power-on default
        return true;
    }

    /// <summary>Set CONFIG averaging (AVG bits 11:9: 0 = 1 .. 7 = 1024 samples), keeping continuous mode.</summary>
    public void SetAveraging(int avgCode) => InaRegs.Write16(_dev, RegConfig, 0x6000 | ((avgCode & 7) << 9) | 0x0127);

    /// <summary>Current in A.</summary>
    public double ReadCurrentAmps() => BitOps.ToInt16((int)InaRegs.ReadUnsigned(_dev, RegCurrent, 2)) * 0.00125;

    /// <summary>Bus voltage in V.</summary>
    public double ReadBusVolts() => InaRegs.ReadUnsigned(_dev, RegBus, 2) * 0.00125;

    /// <summary>Power in W.</summary>
    public double ReadPowerWatts() => InaRegs.ReadUnsigned(_dev, RegPower, 2) * 0.010;
}
