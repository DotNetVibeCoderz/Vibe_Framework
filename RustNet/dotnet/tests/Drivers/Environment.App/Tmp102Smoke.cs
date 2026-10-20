using RustNet.Community.Bus;
using RustNet.Community.Tmp102;

namespace DriversApp;

public static class Tmp102Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x48);
        sim.Responses.Enqueue(Hex.Bytes("60 A0")); // configuration reset value
        Tmp102 tmp = new Tmp102(sim);
        Check.True("tmp102 begin", tmp.Begin());

        // Datasheet 12-bit table: 127.9375 = 0x7FF, 25 = 0x190, -0.25 = 0xFFC, -25 = 0xE70, -55 = 0xC90.
        sim.Responses.Enqueue(Hex.Bytes("7F F0"));
        Check.Near("tmp102 127.9375", 127.9375, tmp.ReadTemperatureCelsius(), 0.00001);
        sim.Responses.Enqueue(Hex.Bytes("19 00"));
        Check.Near("tmp102 25", 25.0, tmp.ReadTemperatureCelsius(), 0.00001);
        sim.Responses.Enqueue(Hex.Bytes("FF C0"));
        Check.Near("tmp102 -0.25", -0.25, tmp.ReadTemperatureCelsius(), 0.00001);
        sim.Responses.Enqueue(Hex.Bytes("E7 00"));
        Check.Near("tmp102 -25", -25.0, tmp.ReadTemperatureCelsius(), 0.00001);
        sim.Responses.Enqueue(Hex.Bytes("C9 00"));
        Check.Near("tmp102 -55", -55.0, tmp.ReadTemperatureCelsius(), 0.00001);
        // Extended mode table: 150 C = 0x0960 (13-bit), register 0x4B01 with the EM flag in bit 0.
        sim.Responses.Enqueue(Hex.Bytes("4B 01"));
        Check.Near("tmp102 extended 150", 150.0, tmp.ReadTemperatureCelsius(), 0.00001);

        sim.Responses.Enqueue(Hex.Bytes("60 A0"));
        tmp.SetLimits(75.0, 80.0);
        Check.True("tmp102 tlow", Hex.Wrote(sim, "02 4B 00"));
        Check.True("tmp102 thigh", Hex.Wrote(sim, "03 50 00"));

        sim.Responses.Enqueue(Hex.Bytes("61 A0"));
        sim.Responses.Enqueue(Hex.Bytes("19 00"));
        Check.Near("tmp102 one-shot", 25.0, tmp.OneShotCelsius(), 0.00001);
        Check.True("tmp102 os+sd", Hex.Wrote(sim, "01 E1 A0"));
    }
}
