using RustNet.Community.Bus;
using RustNet.Community.FlowMeter;
using RustNet.Community.Input;
using RustNet.Community.Mpr121;
using RustNet.Community.Touch;

namespace DriversApp;

public static class InputSmoke
{
    public static void RunFlowMeter()
    {
        SimPulseCounter c = new SimPulseCounter();
        // YF-S201: F = 7.5 × Q, so 75 Hz is 10 L/min and a litre is 450 pulses.
        FlowMeter f = new FlowMeter(c, FlowMeter.KYfS201);
        Check.Near("flow yf-s201 pulses/L", 450.0, f.PulsesPerLiter, 1e-9);
        c.Total = 75;
        Check.Near("flow yf-s201 10 L/min", 10.0, f.Update(1000), 1e-9);
        c.Total = 75 + 75; // 75 pulses in 2 s = 37.5 Hz = 5 L/min
        Check.Near("flow yf-s201 over 2 s", 5.0, f.Update(2000), 1e-9);
        c.Total = 450;
        Check.Near("flow total 1 L", 1.0, f.TotalLiters, 1e-9);
        f.ResetTotal();
        Check.Near("flow reset", 0.0, f.TotalLiters, 1e-9);
        // YF-B1: F = 11 × Q; 110 pulses in 1 s = 10 L/min.
        SimPulseCounter c2 = new SimPulseCounter();
        FlowMeter b1 = new FlowMeter(c2, FlowMeter.KYfB1);
        c2.Total = 110;
        Check.Near("flow yf-b1", 10.0, b1.Update(1000), 1e-9);
        b1.CalibrateK(660, 1.0);
        Check.Near("flow calibrate K", 11.0, b1.K, 1e-9);
        // Software edge counting on a GPIO.
        SimDigitalInput pin = new SimDigitalInput();
        PulseCounter soft = new PulseCounter(pin);
        for (int i = 0; i < 10; i++)
        {
            pin.Level = true;
            soft.Poll();
            soft.Poll();
            pin.Level = false;
            soft.Poll();
        }
        Check.Equal("flow soft counter", 10, soft.Count());
    }

    public static void RunXpt2046()
    {
        SimSpiDevice spi = new SimSpiDevice();
        spi.Responder = new Xpt2046Model().Respond;
        Xpt2046 t = new Xpt2046(spi);
        TouchPoint p = t.Read();
        Check.Equal("xpt2046 one transaction", 1, spi.Transactions.Count);
        Check.Equal("xpt2046 first cmd z1", 0xB1, spi.Transactions[0][0]);
        byte[] tx0 = spi.Transactions[0];
        Check.Equal("xpt2046 last cmd powers down", 0x90, tx0[tx0.Length - 3]);
        Check.Equal("xpt2046 x average", 2048, p.RawX);
        Check.Equal("xpt2046 y", 1024, p.RawY);
        Check.True("xpt2046 touched", p.Touched);
        // Datasheet: R = Rx × X/4096 × (Z2/Z1 - 1) = 400 × 0.5 × 2.
        Check.Near("xpt2046 resistance", 400.0, p.ResistanceOhms, 1e-9);

        // Three-point affine calibration recovers a rotated/scaled mapping.
        TouchCalibration cal = new TouchCalibration();
        Check.True("touch solve", cal.Solve(
            300, 300, Sx(300, 300), Sy(300, 300),
            3800, 400, Sx(3800, 400), Sy(3800, 400),
            2000, 3700, Sx(2000, 3700), Sy(2000, 3700)));
        Check.Near("touch A", 0.08, cal.A, 1e-9);
        Check.Near("touch B", -0.004, cal.B, 1e-9);
        Check.Near("touch F", -20.0, cal.F, 1e-6);
        Check.Near("touch map x", 145.648, cal.MapX(2048, 2048), 1e-6);
        Check.Near("touch map y", 117.216, cal.MapY(2048, 2048), 1e-6);
        cal.SetLinear(200, 3900, 300, 3800, 320, 240);
        Check.Near("touch linear", 159.5, cal.MapX(2050, 0), 1e-9);
        Check.Near("touch linear top", 0.0, cal.MapY(0, 300), 1e-9);
    }

    private static double Sx(double rx, double ry) => 0.08 * rx - 0.004 * ry - 10;

    private static double Sy(double rx, double ry) => 0.002 * rx + 0.065 * ry - 20;

    public static void RunTsc2004()
    {
        SimWordI2c sim = new SimWordI2c(0x48);
        sim.PointerMask = 0x78; // register number in bits 6:3
        sim.Set(0x00, 1024, 2);
        sim.Set(0x08, 3072, 2);
        sim.Set(0x10, 500, 2);
        sim.Set(0x18, 1500, 2);
        Tsc2004 t = new Tsc2004(sim);
        t.Begin();
        Check.Equal("tsc2004 reset cmd", 0x86, sim.Writes[0][0]);
        Check.Equal("tsc2004 stop cmd", 0x85, sim.Writes[1][0]);
        Check.Equal("tsc2004 cfr0 write flag", 0x62, sim.Writes[2][0]);
        Check.Equal("tsc2004 cfr0", 0xABA0, sim.Get(0x60));
        Check.Equal("tsc2004 cfr2", 0xB01C, sim.Get(0x70));
        Check.Equal("tsc2004 scan cmd", 0x84, sim.Writes[sim.Writes.Count - 1][0]);
        TouchPoint p = t.Read();
        Check.Equal("tsc2004 z2 read address", 0x19, sim.Writes[sim.Writes.Count - 1][0]);
        Check.Equal("tsc2004 x", 1024, p.RawX);
        Check.Equal("tsc2004 y", 3072, p.RawY);
        Check.True("tsc2004 touched", p.Touched);
        Check.Near("tsc2004 resistance", 200.0, p.ResistanceOhms, 1e-9);
        sim.Set(0x10, 0, 2);
        Check.True("tsc2004 released", !t.Read().Touched);
    }

    public static void RunMpr121()
    {
        SimI2cDevice sim = new SimI2cDevice(0x5A);
        sim.SetRegister(0x5D, 0x24);
        sim.SetRegister(0x00, 0x05);
        sim.SetRegister(0x01, 0x00);
        sim.SetRegister16Le(0x06, 0x02C3);
        sim.SetRegister(0x1F, 0xB0);
        Mpr121 m = new Mpr121(sim);
        Check.True("mpr121 begin", m.Begin());
        Check.Equal("mpr121 soft reset", 0x63, sim.Registers[0x80]);
        Check.Equal("mpr121 touch th 0", 12, sim.Registers[0x41]);
        Check.Equal("mpr121 release th 11", 6, sim.Registers[0x41 + 23]);
        Check.Equal("mpr121 nclr", 0x0E, sim.Registers[0x2D]);
        Check.Equal("mpr121 config1", 0x10, sim.Registers[0x5C]);
        Check.Equal("mpr121 ecr", 0x8F, sim.Registers[0x5E]);
        Check.Equal("mpr121 mask", 0x005, m.ReadTouched());
        Check.True("mpr121 e2", m.IsTouched(2));
        Check.Equal("mpr121 key", '0', m.ReadKey());
        Check.Equal("mpr121 filtered", 707, m.ReadFiltered(1));
        Check.Equal("mpr121 baseline", 704, m.ReadBaseline(1));
        ElectrodeTally tally = new ElectrodeTally();
        m.Touched = tally.OnTouched;
        m.Released = tally.OnReleased;
        m.Poll();
        sim.SetRegister(0x00, 0x04);
        m.Poll();
        Check.Equal("mpr121 touched events", 0x05, tally.Touches);
        Check.Equal("mpr121 released events", 0x01, tally.Releases);
        SimI2cDevice absent = new SimI2cDevice(0x5A);
        Check.True("mpr121 absent", !new Mpr121(absent).Begin());
    }

    public static void RunKeypad()
    {
        SimDigitalOutput[] rows = new SimDigitalOutput[4];
        for (int i = 0; i < 4; i++)
        {
            rows[i] = new SimDigitalOutput();
        }
        KeypadModel model = new KeypadModel(rows);
        model.Row = 1;
        model.Col = 1;
        SimDigitalInput[] cols = new SimDigitalInput[3];
        for (int c = 0; c < 3; c++)
        {
            SimDigitalInput ci = new SimDigitalInput();
            ci.Source = new KeypadColumn(model, c).Read;
            cols[c] = ci;
        }
        DigitalOutput[] ro = new DigitalOutput[4];
        DigitalInput[] co = new DigitalInput[3];
        for (int i = 0; i < 4; i++)
        {
            ro[i] = rows[i];
        }
        for (int i = 0; i < 3; i++)
        {
            co[i] = cols[i];
        }
        MatrixKeypad k = new MatrixKeypad(ro, co, MatrixKeypad.Layout4x3);
        Check.Equal("keypad scan index", 4, k.Scan());
        Check.Equal("keypad debounce 1", 0, k.Poll());
        Check.Equal("keypad press 5", '5', k.Poll());
        Check.Equal("keypad held", 0, k.Poll());
        Check.True("keypad rows released", rows[0].State && rows[1].State && rows[2].State && rows[3].State);
        model.Row = -1;
        k.Poll();
        k.Poll();
        model.Row = 3;
        model.Col = 2;
        k.Poll();
        Check.Equal("keypad press #", '#', k.Poll());
    }

    public static void RunJoystick()
    {
        SimAnalogInput x = new SimAnalogInput();
        SimAnalogInput y = new SimAnalogInput();
        SimDigitalInput b = new SimDigitalInput();
        b.Level = true;
        AnalogJoystick j = new AnalogJoystick(x, y, b);
        Check.Equal("joystick default centre", 2047, j.CenterX);
        x.Raw = 2000;
        y.Raw = 2100;
        j.CalibrateCenter();
        j.Deadzone = 0.1;
        x.Raw = 2000 + 100; // inside the deadzone (100/2095 < 0.1)
        y.Raw = 2100;
        j.Read();
        Check.Near("joystick deadzone", 0.0, j.X, 1e-12);
        Check.Equal("joystick centred angle", -1, (long)j.AngleDegrees());
        Check.True("joystick released", !j.Pressed);
        // 1257 of 2095 counts = 0.6 of travel -> (0.6 - 0.1) / 0.9 after the deadzone.
        x.Raw = 2000 + 1257;
        y.Raw = 0;
        b.Level = false;
        j.InvertY = true;
        j.Read();
        Check.Near("joystick x", 0.5555555555555556, j.X, 1e-12);
        Check.Near("joystick y full (inverted)", 1.0, j.Y, 1e-12);
        Check.True("joystick pressed", j.Pressed);
        Check.Near("joystick angle", 60.94539590092286, j.AngleDegrees(), 1e-9);
        Check.Near("joystick magnitude clamp", 1.0, j.Magnitude(), 1e-12);
        x.Raw = 4095;
        j.Read();
        Check.Near("joystick x full", 1.0, j.X, 1e-12);
    }
}

/// <summary>XPT2046 conversions: X (noisy first sample, then 2040, 2048, 2056), Y 1024, Z1 1000, Z2 3000.</summary>
public class Xpt2046Model
{
    public int XCount;

    public byte[] Respond(byte[] tx)
    {
        byte[] rx = new byte[tx.Length];
        for (int g = 0; g + 2 < tx.Length; g += 3)
        {
            int ch = (tx[g] >> 4) & 7;
            int v = 0;
            if (ch == 5)
            {
                v = XCount == 0 ? 9 : 2040 + 8 * (XCount - 1);
                XCount++;
            }
            else if (ch == 1)
            {
                v = 1024;
            }
            else if (ch == 3)
            {
                v = 1000;
            }
            else if (ch == 4)
            {
                v = 3000;
            }
            // 16 clocks: busy bit, 12 data bits, 3 zeros.
            rx[g + 1] = (byte)((v << 3) >> 8);
            rx[g + 2] = (byte)(v << 3);
        }
        return rx;
    }
}

public class ElectrodeTally
{
    public int Touches;
    public int Releases;

    public void OnTouched(int e) => Touches += 1 << e;

    public void OnReleased(int e) => Releases += 1 << e;
}

/// <summary>One pressed key at (Row, Col); Row -1 = none.</summary>
public class KeypadModel
{
    public SimDigitalOutput[] Rows;
    public int Row = -1;
    public int Col;

    public KeypadModel(SimDigitalOutput[] rows)
    {
        Rows = rows;
    }
}

public class KeypadColumn
{
    private readonly KeypadModel _m;
    private readonly int _col;

    public KeypadColumn(KeypadModel m, int col)
    {
        _m = m;
        _col = col;
    }

    /// <summary>Low only while the pressed key's row is driven low.</summary>
    public bool Read()
    {
        if (_m.Row < 0 || _m.Col != _col)
        {
            return true;
        }
        return _m.Rows[_m.Row].State;
    }
}
