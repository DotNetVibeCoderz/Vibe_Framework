using RustNet.Community.Bus;
using RustNet.Community.Hx711;
using RustNet.Community.Max1704x;
using RustNet.Community.Nau7802;

namespace DriversApp;

/// <summary>The HX711's serial interface: data shifts out on PD_SCK rising edges.</summary>
public class SimHx711
{
    public Queue<int> Conversions = new Queue<int>();
    public int Current;
    public int Edges;
    public bool Busy = true;
    public List<int> PulsesPerRead = new List<int>();

    public SimHx711Clock Clock;
    public SimHx711Data Data;

    public SimHx711()
    {
        Clock = new SimHx711Clock(this);
        Data = new SimHx711Data(this);
    }

    public void Rising()
    {
        if (Edges == 0 && !Busy)
        {
            Busy = true; // reading started
        }
        Edges++;
    }

    public bool DataLevel()
    {
        if (Edges == 0)
        {
            // Idle: low when a conversion is waiting.
            if (Conversions.Count > 0)
            {
                Current = Conversions.Dequeue() & 0xFFFFFF;
                Busy = false;
                return false;
            }
            return !Busy ? false : true;
        }
        if (Edges <= 24)
        {
            return ((Current >> (24 - Edges)) & 1) != 0;
        }
        return true;
    }

    /// <summary>Called by the test between reads to close the previous transfer.</summary>
    public void EndRead()
    {
        PulsesPerRead.Add(Edges);
        Edges = 0;
    }
}

public class SimHx711Clock : DigitalOutput
{
    private readonly SimHx711 _chip;

    public SimHx711Clock(SimHx711 chip)
    {
        _chip = chip;
    }

    public override void Write(bool high)
    {
        if (high && !State)
        {
            _chip.Rising();
        }
        State = high;
    }
}

public class SimHx711Data : DigitalInput
{
    private readonly SimHx711 _chip;

    public SimHx711Data(SimHx711 chip)
    {
        _chip = chip;
    }

    public override bool Read() => _chip.DataLevel();
}

public static class PowerSmoke
{
    public static void RunMax1704x()
    {
        SimWordI2c sim = new SimWordI2c(0x36);
        sim.Set(0x0C, 0x971C, 2); // CONFIG power-on default: RCOMP 0x97, ATHD 0x1C (4 %)
        sim.Set(0x08, 0x0003, 2);
        sim.Set(0x02, 0xC5A0, 2); // 0xC5A = 3162 × 1.25 mV = 3.9525 V
        sim.Set(0x04, 0x5A80, 2); // 90 % + 128/256 %
        Max1704x g = new Max1704x(sim, 1);
        Check.Equal("max17043 version", 3, g.ReadVersion());
        Check.Near("max17043 vcell", 3.9525, g.ReadVolts(), 1e-9);
        Check.Near("max17043 soc", 90.5, g.ReadStateOfCharge(), 1e-9);
        Check.Equal("max17043 default threshold 4%", 4, g.ReadAlertThreshold());
        g.SetAlertThreshold(10);
        Check.Equal("max17043 athd = 32-10", 0x9716, sim.Get(0x0C));
        g.SetCompensation(0x80);
        Check.Equal("max17043 rcomp", 0x8016, sim.Get(0x0C));
        g.QuickStart();
        Check.Equal("max17043 quick start", 0x4000, sim.Get(0x06));
        g.Reset();
        Check.Equal("max17043 por", 0x5400, sim.Get(0xFE));
        sim.Set(0x0C, 0x9736, 2);
        Check.True("max17043 alert", g.IsAlerting());
        g.ClearAlert();
        Check.Equal("max17043 alert cleared", 0x9716, sim.Get(0x0C));
        // MAX17044: 2.5 mV/LSB for the 2-cell pack.
        Max1704x two = new Max1704x(sim, 2);
        Check.Near("max17044 vcell", 7.905, two.ReadVolts(), 1e-9);
    }

    public static void RunHx711()
    {
        SimHx711 chip = new SimHx711();
        Hx711 hx = new Hx711(chip.Data, chip.Clock);
        // Datasheet output coding: 0x7FFFFF max, 0x800000 min, two's complement.
        chip.Conversions.Enqueue(0x7FFFFF);
        Check.Equal("hx711 max", 8388607, hx.ReadRaw());
        chip.EndRead();
        chip.Conversions.Enqueue(0x800000);
        Check.Equal("hx711 min", -8388608, hx.ReadRaw());
        chip.EndRead();
        hx.SetGain(Hx711.GainB32);
        chip.Conversions.Enqueue(0xFFFFFE);
        Check.Equal("hx711 -2", -2, hx.ReadRaw());
        chip.EndRead();
        hx.SetGain(Hx711.GainA64);
        chip.Conversions.Enqueue(0x012345);
        Check.Equal("hx711 0x012345", 0x012345, hx.ReadRaw());
        chip.EndRead();
        // 25 pulses select A/128, 26 B/32, 27 A/64.
        Check.Equal("hx711 pulses a128", 25, chip.PulsesPerRead[0]);
        Check.Equal("hx711 pulses b32", 26, chip.PulsesPerRead[2]);
        Check.Equal("hx711 pulses a64", 27, chip.PulsesPerRead[3]);
        // Tare at 1000 counts, then 21000 counts for a 500 g weight: 40 counts/g.
        hx.SetGain(Hx711.GainA128);
        chip.Conversions.Enqueue(1000);
        hx.Tare(1);
        chip.EndRead();
        chip.Conversions.Enqueue(21000);
        hx.CalibrateScale(500.0, 1);
        chip.EndRead();
        Check.Near("hx711 scale", 40.0, hx.Scale, 1e-9);
        chip.Conversions.Enqueue(9000);
        Check.Near("hx711 weight", 200.0, hx.ReadWeight(1), 1e-9);
        chip.EndRead();
        bool threw = false;
        try
        {
            hx.ReadRaw();
        }
        catch (Exception)
        {
            threw = true;
        }
        Check.True("hx711 not ready throws", threw);
    }

    public static void RunNau7802()
    {
        SimI2cDevice sim = new SimI2cDevice(0x2A);
        sim.SetRegister(0x1F, 0x0F);
        sim.OnWrite = new Nau7802Model().OnWrite;
        // ADC output 0x123456, MSB first.
        sim.SetRegister(0x12, 0x12);
        sim.SetRegister(0x13, 0x34);
        sim.SetRegister(0x14, 0x56);
        Nau7802 n = new Nau7802(sim);
        Check.True("nau7802 begin", n.Begin());
        // LDO 3.3 V (VLDO=100) and gain 128 (GAINS=111): 0x27; 80 SPS (CRS=011): 0x30.
        Check.Equal("nau7802 ctrl1", 0x27, sim.Registers[0x01]);
        Check.Equal("nau7802 ctrl2", 0x30, sim.Registers[0x02]);
        Check.Equal("nau7802 clk chopper off", 0x30, sim.Registers[0x15]);
        Check.Equal("nau7802 pga cap", 0x80, sim.Registers[0x1C] & 0x80);
        Check.Equal("nau7802 cycle start", 0xBE, sim.Registers[0x00] & 0xFE);
        Check.Equal("nau7802 raw", 0x123456, n.ReadRaw());
        sim.SetRegister(0x12, 0xFF);
        sim.SetRegister(0x13, 0xFF);
        sim.SetRegister(0x14, 0xFF);
        Check.Equal("nau7802 negative", -1, n.ReadRaw());
        // Full scale ±0.5·VREF/gain: 2^22 counts at 3.3 V, ×128 = 0.25 × 3300/128 mV.
        Check.Near("nau7802 mV", 0.25 * 3300.0 / 128.0, n.RawToMillivolts(4194304, 3300), 1e-9);
        SimI2cDevice wrong = new SimI2cDevice(0x2A);
        Check.True("nau7802 absent", !new Nau7802(wrong).Begin());
    }
}

/// <summary>NAU7802 behaviour: power-up ready after PUD, conversions ready, calibration completes at once.</summary>
public class Nau7802Model
{
    public void OnWrite(SimI2cDevice d, byte[] w)
    {
        if (w.Length == 2 && w[0] == 0x00 && (w[1] & 0x02) != 0)
        {
            d.Registers[0x00] = (byte)(w[1] | 0x08 | 0x20); // PUR, CR
        }
        if (w.Length == 2 && w[0] == 0x02 && (w[1] & 0x04) != 0)
        {
            d.Registers[0x02] = (byte)(w[1] & ~0x04); // CALS clears
        }
    }
}
