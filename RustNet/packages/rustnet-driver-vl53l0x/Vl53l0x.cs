using RustNet.Community.Bus;

namespace RustNet.Community.Vl53l0x;

/// <summary>
/// ST VL53L0X time-of-flight ranging sensor over I2C (default address 0x29).
/// The initialisation is ST's VL53L0X API (DataInit, StaticInit with the
/// default tuning settings, reference SPAD management, timing budget and the
/// VHV/phase reference calibrations) as condensed by Pololu's VL53L0X
/// Arduino library, which is what this is a port of. Distances in mm.
/// </summary>
public class Vl53l0x
{
    public const int DefaultAddress = 0x29;
    public const int ModelId = 0xEE;

    private const int SysrangeStart = 0x00;
    private const int SystemSequenceConfig = 0x01;
    private const int SystemIntermeasurementPeriod = 0x04;
    private const int SystemInterruptConfigGpio = 0x0A;
    private const int SystemInterruptClear = 0x0B;
    private const int ResultInterruptStatus = 0x13;
    private const int ResultRangeStatus = 0x14;
    private const int FinalRangeConfigMinCountRateRtnLimit = 0x44;
    private const int MsrcConfigTimeoutMacrop = 0x46;
    private const int PreRangeConfigVcselPeriod = 0x50;
    private const int PreRangeConfigTimeoutMacropHi = 0x51;
    private const int MsrcConfigControl = 0x60;
    private const int FinalRangeConfigVcselPeriod = 0x70;
    private const int FinalRangeConfigTimeoutMacropHi = 0x71;
    private const int GpioHvMuxActiveHigh = 0x84;
    private const int VhvConfigPadSclSdaExtsupHv = 0x89;
    private const int I2cSlaveDeviceAddress = 0x8A;
    private const int GlobalConfigSpadEnablesRef0 = 0xB0;
    private const int GlobalConfigRefEnStartSelect = 0xB6;
    private const int DynamicSpadNumRequestedRefSpad = 0x4E;
    private const int DynamicSpadRefEnStartOffset = 0x4F;
    private const int IdentificationModelId = 0xC0;
    private const int OscCalibrateVal = 0xF8;

    // ST's DefaultTuningSettings as (register, value) pairs, 0xFF selecting the page.
    private const string TuningSettings =
        "FF01 0000 FF00 0900 1000 1100 2401 25FF 7500 FF01 4E2C 4800 3020 FF00 3009 5400 3104 3203 4083 4625 6000 2700 " +
        "5006 5100 5296 5608 5730 6100 6200 6400 6500 66A0 FF01 2232 4714 49FF 4A00 FF00 7A0A 7B00 7821 FF01 2334 4200 " +
        "44FF 4526 4605 4040 0E06 201A 4340 FF00 3403 3544 FF01 3104 4B09 4C05 4D04 FF00 4400 4520 4708 4828 6700 7004 " +
        "7101 72FE 7600 7700 FF01 0D01 FF00 8001 01F8 FF01 8E01 0001 FF00 8000";

    private readonly I2cDevice _dev;

    private int _stopVariable;
    private long _timingBudgetUs;

    /// <summary>Polling attempts (each after a 1 ms sleep) before a measurement is declared timed out.</summary>
    public int TimeoutPolls = 500;

    /// <summary>Set when the last operation gave up waiting on the sensor.</summary>
    public bool TimedOut;

    /// <summary>Reference SPAD count and type read from the NVM during init.</summary>
    public int SpadCount;
    public bool SpadTypeIsAperture;

    public Vl53l0x(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Vl53l0x(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Vl53l0x(I2cDevice device)
    {
        _dev = device;
    }

    private void W(int reg, int value) => _dev.WriteRegister(reg, value);

    private int R(int reg) => _dev.ReadRegister(reg);

    private void W16(int reg, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)reg;
        b[1] = (byte)(value >> 8);
        b[2] = (byte)value;
        _dev.Write(b);
    }

    private int R16(int reg) => _dev.ReadUInt16BigEndian(reg);

    /// <summary>Current measurement timing budget in µs (≈33 ms after init).</summary>
    public long TimingBudgetMicroseconds => _timingBudgetUs;

    /// <summary>
    /// Full sensor initialisation. <paramref name="io2v8"/> selects 2.8 V I/O
    /// (the breakouts with a level shifter); otherwise 1.8 V. Returns false if
    /// the model ID is wrong or a calibration step times out.
    /// </summary>
    public bool Begin(bool io2v8)
    {
        if (R(IdentificationModelId) != ModelId)
        {
            return false;
        }
        if (io2v8)
        {
            W(VhvConfigPadSclSdaExtsupHv, R(VhvConfigPadSclSdaExtsupHv) | 0x01);
        }
        // "Set I2C standard mode".
        W(0x88, 0x00);
        W(0x80, 0x01);
        W(0xFF, 0x01);
        W(0x00, 0x00);
        _stopVariable = R(0x91);
        W(0x00, 0x01);
        W(0xFF, 0x00);
        W(0x80, 0x00);

        // Disable the SIGNAL_RATE_MSRC and SIGNAL_RATE_PRE_RANGE limit checks.
        W(MsrcConfigControl, R(MsrcConfigControl) | 0x12);
        SetSignalRateLimit(0.25);
        W(SystemSequenceConfig, 0xFF);

        // StaticInit: reference SPADs.
        if (!GetSpadInfo())
        {
            return false;
        }
        byte[] spadMap = _dev.ReadRegisters(GlobalConfigSpadEnablesRef0, 6);
        W(0xFF, 0x01);
        W(DynamicSpadRefEnStartOffset, 0x00);
        W(DynamicSpadNumRequestedRefSpad, 0x2C);
        W(0xFF, 0x00);
        W(GlobalConfigRefEnStartSelect, 0xB4);
        int first = SpadTypeIsAperture ? 12 : 0; // 12 is the first aperture SPAD
        int enabled = 0;
        for (int i = 0; i < 48; i++)
        {
            if (i < first || enabled == SpadCount)
            {
                spadMap[i / 8] = (byte)(spadMap[i / 8] & ~(1 << (i % 8)));
            }
            else if (((spadMap[i / 8] >> (i % 8)) & 0x1) != 0)
            {
                enabled++;
            }
        }
        _dev.WriteRegisters(GlobalConfigSpadEnablesRef0, spadMap);

        LoadTuningSettings();

        // Interrupt on new sample ready, active low.
        W(SystemInterruptConfigGpio, 0x04);
        W(GpioHvMuxActiveHigh, R(GpioHvMuxActiveHigh) & ~0x10);
        W(SystemInterruptClear, 0x01);

        _timingBudgetUs = GetMeasurementTimingBudget();
        // Disable MSRC and TCC by default.
        W(SystemSequenceConfig, 0xE8);
        SetMeasurementTimingBudget(_timingBudgetUs);

        // VHV and phase reference calibration.
        W(SystemSequenceConfig, 0x01);
        if (!SingleRefCalibration(0x40))
        {
            return false;
        }
        W(SystemSequenceConfig, 0x02);
        if (!SingleRefCalibration(0x00))
        {
            return false;
        }
        W(SystemSequenceConfig, 0xE8);
        return true;
    }

    private void LoadTuningSettings()
    {
        string t = TuningSettings;
        int i = 0;
        while (i + 3 < t.Length)
        {
            if (t[i] == ' ')
            {
                i++;
                continue;
            }
            int reg = Nibble(t[i]) * 16 + Nibble(t[i + 1]);
            int val = Nibble(t[i + 2]) * 16 + Nibble(t[i + 3]);
            W(reg, val);
            i += 4;
        }
    }

    private static int Nibble(char c) => c <= '9' ? c - '0' : c - 'A' + 10;

    /// <summary>Change the I2C address (lost at power-off).</summary>
    public void SetAddress(int newAddress)
    {
        W(I2cSlaveDeviceAddress, newAddress & 0x7F);
        _dev.Address = newAddress;
    }

    /// <summary>Return signal rate limit in MCPS (Q9.7; default 0.25). Lower extends range at the cost of accuracy.</summary>
    public bool SetSignalRateLimit(double mcps)
    {
        if (mcps < 0 || mcps > 511.99)
        {
            return false;
        }
        W16(FinalRangeConfigMinCountRateRtnLimit, (int)(mcps * 128));
        return true;
    }

    private bool GetSpadInfo()
    {
        W(0x80, 0x01);
        W(0xFF, 0x01);
        W(0x00, 0x00);
        W(0xFF, 0x06);
        W(0x83, R(0x83) | 0x04);
        W(0xFF, 0x07);
        W(0x81, 0x01);
        W(0x80, 0x01);
        W(0x94, 0x6B);
        W(0x83, 0x00);
        int polls = 0;
        while (R(0x83) == 0x00)
        {
            if (++polls > TimeoutPolls)
            {
                TimedOut = true;
                return false;
            }
            Timing.SleepMs(1);
        }
        W(0x83, 0x01);
        int tmp = R(0x92);
        SpadCount = tmp & 0x7F;
        SpadTypeIsAperture = ((tmp >> 7) & 0x01) != 0;
        W(0x81, 0x00);
        W(0xFF, 0x06);
        W(0x83, R(0x83) & ~0x04);
        W(0xFF, 0x01);
        W(0x00, 0x01);
        W(0xFF, 0x00);
        W(0x80, 0x00);
        return true;
    }

    private bool SingleRefCalibration(int vhvInitByte)
    {
        W(SysrangeStart, 0x01 | vhvInitByte);
        int polls = 0;
        while ((R(ResultInterruptStatus) & 0x07) == 0)
        {
            if (++polls > TimeoutPolls)
            {
                TimedOut = true;
                return false;
            }
            Timing.SleepMs(1);
        }
        W(SystemInterruptClear, 0x01);
        W(SysrangeStart, 0x00);
        return true;
    }

    // ---- Timing budget (ST API VL53L0X_get/set_measurement_timing_budget_micro_seconds) ----

    /// <summary>VCSEL period in PCLKs from its register encoding.</summary>
    public static int DecodeVcselPeriod(int regVal) => (regVal + 1) << 1;

    /// <summary>Macro period in ns for a VCSEL period in PCLKs.</summary>
    public static long CalcMacroPeriod(int vcselPeriodPclks) => (2304L * vcselPeriodPclks * 1655 + 500) / 1000;

    /// <summary>Register encoding "(LSByte * 2^MSByte) + 1" to MCLKs.</summary>
    public static long DecodeTimeout(int regVal) => ((long)(regVal & 0xFF) << ((regVal >> 8) & 0xFF)) + 1;

    public static int EncodeTimeout(long timeoutMclks)
    {
        if (timeoutMclks <= 0)
        {
            return 0;
        }
        long ls = timeoutMclks - 1;
        int ms = 0;
        while ((ls & 0xFFFFFF00) > 0)
        {
            ls >>= 1;
            ms++;
        }
        return (ms << 8) | (int)(ls & 0xFF);
    }

    public static long TimeoutMclksToMicroseconds(long mclks, int vcselPeriodPclks)
    {
        long macroNs = CalcMacroPeriod(vcselPeriodPclks);
        return (mclks * macroNs + 500) / 1000;
    }

    public static long TimeoutMicrosecondsToMclks(long us, int vcselPeriodPclks)
    {
        long macroNs = CalcMacroPeriod(vcselPeriodPclks);
        return (us * 1000 + macroNs / 2) / macroNs;
    }

    private class Steps
    {
        public bool Tcc, Msrc, Dss, PreRange, FinalRange;
        public int PreRangeVcselPclks, FinalRangeVcselPclks;
        public long MsrcDssTccMclks, PreRangeMclks, FinalRangeMclks;
        public long MsrcDssTccUs, PreRangeUs, FinalRangeUs;
    }

    private Steps ReadSteps()
    {
        Steps s = new Steps();
        int cfg = R(SystemSequenceConfig);
        s.Tcc = ((cfg >> 4) & 1) != 0;
        s.Dss = ((cfg >> 3) & 1) != 0;
        s.Msrc = ((cfg >> 2) & 1) != 0;
        s.PreRange = ((cfg >> 6) & 1) != 0;
        s.FinalRange = ((cfg >> 7) & 1) != 0;
        s.PreRangeVcselPclks = DecodeVcselPeriod(R(PreRangeConfigVcselPeriod));
        s.MsrcDssTccMclks = R(MsrcConfigTimeoutMacrop) + 1;
        s.MsrcDssTccUs = TimeoutMclksToMicroseconds(s.MsrcDssTccMclks, s.PreRangeVcselPclks);
        s.PreRangeMclks = DecodeTimeout(R16(PreRangeConfigTimeoutMacropHi));
        s.PreRangeUs = TimeoutMclksToMicroseconds(s.PreRangeMclks, s.PreRangeVcselPclks);
        s.FinalRangeVcselPclks = DecodeVcselPeriod(R(FinalRangeConfigVcselPeriod));
        s.FinalRangeMclks = DecodeTimeout(R16(FinalRangeConfigTimeoutMacropHi));
        if (s.PreRange)
        {
            s.FinalRangeMclks -= s.PreRangeMclks;
        }
        s.FinalRangeUs = TimeoutMclksToMicroseconds(s.FinalRangeMclks, s.FinalRangeVcselPclks);
        return s;
    }

    private static long FixedOverheads(Steps s, long budget)
    {
        if (s.Tcc)
        {
            budget += s.MsrcDssTccUs + 590;
        }
        if (s.Dss)
        {
            budget += 2 * (s.MsrcDssTccUs + 690);
        }
        else if (s.Msrc)
        {
            budget += s.MsrcDssTccUs + 660;
        }
        if (s.PreRange)
        {
            budget += s.PreRangeUs + 660;
        }
        return budget;
    }

    /// <summary>The measurement timing budget the current sequence and timeouts add up to, in µs.</summary>
    public long GetMeasurementTimingBudget()
    {
        Steps s = ReadSteps();
        long budget = FixedOverheads(s, 1910 + 960);
        if (s.FinalRange)
        {
            budget += s.FinalRangeUs + 550;
        }
        _timingBudgetUs = budget;
        return budget;
    }

    /// <summary>
    /// Set the time allowed for one measurement (≥ 20 ms): longer budgets give
    /// more accurate readings. Adjusts the final-range timeout.
    /// </summary>
    public bool SetMeasurementTimingBudget(long budgetUs)
    {
        if (budgetUs < 20000)
        {
            return false;
        }
        Steps s = ReadSteps();
        long used = FixedOverheads(s, 1320 + 960);
        if (s.FinalRange)
        {
            used += 550;
            if (used > budgetUs)
            {
                return false;
            }
            long finalUs = budgetUs - used;
            long finalMclks = TimeoutMicrosecondsToMclks(finalUs, s.FinalRangeVcselPclks);
            if (s.PreRange)
            {
                finalMclks += s.PreRangeMclks;
            }
            W16(FinalRangeConfigTimeoutMacropHi, EncodeTimeout(finalMclks));
            _timingBudgetUs = budgetUs;
        }
        return true;
    }

    // ---- Ranging ----

    /// <summary>One single-shot measurement in mm; 65535 on timeout (≥ 8190 means out of range).</summary>
    public int ReadRangeSingleMillimeters()
    {
        W(0x80, 0x01);
        W(0xFF, 0x01);
        W(0x00, 0x00);
        W(0x91, _stopVariable);
        W(0x00, 0x01);
        W(0xFF, 0x00);
        W(0x80, 0x00);
        W(SysrangeStart, 0x01);
        int polls = 0;
        while ((R(SysrangeStart) & 0x01) != 0)
        {
            if (++polls > TimeoutPolls)
            {
                TimedOut = true;
                return 65535;
            }
            Timing.SleepMs(1);
        }
        return ReadRangeContinuousMillimeters();
    }

    /// <summary>
    /// Start back-to-back (<paramref name="periodMs"/> = 0) or timed continuous
    /// ranging; read results with <see cref="ReadRangeContinuousMillimeters"/>.
    /// </summary>
    public void StartContinuous(int periodMs)
    {
        W(0x80, 0x01);
        W(0xFF, 0x01);
        W(0x00, 0x00);
        W(0x91, _stopVariable);
        W(0x00, 0x01);
        W(0xFF, 0x00);
        W(0x80, 0x00);
        if (periodMs != 0)
        {
            int oscCalibrate = R16(OscCalibrateVal);
            if (oscCalibrate != 0)
            {
                periodMs *= oscCalibrate;
            }
            byte[] b = new byte[5];
            b[0] = SystemIntermeasurementPeriod;
            b[1] = (byte)(periodMs >> 24);
            b[2] = (byte)(periodMs >> 16);
            b[3] = (byte)(periodMs >> 8);
            b[4] = (byte)periodMs;
            _dev.Write(b);
            W(SysrangeStart, 0x04); // timed
        }
        else
        {
            W(SysrangeStart, 0x02); // back-to-back
        }
    }

    public void StopContinuous()
    {
        W(SysrangeStart, 0x01); // single-shot mode stops continuous
        W(0xFF, 0x01);
        W(0x00, 0x00);
        W(0x91, 0x00);
        W(0x00, 0x01);
        W(0xFF, 0x00);
    }

    /// <summary>Wait for a result, read it (mm) and clear the interrupt. 65535 on timeout.</summary>
    public int ReadRangeContinuousMillimeters()
    {
        int polls = 0;
        while ((R(ResultInterruptStatus) & 0x07) == 0)
        {
            if (++polls > TimeoutPolls)
            {
                TimedOut = true;
                return 65535;
            }
            Timing.SleepMs(1);
        }
        // RESULT_RANGE_STATUS + 10 holds the range in mm.
        int range = R16(ResultRangeStatus + 10);
        W(SystemInterruptClear, 0x01);
        return range;
    }
}
