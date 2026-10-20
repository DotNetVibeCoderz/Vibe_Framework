using RustNet.Community.Bus;
using RustNet.Community.Lm75;

namespace DriversApp;

public static class Lm75Smoke
{
    public static void Run()
    {
        // Pointer registers are 16-bit and overlap in the byte-addressed simulator: serve them from the queue.
        SimI2cDevice sim = new SimI2cDevice(0x48);
        sim.Responses.Enqueue(Hex.Bytes("50 00")); // TOS default 80 C
        sim.Responses.Enqueue(Hex.Bytes("4B 00")); // THYST default 75 C
        Lm75 lm = new Lm75(sim);
        Check.True("lm75 begin", lm.Begin());

        // Datasheet 9-bit table: +125 = 0x0FA, +25 = 0x032, -0.5 = 0x1FF, -55 = 0x192 (left-justified).
        sim.Responses.Enqueue(Hex.Bytes("7D 00"));
        Check.Near("lm75 +125", 125.0, lm.ReadTemperatureCelsius(), 0.0001);
        sim.Responses.Enqueue(Hex.Bytes("19 00"));
        Check.Near("lm75 +25", 25.0, lm.ReadTemperatureCelsius(), 0.0001);
        sim.Responses.Enqueue(Hex.Bytes("FF 80"));
        Check.Near("lm75 -0.5", -0.5, lm.ReadTemperatureCelsius(), 0.0001);
        sim.Responses.Enqueue(Hex.Bytes("C9 00"));
        Check.Near("lm75 -55", -55.0, lm.ReadTemperatureCelsius(), 0.0001);

        // LM75B 11-bit table: -0.125 = 0x7FF, -54.875 = 0x649.
        lm.ResolutionBits = 11;
        sim.Responses.Enqueue(Hex.Bytes("FF E0"));
        Check.Near("lm75b -0.125", -0.125, lm.ReadTemperatureCelsius(), 0.0001);
        sim.Responses.Enqueue(Hex.Bytes("C9 20"));
        Check.Near("lm75b -54.875", -54.875, lm.ReadTemperatureCelsius(), 0.0001);

        lm.SetLimits(80.0, 75.0);
        Check.True("lm75 tos write", Hex.Wrote(sim, "03 50 00"));
        Check.True("lm75 thyst write", Hex.Wrote(sim, "02 4B 00"));
        Check.Equal("lm75 encode -25 (0x1CE << 7)", 0xE700, Lm75.EncodeSetPoint(-25.0));
        lm.Configure(false, true, false, 2);
        Check.True("lm75 config", Hex.Wrote(sim, "01 12"));
    }
}
