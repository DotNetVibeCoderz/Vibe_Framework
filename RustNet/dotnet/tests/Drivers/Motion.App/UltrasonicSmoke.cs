using RustNet.Community.Bus;
using RustNet.Community.Ultrasonic;

namespace DriversApp;

public static class UltrasonicSmoke
{
    public static void RunHcSr04()
    {
        SimEchoTimer t = new SimEchoTimer();
        HcSr04 s = new HcSr04(t);
        // Datasheet: µs / 58 = cm.
        Check.Near("hcsr04 datasheet cm", 10.0, HcSr04.DatasheetCentimeters(580), 1e-9);
        Check.Near("hcsr04 datasheet inch", 10.0, HcSr04.DatasheetInches(1480), 1e-9);
        // 1 ms round trip at 343.4 m/s (20 °C) = 171.7 mm one way.
        t.Echoes.Enqueue(1000);
        Check.Near("hcsr04 mm", 171.7, s.ReadMillimeters(), 1e-9);
        Check.Equal("hcsr04 trigger 10us", 10, t.LastPulseUs);
        s.SetAirTemperature(0);
        Check.Near("hcsr04 speed 0C", 331.3, s.SpeedOfSound, 1e-9);
        t.Echoes.Enqueue(1000);
        Check.Near("hcsr04 mm 0C", 165.65, s.ReadMillimeters(), 1e-9);
        s.SetAirTemperature(20);
        Check.Near("hcsr04 no echo", -1.0, s.ReadMillimeters(), 1e-9);
        t.Echoes.Enqueue(1000);
        t.Echoes.Enqueue(0);
        t.Echoes.Enqueue(3000);
        t.Echoes.Enqueue(2000);
        Check.Near("hcsr04 median", 2000 * 343.42 / 2000.0, s.ReadMedianMillimeters(4, 60), 1e-9);
    }

    public static void RunMaxBotix()
    {
        SimAnalogInput an = new SimAnalogInput();
        // LV-EZ: Vcc/512 per inch -> 9.765625 mV/in at 5 V; 10 in = 254 mm.
        MaxBotix lv = new MaxBotix(MaxBotix.FamilyLvEz, an, 5000);
        an.Millivolts = 98;
        Check.Near("maxbotix lv analog", 98 / 9.765625 * 25.4, lv.ReadAnalogMillimeters(), 1e-9);
        Check.Near("maxbotix lv 10in", 254.0, lv.MillivoltsToMillimeters(97.65625), 1e-9);
        // HRLV: Vcc/1024 per 5 mm -> 976.5625 mV = 1000 mm at 5 V.
        MaxBotix hr = new MaxBotix(MaxBotix.FamilyHrlv, an, 5000);
        Check.Near("maxbotix hrlv", 1000.0, hr.MillivoltsToMillimeters(976.5625), 1e-9);
        // XL: Vcc/1024 per cm -> at 3.3 V, 322.265625 mV = 100 cm.
        MaxBotix xl = new MaxBotix(MaxBotix.FamilyXl, an, 3300);
        Check.Near("maxbotix xl", 1000.0, xl.MillivoltsToMillimeters(322.265625), 1e-9);

        SimSerialDevice uart = new SimSerialDevice();
        MaxBotix hs = new MaxBotix(MaxBotix.FamilyHrlv, uart);
        uart.Receive("34\rR1234\rR12");
        Check.True("maxbotix serial frame", hs.Poll());
        Check.Equal("maxbotix serial mm", 1234, hs.LastSerialMillimeters);
        uart.Receive("x4\rR0567\r");
        Check.True("maxbotix serial second", hs.Poll());
        Check.Equal("maxbotix serial garbage skipped", 567, hs.LastSerialMillimeters);
        SimSerialDevice uart2 = new SimSerialDevice();
        MaxBotix ls = new MaxBotix(MaxBotix.FamilyLvEz, uart2);
        uart2.Receive("R012\r");
        ls.Poll();
        Check.Equal("maxbotix lv serial inches", 305, ls.LastSerialMillimeters);
    }

    public static void RunA02yyuw()
    {
        SimSerialDevice uart = new SimSerialDevice();
        A02yyuw s = new A02yyuw(uart);
        // DFRobot wiki example: FF 07 A1 A7 = 0x07A1 = 1953 mm.
        Check.Equal("a02yyuw checksum example", 0xA7, A02yyuw.Checksum(0x07, 0xA1));
        uart.Inbox.Add(0x12);
        uart.Inbox.Add(0xFF);
        uart.Inbox.Add(0x07);
        uart.Inbox.Add(0xA1);
        uart.Inbox.Add(0xA7);
        Check.True("a02yyuw frame", s.Poll());
        Check.Equal("a02yyuw mm", 1953, s.Millimeters);
        AddFrame(uart, 0x00, 0x10, 0x00); // bad checksum
        AddFrame(uart, 0x01, 0x00, 0x00); // 0xFF + 1 + 0 = 0x100 -> 0x00
        s.Poll();
        Check.Equal("a02yyuw checksum error", 1, s.ChecksumErrors);
        Check.Equal("a02yyuw second", 256, s.Millimeters);

        SimSerialDevice u2 = new SimSerialDevice();
        Me007ys me = new Me007ys(u2);
        AddFrame(u2, 0x0B, 0xB8, A02yyuw.Checksum(0x0B, 0xB8));
        me.Poll();
        Check.Equal("me007ys mm", 3000, me.Millimeters);
    }

    private static void AddFrame(SimSerialDevice u, int h, int l, int sum)
    {
        u.Inbox.Add(0xFF);
        u.Inbox.Add((byte)h);
        u.Inbox.Add((byte)l);
        u.Inbox.Add((byte)sum);
    }
}
