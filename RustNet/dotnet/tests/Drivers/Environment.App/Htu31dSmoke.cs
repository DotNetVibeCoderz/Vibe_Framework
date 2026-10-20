using RustNet.Community.Bus;
using RustNet.Community.Htu31d;

namespace DriversApp;

public static class Htu31dSmoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x40);
        sim.Responses.Enqueue(Hex.Bytes("12 34 56 C1"));
        Htu31d htu = new Htu31d(sim);
        Check.True("htu31d begin", htu.Begin());
        Check.Equal("htu31d serial", 0x123456, htu.SerialNumber);
        Check.True("htu31d reset", Hex.Wrote(sim, "1E"));

        // S_T 0x5555 -> -40 + 165/3 = 15.000 C; S_RH 0x8000 -> 50.0008 %.
        sim.Responses.Enqueue(Hex.Bytes("55 55 1B 80 00 23"));
        htu.Measure();
        Check.True("htu31d conversion osr3/3", Hex.Wrote(sim, "5E"));
        Check.Near("htu31d temperature", 15.0, htu.TemperatureCelsius, 0.0005);
        Check.Near("htu31d humidity", 50.000763, htu.HumidityPercent, 0.0005);

        SimI2cDevice bad = new SimI2cDevice(0x40);
        bad.Responses.Enqueue(Hex.Bytes("12 34 56 C2"));
        Check.True("htu31d bad crc", !new Htu31d(bad).Begin());
    }
}
