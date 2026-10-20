using RustNet.Community.Bus;

namespace RustNet.Community.Rtc;

/// <summary>A calendar time as an RTC keeps it (24-hour clock).</summary>
public class RtcDateTime
{
    public int Year;
    public int Month;
    public int Day;
    public int Hour;
    public int Minute;
    public int Second;

    /// <summary>0 = Sunday … 6 = Saturday.</summary>
    public int DayOfWeek;

    public RtcDateTime()
    {
        Year = 2000;
        Month = 1;
        Day = 1;
    }

    /// <summary>A time with its day of week computed from the date.</summary>
    public RtcDateTime(int year, int month, int day, int hour, int minute, int second)
    {
        Year = year;
        Month = month;
        Day = day;
        Hour = hour;
        Minute = minute;
        Second = second;
        DayOfWeek = ComputeDayOfWeek(year, month, day);
    }

    /// <summary>Sakamoto's method; 0 = Sunday.</summary>
    public static int ComputeDayOfWeek(int year, int month, int day)
    {
        // Month offsets 0,3,2,5,0,3,5,1,4,6,2,4.
        string t = "032503514624";
        if (month < 3)
        {
            year -= 1;
        }
        return (year + year / 4 - year / 100 + year / 400 + (t[month - 1] - '0') + day) % 7;
    }

    /// <summary>"YYYY-MM-DD hh:mm:ss".</summary>
    public override string ToString()
        => $"{Year}-{Two(Month)}-{Two(Day)} {Two(Hour)}:{Two(Minute)}:{Two(Second)}";

    private static string Two(int v) => v < 10 ? "0" + v : "" + v;
}

/// <summary>Shared BCD time-keeping register logic (seconds…years layout).</summary>
public static class RtcBcd
{
    /// <summary>
    /// Decode an hours register: bit 6 set selects 12-hour mode with bit 5 = PM
    /// and bits 4..0 the hour 1..12; otherwise bits 5..0 hold 0..23.
    /// </summary>
    public static int DecodeHours(int reg)
    {
        if ((reg & 0x40) != 0)
        {
            int h = BitOps.FromBcd(reg & 0x1F) % 12;
            return (reg & 0x20) != 0 ? h + 12 : h;
        }
        return BitOps.FromBcd(reg & 0x3F);
    }
}

/// <summary>
/// Maxim DS1307 I2C RTC (address 0x68). Registers 0x00..0x06: seconds
/// (bit 7 CH = clock halt), minutes, hours, day of week 1..7, date, month,
/// year 00..99, all BCD; 0x07 control: OUT (bit 7), SQWE (bit 4), RS1..RS0
/// (00 1 Hz, 01 4.096 kHz, 10 8.192 kHz, 11 32.768 kHz); 0x08..0x3F: 56 bytes
/// of battery-backed RAM. Day of week is stored as <see cref="RtcDateTime.DayOfWeek"/> + 1.
/// </summary>
public class Ds1307
{
    public const int DefaultAddress = 0x68;

    public const int SquareWave1Hz = 0x10;
    public const int SquareWave4kHz = 0x11;
    public const int SquareWave8kHz = 0x12;
    public const int SquareWave32kHz = 0x13;
    public const int SquareWaveOffLow = 0x00;
    public const int SquareWaveOffHigh = 0x80;

    public const int RamSize = 56;

    private readonly I2cDevice _dev;

    public Ds1307(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Ds1307(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Set the time and start the oscillator (CH = 0), in one 8-byte write.</summary>
    public void SetTime(RtcDateTime t)
    {
        byte[] r = new byte[7];
        r[0] = (byte)BitOps.ToBcd(t.Second);
        r[1] = (byte)BitOps.ToBcd(t.Minute);
        r[2] = (byte)BitOps.ToBcd(t.Hour);
        r[3] = (byte)(t.DayOfWeek + 1);
        r[4] = (byte)BitOps.ToBcd(t.Day);
        r[5] = (byte)BitOps.ToBcd(t.Month);
        r[6] = (byte)BitOps.ToBcd(t.Year % 100);
        _dev.WriteRegisters(0x00, r);
    }

    public RtcDateTime GetTime()
    {
        byte[] r = _dev.ReadRegisters(0x00, 7);
        RtcDateTime t = new RtcDateTime();
        t.Second = BitOps.FromBcd(r[0] & 0x7F);
        t.Minute = BitOps.FromBcd(r[1] & 0x7F);
        t.Hour = RtcBcd.DecodeHours(r[2]);
        t.DayOfWeek = ((r[3] & 0x07) + 6) % 7;
        t.Day = BitOps.FromBcd(r[4] & 0x3F);
        t.Month = BitOps.FromBcd(r[5] & 0x1F);
        t.Year = 2000 + BitOps.FromBcd(r[6]);
        return t;
    }

    /// <summary>False while the clock-halt bit is set (fresh chip or stopped).</summary>
    public bool IsRunning() => (_dev.ReadRegister(0x00) & 0x80) == 0;

    /// <summary>Halt or restart the oscillator, keeping the seconds value.</summary>
    public void SetRunning(bool run) => _dev.UpdateRegister(0x00, 0x80, run ? 0x00 : 0x80);

    /// <summary>SQW/OUT pin: one of the SquareWave* constants.</summary>
    public void SetSquareWave(int control) => _dev.WriteRegister(0x07, control);

    public void WriteRam(int offset, byte[] data) => _dev.WriteRegisters(0x08 + offset, data);

    public byte[] ReadRam(int offset, int count) => _dev.ReadRegisters(0x08 + offset, count);
}

/// <summary>
/// Maxim DS3231 (TCXO RTC) and DS3232 (plus 236 bytes SRAM), address 0x68.
/// Time registers 0x00..0x06 as the DS1307 (no CH bit; month bit 7 =
/// century). Alarm 1 at 0x07..0x0A (sec, min, hour, day/date) and alarm 2 at
/// 0x0B..0x0D (min, hour, day/date): bit 7 of each is the AxMy mask bit, bit 6
/// of the day/date register is DY/DT. Control 0x0E: EOSC̅ 7, BBSQW 6, CONV 5,
/// RS2..RS1 4..3, INTCN 2, A2IE 1, A1IE 0. Status 0x0F: OSF 7, EN32kHz 3,
/// BSY 2, A2F 1, A1F 0. Aging 0x10. Temperature 0x11 (signed °C) and 0x12
/// bits 7..6 (0.25 °C steps).
/// </summary>
public class Ds3231
{
    public const int DefaultAddress = 0x68;

    public const int RegControl = 0x0E;
    public const int RegStatus = 0x0F;
    public const int RegTempMsb = 0x11;

    // Alarm 1 rates (A1M4..A1M1 mask bits; DY/DT for the day modes).
    public const int Alarm1EverySecond = 0;
    public const int Alarm1MatchSeconds = 1;
    public const int Alarm1MatchMinutes = 2;
    public const int Alarm1MatchHours = 3;
    public const int Alarm1MatchDate = 4;
    public const int Alarm1MatchDay = 5;

    // Alarm 2 rates (A2M4..A2M2).
    public const int Alarm2EveryMinute = 0;
    public const int Alarm2MatchMinutes = 1;
    public const int Alarm2MatchHours = 2;
    public const int Alarm2MatchDate = 3;
    public const int Alarm2MatchDay = 4;

    // Square-wave rates (RS2..RS1).
    public const int SquareWave1Hz = 0;
    public const int SquareWave1024Hz = 1;
    public const int SquareWave4096Hz = 2;
    public const int SquareWave8192Hz = 3;

    protected readonly I2cDevice Dev;

    public Ds3231(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Ds3231(I2cDevice device)
    {
        Dev = device;
    }

    public void SetTime(RtcDateTime t)
    {
        byte[] r = new byte[7];
        r[0] = (byte)BitOps.ToBcd(t.Second);
        r[1] = (byte)BitOps.ToBcd(t.Minute);
        r[2] = (byte)BitOps.ToBcd(t.Hour);
        r[3] = (byte)(t.DayOfWeek + 1);
        r[4] = (byte)BitOps.ToBcd(t.Day);
        r[5] = (byte)(BitOps.ToBcd(t.Month) | (t.Year >= 2100 ? 0x80 : 0));
        r[6] = (byte)BitOps.ToBcd(t.Year % 100);
        Dev.WriteRegisters(0x00, r);
    }

    public RtcDateTime GetTime()
    {
        byte[] r = Dev.ReadRegisters(0x00, 7);
        RtcDateTime t = new RtcDateTime();
        t.Second = BitOps.FromBcd(r[0] & 0x7F);
        t.Minute = BitOps.FromBcd(r[1] & 0x7F);
        t.Hour = RtcBcd.DecodeHours(r[2]);
        t.DayOfWeek = ((r[3] & 0x07) + 6) % 7;
        t.Day = BitOps.FromBcd(r[4] & 0x3F);
        t.Month = BitOps.FromBcd(r[5] & 0x1F);
        t.Year = ((r[5] & 0x80) != 0 ? 2100 : 2000) + BitOps.FromBcd(r[6]);
        return t;
    }

    /// <summary>Die temperature in °C (0.25 °C resolution, updated every 64 s).</summary>
    public double GetTemperature()
    {
        byte[] r = Dev.ReadRegisters(RegTempMsb, 2);
        int msb = r[0] >= 0x80 ? r[0] - 256 : r[0];
        return msb + (r[1] >> 6) * 0.25;
    }

    /// <summary>OSF: the oscillator stopped at some point (power loss) — the time is not valid.</summary>
    public bool LostPower() => (Dev.ReadRegister(RegStatus) & 0x80) != 0;

    /// <summary>Clear OSF (do it after setting the time).</summary>
    public void ClearLostPower() => Dev.UpdateRegister(RegStatus, 0x80, 0);

    /// <summary>
    /// Program alarm 1. <paramref name="dayOrDate"/> is the date (1..31) for
    /// <see cref="Alarm1MatchDate"/> or the day of week 0..6 for
    /// <see cref="Alarm1MatchDay"/>.
    /// </summary>
    public void SetAlarm1(int rate, int dayOrDate, int hour, int minute, int second)
    {
        byte[] r = new byte[4];
        r[0] = (byte)BitOps.ToBcd(second);
        r[1] = (byte)BitOps.ToBcd(minute);
        r[2] = (byte)BitOps.ToBcd(hour);
        r[3] = (byte)(rate == Alarm1MatchDay ? 0x40 | (dayOrDate + 1) : BitOps.ToBcd(dayOrDate));
        // Number of leading registers that must match: everything after is masked.
        int matched = rate >= Alarm1MatchDate ? 4 : rate;
        for (int i = matched; i < 4; i++)
        {
            r[i] = (byte)(r[i] | 0x80);
        }
        Dev.WriteRegisters(0x07, r);
    }

    /// <summary>Program alarm 2 (minute resolution, fires at second 00).</summary>
    public void SetAlarm2(int rate, int dayOrDate, int hour, int minute)
    {
        byte[] r = new byte[3];
        r[0] = (byte)BitOps.ToBcd(minute);
        r[1] = (byte)BitOps.ToBcd(hour);
        r[2] = (byte)(rate == Alarm2MatchDay ? 0x40 | (dayOrDate + 1) : BitOps.ToBcd(dayOrDate));
        int matched = rate >= Alarm2MatchDate ? 3 : rate;
        for (int i = matched; i < 3; i++)
        {
            r[i] = (byte)(r[i] | 0x80);
        }
        Dev.WriteRegisters(0x0B, r);
    }

    /// <summary>Route alarm <paramref name="alarm"/> (1 or 2) to INT̅/SQW (INTCN = 1, AxIE).</summary>
    public void EnableAlarmInterrupt(int alarm, bool on)
    {
        int bit = alarm == 1 ? 0x01 : 0x02;
        Dev.UpdateRegister(RegControl, 0x04 | bit, 0x04 | (on ? bit : 0));
    }

    /// <summary>Has alarm 1 or 2 fired (A1F / A2F)?</summary>
    public bool AlarmFired(int alarm) => (Dev.ReadRegister(RegStatus) & (alarm == 1 ? 0x01 : 0x02)) != 0;

    public void ClearAlarm(int alarm) => Dev.UpdateRegister(RegStatus, alarm == 1 ? 0x01 : 0x02, 0);

    /// <summary>Square wave on INT̅/SQW (INTCN = 0) at a SquareWave* rate.</summary>
    public void SetSquareWave(int rate) => Dev.UpdateRegister(RegControl, 0x1C, (rate & 3) << 3);

    /// <summary>Force a temperature conversion and TCXO update (CONV).</summary>
    public void ConvertTemperature() => Dev.UpdateRegister(RegControl, 0x20, 0x20);

    /// <summary>32 kHz output on/off (EN32kHz).</summary>
    public void Set32kHzOutput(bool on) => Dev.UpdateRegister(RegStatus, 0x08, on ? 0x08 : 0);
}

/// <summary>DS3232: a DS3231 with 236 bytes of battery-backed SRAM at 0x14..0xFF.</summary>
public class Ds3232 : Ds3231
{
    public const int SramStart = 0x14;
    public const int SramSize = 236;

    public Ds3232(int bus) : base(bus)
    {
    }

    public Ds3232(I2cDevice device) : base(device)
    {
    }

    public void WriteSram(int offset, byte[] data) => Dev.WriteRegisters(SramStart + offset, data);

    public byte[] ReadSram(int offset, int count) => Dev.ReadRegisters(SramStart + offset, count);
}

/// <summary>
/// NXP PCF8523 RTC (address 0x68). Control_1 0x00: CAP_SEL 7 (1 = 12.5 pF),
/// STOP 5, SR 4 (software reset by writing 0x58), 12_24 3, SIE/AIE/CIE.
/// Control_3 0x02: PM[2:0] 7..5 battery switch-over / low detection, BSF 3,
/// BLF 2. Time at 0x03..0x09: seconds (bit 7 OS = oscillator stopped),
/// minutes, hours, days, weekdays (0..6), months, years — note days come
/// before weekdays, unlike the DS13xx/DS323x.
/// </summary>
public class Pcf8523
{
    public const int DefaultAddress = 0x68;

    public const int RegControl1 = 0x00;
    public const int RegControl2 = 0x01;
    public const int RegControl3 = 0x02;
    public const int RegSeconds = 0x03;
    public const int RegOffset = 0x0E;

    // Control_3 PM[2:0] values (already shifted into bits 7..5).
    /// <summary>Switch-over in standard mode, battery-low detection on.</summary>
    public const int BatteryStandard = 0x00;
    /// <summary>Switch-over in direct-switching mode, battery-low detection on.</summary>
    public const int BatteryDirect = 0x20;
    /// <summary>Switch-over standard mode, battery-low detection off.</summary>
    public const int BatteryStandardNoDetect = 0x80;
    /// <summary>Switch-over direct mode, battery-low detection off.</summary>
    public const int BatteryDirectNoDetect = 0xA0;
    /// <summary>Switch-over and battery-low detection off (power-on default).</summary>
    public const int BatteryDisabled = 0xE0;

    private readonly I2cDevice _dev;

    public Pcf8523(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Pcf8523(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>
    /// Enable battery switch-over (standard mode) — out of reset the PCF8523
    /// does NOT switch to the backup battery — select the crystal load and
    /// make sure the clock runs in 24-hour mode.
    /// </summary>
    public void Begin(bool load12p5pF = true)
    {
        SetBatterySwitchover(BatteryStandard);
        _dev.UpdateRegister(RegControl1, 0xA8, load12p5pF ? 0x80 : 0x00);
    }

    /// <summary>Software reset (writes 0x58 to Control_1).</summary>
    public void Reset() => _dev.WriteRegister(RegControl1, 0x58);

    public void SetBatterySwitchover(int pm) => _dev.UpdateRegister(RegControl3, 0xE0, pm);

    /// <summary>BLF: backup battery is low.</summary>
    public bool BatteryLow() => (_dev.ReadRegister(RegControl3) & 0x04) != 0;

    /// <summary>BSF: a switch-over to battery happened (cleared by <see cref="ClearSwitchoverFlag"/>).</summary>
    public bool SwitchedToBattery() => (_dev.ReadRegister(RegControl3) & 0x08) != 0;

    public void ClearSwitchoverFlag() => _dev.UpdateRegister(RegControl3, 0x08, 0);

    /// <summary>OS flag: the oscillator stopped; time is not trustworthy.</summary>
    public bool LostPower() => (_dev.ReadRegister(RegSeconds) & 0x80) != 0;

    /// <summary>STOP bit: freeze or run the time circuits.</summary>
    public void SetRunning(bool run) => _dev.UpdateRegister(RegControl1, 0x20, run ? 0 : 0x20);

    /// <summary>Set the time (clears OS).</summary>
    public void SetTime(RtcDateTime t)
    {
        byte[] r = new byte[7];
        r[0] = (byte)BitOps.ToBcd(t.Second);
        r[1] = (byte)BitOps.ToBcd(t.Minute);
        r[2] = (byte)BitOps.ToBcd(t.Hour);
        r[3] = (byte)BitOps.ToBcd(t.Day);
        r[4] = (byte)t.DayOfWeek;
        r[5] = (byte)BitOps.ToBcd(t.Month);
        r[6] = (byte)BitOps.ToBcd(t.Year % 100);
        _dev.WriteRegisters(RegSeconds, r);
    }

    public RtcDateTime GetTime()
    {
        byte[] r = _dev.ReadRegisters(RegSeconds, 7);
        RtcDateTime t = new RtcDateTime();
        t.Second = BitOps.FromBcd(r[0] & 0x7F);
        t.Minute = BitOps.FromBcd(r[1] & 0x7F);
        // 12_24 = 1 puts AMPM in bit 5 and hours 1..12 in bits 4..0.
        bool twelve = (_dev.ReadRegister(RegControl1) & 0x08) != 0;
        if (twelve)
        {
            int h = BitOps.FromBcd(r[2] & 0x1F) % 12;
            t.Hour = (r[2] & 0x20) != 0 ? h + 12 : h;
        }
        else
        {
            t.Hour = BitOps.FromBcd(r[2] & 0x3F);
        }
        t.Day = BitOps.FromBcd(r[3] & 0x3F);
        t.DayOfWeek = r[4] & 0x07;
        t.Month = BitOps.FromBcd(r[5] & 0x1F);
        t.Year = 2000 + BitOps.FromBcd(r[6]);
        return t;
    }

    /// <summary>Aging/frequency offset: signed 7-bit steps; mode false = every 2 h (4.34 ppm), true = every minute (4.069 ppm).</summary>
    public void SetOffset(int steps, bool everyMinute)
    {
        _dev.WriteRegister(RegOffset, (everyMinute ? 0x80 : 0) | (steps & 0x7F));
    }
}
