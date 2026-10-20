using RustNet.Community.Bus;
using RustNet.Community.Mcp9600;

namespace DriversApp;

public static class Mcp9600Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x67);
        sim.SetRegister(0x20, 0x40); // device ID MCP9600
        sim.SetRegister(0x21, 0x11); // revision 1.1
        Mcp9600 mcp = new Mcp9600(sim);
        Check.True("mcp9600 begin", mcp.Begin());
        Check.Equal("mcp9600 revision", 0x11, mcp.Revision);

        mcp.SetThermocouple(Mcp9600.TypeK, 3);
        Check.True("mcp9600 type K filter 3", Hex.Wrote(sim, "05 03"));
        mcp.SetThermocouple(Mcp9600.TypeJ, 0);
        Check.True("mcp9600 type J", Hex.Wrote(sim, "05 10"));
        mcp.SetDeviceConfig(false, Mcp9600.Adc16Bit, 0, 0);
        Check.True("mcp9600 adc 16 bit", Hex.Wrote(sim, "06 20"));

        // 16-bit registers overlap in the byte-addressed simulator: serve them from the queue.
        // 0x0190 = 400/16 = 25.0 C; 0xFF38 = -200/16 = -12.5 C; 0x0164 = 356/16 = 22.25 C.
        sim.Responses.Enqueue(Hex.Bytes("01 90"));
        Check.Near("mcp9600 hot junction", 25.0, mcp.ReadHotJunctionCelsius(), 0.00001);
        sim.Responses.Enqueue(Hex.Bytes("FF 38"));
        Check.Near("mcp9600 negative", -12.5, mcp.ReadHotJunctionCelsius(), 0.00001);
        sim.Responses.Enqueue(Hex.Bytes("01 64"));
        Check.Near("mcp9600 cold junction", 22.25, mcp.ReadColdJunctionCelsius(), 0.00001);
        // Raw ADC: sign-extended 24-bit, 2 uV/LSB.
        sim.Responses.Enqueue(Hex.Bytes("00 03 E8"));
        Check.Equal("mcp9600 raw uV", 2000, mcp.ReadRawMicrovolts());
        sim.Responses.Enqueue(Hex.Bytes("FF FC 18"));
        Check.Equal("mcp9600 raw negative uV", -2000, mcp.ReadRawMicrovolts());
        sim.SetRegister(0x04, 0xD0);
        Check.True("mcp9600 input range", mcp.InputOutOfRange());

        SimI2cDevice other = new SimI2cDevice(0x67);
        other.SetRegister(0x20, 0x5A);
        Check.True("mcp9600 wrong id", !new Mcp9600(other).Begin());
    }
}
