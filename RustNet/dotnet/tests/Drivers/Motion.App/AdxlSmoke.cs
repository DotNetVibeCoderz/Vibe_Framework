using RustNet.Community.Adxl;
using RustNet.Community.Bus;

namespace DriversApp;

public static class AdxlSmoke
{
    /// <summary>ADXL335/ADXL377: ratiometric sensitivity (300 / 6.5 mV/g at 3 V), 0 g at Vs/2.</summary>
    public static void RunAnalog()
    {
        SimAnalogInput x = new SimAnalogInput();
        SimAnalogInput y = new SimAnalogInput();
        SimAnalogInput z = new SimAnalogInput();
        // At Vs = 3.3 V: 330 mV/g, 0 g at 1650 mV.
        AnalogAccelerometer a = AnalogAccelerometer.Adxl335(x, y, z, 3300);
        Check.Near("adxl335 sensitivity", 330.0, a.MillivoltsPerG, 1e-9);
        x.Millivolts = 1650;
        y.Millivolts = 1980;
        z.Millivolts = 1320;
        a.Read();
        Check.Near("adxl335 x 0g", 0.0, a.X, 1e-9);
        Check.Near("adxl335 y +1g", 1.0, a.Y, 1e-9);
        Check.Near("adxl335 z -1g", -1.0, a.Z, 1e-9);

        // ADXL377 at 3.0 V: 6.5 mV/g. 1565 mV = +10 g, 200 mV = -200 g.
        AnalogAccelerometer h = AnalogAccelerometer.Adxl377(x, y, z, 3000);
        x.Millivolts = 1565;
        y.Millivolts = 200;
        z.Millivolts = 1500;
        h.Read();
        Check.Near("adxl377 x", 10.0, h.X, 1e-9);
        Check.Near("adxl377 y", -200.0, h.Y, 1e-9);
        Check.Near("adxl377 z", 0.0, h.Z, 1e-9);
    }

    public static void Run345()
    {
        SimI2cDevice sim = new SimI2cDevice(0x53);
        sim.SetRegister(0x00, 0xE5);
        sim.SetRegister16Le(0x32, 256);
        sim.SetRegister16Le(0x34, -256);
        sim.SetRegister16Le(0x36, 128);
        Adxl345 a = new Adxl345(sim);
        Check.True("adxl345 begin", a.Begin());
        Check.Equal("adxl345 measure bit", 0x08, sim.Registers[0x2D]);
        Check.Equal("adxl345 full res", 0x08, sim.Registers[0x31]);
        a.Read();
        // Full resolution: 256 LSB/g (3.9 mg/LSB).
        Check.Near("adxl345 x", 1.0, a.X, 1e-9);
        Check.Near("adxl345 y", -1.0, a.Y, 1e-9);
        Check.Near("adxl345 z", 0.5, a.Z, 1e-9);

        // 10-bit mode at ±16 g: 32 LSB/g, so -512 is -16 g.
        a.FullResolution = false;
        a.SetRange(Adxl345.Range16G);
        Check.Equal("adxl345 data format", 0x03, sim.Registers[0x31]);
        sim.SetRegister16Le(0x32, -512);
        a.Read();
        Check.Near("adxl345 x 16g", -16.0, a.X, 1e-9);

        SimI2cDevice other = new SimI2cDevice(0x53);
        Check.True("adxl345 wrong id refused", !new Adxl345(other).Begin());
    }

    public static void Run362()
    {
        byte[] regs = new byte[64];
        regs[0] = 0xAD;
        regs[1] = 0x1D;
        regs[2] = 0xF2;
        Put16(regs, 0x0E, 1000);
        Put16(regs, 0x10, -500);
        Put16(regs, 0x12, 0);
        Put16(regs, 0x14, 504);
        SimSpiDevice spi = new SimSpiDevice();
        spi.Responder = new Adxl362Model(regs).Respond;
        Adxl362 a = new Adxl362(spi);
        Check.True("adxl362 begin", a.Begin());
        Check.Equal("adxl362 soft reset", 0x52, regs[0x1F]);
        Check.Equal("adxl362 measurement mode", 0x02, regs[0x2D] & 0x03);
        Check.Equal("adxl362 filter ctl default", 0x13, regs[0x2C]);
        a.Read();
        Check.Near("adxl362 x (1 mg/LSB)", 1.0, a.X, 1e-9);
        Check.Near("adxl362 y", -0.5, a.Y, 1e-9);
        // 350 LSB at 25 °C, 0.065 °C/LSB: 504 LSB is 25 + 154*0.065 = 35.01 °C.
        Check.Near("adxl362 temperature", 35.01, a.TemperatureCelsius, 1e-6);
    }

    private static void Put16(byte[] r, int at, int v)
    {
        r[at] = (byte)v;
        r[at + 1] = (byte)(v >> 8);
    }
}

/// <summary>ADXL362 command protocol: 0x0B read, 0x0A write (instance-method delegate, no closure class).</summary>
public class Adxl362Model
{
    private readonly byte[] _regs;

    public Adxl362Model(byte[] regs)
    {
        _regs = regs;
    }

    public byte[] Respond(byte[] tx)
    {
        byte[] rx = new byte[tx.Length];
        if (tx[0] == 0x0B)
        {
            for (int i = 2; i < tx.Length; i++)
            {
                rx[i] = _regs[(tx[1] + i - 2) & 63];
            }
        }
        else if (tx[0] == 0x0A)
        {
            _regs[tx[1] & 63] = tx[2];
        }
        return rx;
    }
}
