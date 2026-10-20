using RustNet.Community.Bus;
using RustNet.Community.Mcp9808;

namespace DriversApp;

public static class Mcp9808Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x18);
        sim.Responses.Enqueue(Hex.Bytes("00 54")); // manufacturer ID
        sim.Responses.Enqueue(Hex.Bytes("04 00")); // device ID 0x04, revision 0
        Mcp9808 mcp = new Mcp9808(sim);
        Check.True("mcp9808 begin", mcp.Begin());

        // 25.0625 C = 401/16 = 0x191, with T_CRIT and T_UPPER flags (bits 15, 14) set: 0xC191.
        sim.Responses.Enqueue(Hex.Bytes("C1 91"));
        Check.Near("mcp9808 temperature", 25.0625, mcp.ReadTemperatureCelsius(), 0.00001);
        Check.Equal("mcp9808 flags", 6, mcp.Flags);
        // -0.0625 C: 13-bit two's complement 0x1FFF.
        sim.Responses.Enqueue(Hex.Bytes("1F FF"));
        Check.Near("mcp9808 negative", -0.0625, mcp.ReadTemperatureCelsius(), 0.00001);
        Check.Near("mcp9808 -40", -40.0, Mcp9808.DecodeAmbient(0x1D80), 0.00001);
        // Limits use 0.25 C steps in bits 12:2: -20 C -> 0x1EC0, 80 C -> 0x0500.
        Check.Equal("mcp9808 limit -20", 0x1EC0, Mcp9808.EncodeLimit(-20.0));
        Check.Equal("mcp9808 limit 80", 0x0500, Mcp9808.EncodeLimit(80.0));
        mcp.SetResolution(Mcp9808.Resolution0_25);
        Check.True("mcp9808 resolution", Hex.Wrote(sim, "08 01"));

        SimI2cDevice other = new SimI2cDevice(0x18);
        other.Responses.Enqueue(Hex.Bytes("00 54"));
        other.Responses.Enqueue(Hex.Bytes("05 00"));
        Check.True("mcp9808 wrong device id", !new Mcp9808(other).Begin());
    }
}
