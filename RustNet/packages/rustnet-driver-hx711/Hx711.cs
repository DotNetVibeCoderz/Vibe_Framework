using RustNet.Community.Bus;

namespace RustNet.Community.Hx711;

/// <summary>
/// Avia HX711 24-bit load-cell ADC on two GPIOs (DOUT input, PD_SCK output).
/// DOUT going low means a conversion is ready; 24 clock pulses shift the
/// result out MSB first (24-bit two's complement), and 1, 2 or 3 extra
/// pulses select the next conversion: channel A gain 128, channel B gain 32,
/// channel A gain 64. Holding PD_SCK high for &gt; 60 µs powers it down.
///
/// Cost: bit-banging costs ~2 host calls per bit (≈ 50 per reading), which
/// is fine at the chip's 10/80 SPS but is the main cost of a reading.
/// </summary>
public class Hx711
{
    public const int GainA128 = 1;
    public const int GainB32 = 2;
    public const int GainA64 = 3;

    private readonly DigitalInput _dout;
    private readonly DigitalOutput _sck;

    /// <summary>Extra pulses after the 24 data bits: 1 (A/128), 2 (B/32) or 3 (A/64).</summary>
    public int GainPulses = GainA128;

    /// <summary>Raw value treated as zero load (see <see cref="Tare"/>).</summary>
    public long Offset;

    /// <summary>Raw counts per unit of weight (see <see cref="CalibrateScale"/>).</summary>
    public double Scale = 1.0;

    /// <summary>Polls of DOUT (1 ms apart) before <see cref="ReadRaw"/> gives up.</summary>
    public int ReadyPolls = 200;

    public Hx711(int doutPin, int sckPin) : this(new DigitalInput(doutPin, RustNet.Hal.PinMode.Input), new DigitalOutput(sckPin, false))
    {
    }

    public Hx711(DigitalInput dout, DigitalOutput sck)
    {
        _dout = dout;
        _sck = sck;
    }

    /// <summary>A conversion is waiting (DOUT low).</summary>
    public bool IsReady() => !_dout.Read();

    /// <summary>Choose channel/gain; takes effect from the conversion after the next read.</summary>
    public void SetGain(int gainPulses)
    {
        GainPulses = gainPulses < 1 ? 1 : gainPulses > 3 ? 3 : gainPulses;
    }

    /// <summary>
    /// Wait for data, clock out 24 bits plus the gain pulses, return the signed
    /// 24-bit value. Throws when the chip never becomes ready.
    /// </summary>
    public int ReadRaw()
    {
        int polls = 0;
        while (!IsReady())
        {
            if (++polls > ReadyPolls)
            {
                throw new InvalidOperationException("hx711 not ready");
            }
            Timing.SleepMs(1);
        }
        int v = 0;
        for (int i = 0; i < 24; i++)
        {
            _sck.Write(true);
            v = (v << 1) | (_dout.Read() ? 1 : 0);
            _sck.Write(false);
        }
        for (int i = 0; i < GainPulses; i++)
        {
            _sck.Write(true);
            _sck.Write(false);
        }
        return BitOps.SignExtend(v, 24);
    }

    /// <summary>Average of <paramref name="times"/> raw readings.</summary>
    public long ReadAverage(int times)
    {
        long sum = 0;
        for (int i = 0; i < times; i++)
        {
            sum += ReadRaw();
        }
        return sum / times;
    }

    /// <summary>Record the current (unloaded) average as zero.</summary>
    public void Tare(int times) => Offset = ReadAverage(times);

    /// <summary>With a known weight on the cell, derive <see cref="Scale"/> (counts per unit).</summary>
    public void CalibrateScale(double knownWeight, int times) => Scale = (ReadAverage(times) - Offset) / knownWeight;

    /// <summary>Weight in the units used for calibration.</summary>
    public double ReadWeight(int times) => (ReadAverage(times) - Offset) / Scale;

    /// <summary>Power down (PD_SCK high &gt; 60 µs) or wake (PD_SCK low; the chip resets to channel A/128).</summary>
    public void PowerDown(bool down)
    {
        _sck.Write(false);
        if (down)
        {
            _sck.Write(true);
            Timing.SleepMs(1);
        }
    }
}
