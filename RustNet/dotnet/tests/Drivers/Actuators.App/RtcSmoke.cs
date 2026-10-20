using RustNet.Community.Bus;
using RustNet.Community.Rtc;

namespace DriversApp;

public static class RtcSmoke
{
    /// <summary>2026-10-20 is a Tuesday (python datetime), 2101-03-04 a Friday.</summary>
    public static void RunDayOfWeek()
    {
        Check.Equal("dow 2026-10-20 tuesday", 2, RtcDateTime.ComputeDayOfWeek(2026, 10, 20));
        Check.Equal("dow 2000-01-01 saturday", 6, RtcDateTime.ComputeDayOfWeek(2000, 1, 1));
        Check.Equal("dow 2024-02-29 thursday", 4, RtcDateTime.ComputeDayOfWeek(2024, 2, 29));
        Check.Equal("dow 2101-03-04 friday", 5, RtcDateTime.ComputeDayOfWeek(2101, 3, 4));
        Expect.Text("rtc tostring", "2026-10-20 13:05:09", new RtcDateTime(2026, 10, 20, 13, 5, 9).ToString());
    }

    public static void RunDs1307()
    {
        SimI2cDevice sim = new SimI2cDevice(0x68);
        Ds1307 rtc = new Ds1307(sim);
        rtc.SetTime(new RtcDateTime(2026, 10, 20, 13, 45, 30));
        // BCD seconds (CH=0), minutes, hours, day 1..7 (Tue = 3), date, month, year.
        Expect.LastWrite("ds1307 time registers", "00 30 45 13 03 20 10 26", sim);
        RtcDateTime t = rtc.GetTime();
        Expect.Text("ds1307 read back", "2026-10-20 13:45:30", t.ToString());
        Check.Equal("ds1307 dow", 2, t.DayOfWeek);

        sim.SetRegister(0x02, 0x71); // 12-hour mode, PM, 11 -> 23 h
        sim.SetRegister(0x00, 0xD9); // CH set, 59 s
        Check.Equal("ds1307 12h PM decode", 23, rtc.GetTime().Hour);
        Check.Equal("ds1307 seconds ignore CH", 59, rtc.GetTime().Second);
        Check.True("ds1307 halted", !rtc.IsRunning());
        rtc.SetRunning(true);
        Expect.LastWrite("ds1307 clear CH keeps seconds", "00 59", sim);

        rtc.SetSquareWave(Ds1307.SquareWave1Hz);
        Expect.LastWrite("ds1307 SQW 1 Hz", "07 10", sim);
        rtc.SetSquareWave(Ds1307.SquareWave32kHz);
        Expect.LastWrite("ds1307 SQW 32.768 kHz", "07 13", sim);
        byte[] ram = new byte[2];
        ram[0] = 0xAA;
        ram[1] = 0x55;
        rtc.WriteRam(2, ram);
        Expect.LastWrite("ds1307 RAM at 0x0A", "0A AA 55", sim);
        Expect.Bytes("ds1307 RAM read", "AA 55", rtc.ReadRam(2, 2));
    }

    public static void RunDs3231()
    {
        SimI2cDevice sim = new SimI2cDevice(0x68);
        sim.SetRegister(0x0E, 0x1C); // control power-on value
        Ds3231 rtc = new Ds3231(sim);
        rtc.SetTime(new RtcDateTime(2101, 3, 4, 5, 6, 7));
        Expect.LastWrite("ds3231 time with century bit", "00 07 06 05 06 04 83 01", sim);
        Check.Equal("ds3231 century year", 2101, rtc.GetTime().Year);

        sim.SetRegister(0x11, 0x19);
        sim.SetRegister(0x12, 0x40);
        Check.Near("ds3231 temp +25.25", 25.25, rtc.GetTemperature(), 0.0001);
        sim.SetRegister(0x11, 0xF5);
        sim.SetRegister(0x12, 0xC0);
        Check.Near("ds3231 temp -10.25", -10.25, rtc.GetTemperature(), 0.0001);

        sim.SetRegister(0x0F, 0x88);
        Check.True("ds3231 OSF", rtc.LostPower());
        rtc.ClearLostPower();
        Expect.LastWrite("ds3231 clear OSF keeps EN32kHz", "0F 08", sim);

        rtc.SetAlarm1(Ds3231.Alarm1MatchHours, 1, 10, 30, 15);
        Expect.LastWrite("ds3231 A1 match h:m:s (A1M4 set)", "07 15 30 10 81", sim);
        rtc.SetAlarm1(Ds3231.Alarm1MatchDay, 1, 6, 0, 0);
        Expect.LastWrite("ds3231 A1 match Monday (DY/DT)", "07 00 00 06 42", sim);
        rtc.SetAlarm1(Ds3231.Alarm1EverySecond, 1, 0, 0, 0);
        Expect.LastWrite("ds3231 A1 every second", "07 80 80 80 81", sim);
        rtc.SetAlarm1(Ds3231.Alarm1MatchDate, 15, 23, 59, 0);
        Expect.LastWrite("ds3231 A1 match date", "07 00 59 23 15", sim);
        rtc.SetAlarm2(Ds3231.Alarm2MatchMinutes, 1, 0, 45);
        Expect.LastWrite("ds3231 A2 match minutes", "0B 45 80 81", sim);

        rtc.EnableAlarmInterrupt(1, true);
        Expect.LastWrite("ds3231 INTCN + A1IE", "0E 1D", sim);
        rtc.SetSquareWave(Ds3231.SquareWave4096Hz);
        Expect.LastWrite("ds3231 SQW 4.096 kHz, INTCN off", "0E 11", sim);

        sim.SetRegister(0x0F, 0x02);
        Check.True("ds3231 A2F", rtc.AlarmFired(2) && !rtc.AlarmFired(1));
        rtc.ClearAlarm(2);
        Expect.LastWrite("ds3231 clear A2F", "0F 00", sim);

        Ds3232 big = new Ds3232(sim);
        byte[] b = new byte[1];
        b[0] = 0x42;
        big.WriteSram(0, b);
        Expect.LastWrite("ds3232 SRAM at 0x14", "14 42", sim);
    }

    public static void RunPcf8523()
    {
        SimI2cDevice sim = new SimI2cDevice(0x68);
        sim.SetRegister(0x02, 0xE0); // Control_3 reset: switch-over disabled
        Pcf8523 rtc = new Pcf8523(sim);
        rtc.Begin();
        Expect.Write("pcf8523 battery switch-over standard", "02 00", sim, 1);
        Expect.Write("pcf8523 CAP_SEL 12.5 pF, run, 24 h", "00 80", sim, 3);

        rtc.SetTime(new RtcDateTime(2026, 10, 20, 13, 45, 30));
        // Seconds..years at 0x03: days before weekdays (0..6, Tue = 2).
        Expect.LastWrite("pcf8523 time registers", "03 30 45 13 20 02 10 26", sim);
        Expect.Text("pcf8523 read back", "2026-10-20 13:45:30", rtc.GetTime().ToString());
        Check.True("pcf8523 OS clear", !rtc.LostPower());
        sim.SetRegister(0x03, 0xB0);
        Check.True("pcf8523 OS flag", rtc.LostPower());

        sim.SetRegister(0x00, 0x88); // 12-hour mode
        sim.SetRegister(0x05, 0x32); // PM 12 -> noon
        Check.Equal("pcf8523 12h noon", 12, rtc.GetTime().Hour);
        sim.SetRegister(0x05, 0x12); // AM 12 -> midnight
        Check.Equal("pcf8523 12h midnight", 0, rtc.GetTime().Hour);

        sim.SetRegister(0x02, 0x0C);
        Check.True("pcf8523 BLF", rtc.BatteryLow());
        Check.True("pcf8523 BSF", rtc.SwitchedToBattery());
        rtc.Reset();
        Expect.LastWrite("pcf8523 software reset", "00 58", sim);
    }
}
