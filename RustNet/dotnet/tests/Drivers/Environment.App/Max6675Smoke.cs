using RustNet.Community.Bus;
using RustNet.Community.Max6675;

namespace DriversApp;

/// <summary>An SPI device that answers every transfer with one fixed 16-bit frame (MSB first).</summary>
public class FrameSpiDevice : SimSpiDevice
{
    public int Frame;

    public override byte[] Transfer(byte[] data)
    {
        Transactions.Add(data);
        byte[] r = new byte[data.Length];
        if (data.Length >= 2)
        {
            r[0] = (byte)(Frame >> 8);
            r[1] = (byte)Frame;
        }
        return r;
    }
}

public static class Max6675Smoke
{
    public static void Run()
    {
        FrameSpiDevice spi = new FrameSpiDevice();
        Max6675 tc = new Max6675(spi);
        // Bits 14:3 = 200 -> 50.00 C.
        spi.Frame = 0x0640;
        Check.True("max6675 present", tc.IsPresent());
        Check.Near("max6675 50C", 50.0, tc.ReadCelsius(), 0.0001);
        Check.True("max6675 not open", !tc.Open);
        // Full scale: 4095 x 0.25 = 1023.75 C.
        spi.Frame = 0x7FF8;
        Check.Near("max6675 full scale", 1023.75, tc.ReadCelsius(), 0.0001);
        spi.Frame = 0x0003 << 3;
        Check.Near("max6675 0.75C", 0.75, tc.ReadCelsius(), 0.0001);
        // D2 set: thermocouple input open.
        spi.Frame = 0x0644;
        double open = tc.ReadCelsius();
        Check.True("max6675 open detected", tc.Open && open != open);
        Check.Equal("max6675 2-byte frames", 2, spi.Transactions[0].Length);
    }
}
