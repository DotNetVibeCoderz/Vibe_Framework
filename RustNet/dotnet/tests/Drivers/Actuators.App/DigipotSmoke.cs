using RustNet.Community.Bus;
using RustNet.Community.Digipot;

namespace DriversApp;

public static class DigipotSmoke
{
    /// <summary>Command byte AD3..0 C1C0 D9D8: write 00, increment 01, decrement 10, read 11.</summary>
    public static void RunMcp41xx()
    {
        SimSpiDevice spi = Expect.Spi();
        Mcp4131 p = new Mcp4131(spi, 10000);
        p.SetWiper(0, 64);
        Expect.LastSpi("mcp4131 wiper0 = 64", "00 40", spi);
        p.SetWiper(0, 500);
        Expect.LastSpi("mcp4131 clamp to 128", "00 80", spi);
        Check.Near("mcp4131 10k at 64/128 + 75 ohm", 5075.0, p.WiperToBResistance(64), 0.001);
        Check.Equal("mcp4131 value for 2575 ohm", 32, p.ValueForResistance(2575));
        p.Decrement(0);
        Expect.LastSpi("mcp4131 decrement", "08", spi);

        SimSpiDevice spi51 = Expect.Spi();
        Mcp4151 p51 = new Mcp4151(spi51, 50000);
        p51.SetWiper(0, 256);
        Expect.LastSpi("mcp4151 full scale D8", "01 00", spi51);

        SimSpiDevice spi2 = Expect.Spi();
        Mcp4231 d = new Mcp4231(spi2, 5000);
        d.SetWiper(1, 100);
        Expect.LastSpi("mcp4231 wiper1", "10 64", spi2);
        d.Increment(1);
        Expect.LastSpi("mcp4231 increment wiper1", "14", spi2);

        SimSpiDevice spi4 = Expect.Spi();
        spi4.Responder = new Mcp4251Chip().Respond;
        Mcp4251 d51 = new Mcp4251(spi4, 100000);
        d51.SetWiper(1, 256);
        Expect.LastSpi("mcp4251 wiper1 256", "11 00", spi4);
        Check.Equal("mcp4251 read wiper1", 256, d51.ReadWiper(1));
        Expect.LastSpi("mcp4251 read command", "1F FF", spi4);
        d51.SetTerminalControl(0xFF);
        Expect.LastSpi("mcp4251 TCON", "40 FF", spi4);
    }

    public static void RunMcp401x()
    {
        SimI2cDevice sim = new SimI2cDevice(0x2F);
        Mcp401x p = new Mcp401x(sim, 10000);
        p.SetWiper(100);
        Expect.LastWrite("mcp4018 single byte", "64", sim);
        p.SetWiper(200);
        Expect.LastWrite("mcp4018 clamp 127", "7F", sim);
        byte[] r = new byte[1];
        r[0] = 0x3F;
        sim.Responses.Enqueue(r);
        Check.Equal("mcp4018 read", 63, p.ReadWiper());
        Check.Near("mcp4018 full scale", 10075.0, p.WiperToBResistance(127), 0.001);
    }

    /// <summary>MCP4451/4461 map: volatile wipers 00, 01, 06, 07; NV 02, 03, 08, 09; TCON0 04, TCON1 0A.</summary>
    public static void RunMcp44xx()
    {
        SimI2cDevice sim = new SimI2cDevice(0x2C);
        Mcp4451 p = new Mcp4451(sim, 10000);
        p.SetWiper(2, 256);
        Expect.LastWrite("mcp4451 wiper2 256 (9-bit)", "61 00", sim);
        p.SetWiper(1, 128);
        Expect.LastWrite("mcp4451 wiper1 128", "10 80", sim);
        p.Increment(3);
        Expect.LastWrite("mcp4451 increment wiper3", "74", sim);
        Check.True("mcp4451 has no NV wipers", !p.SetWiperPowerUp(0, 1));
        p.SetTerminalControl(1, 0xFF);
        Expect.LastWrite("mcp4451 TCON1", "A0 FF", sim);

        SimI2cDevice sim61 = new SimI2cDevice(0x2C);
        Mcp4461 nv = new Mcp4461(sim61, 5000);
        Check.True("mcp4461 NV write", nv.SetWiperPowerUp(3, 200));
        Expect.LastWrite("mcp4461 NV wiper3", "90 C8", sim61);
        sim61.Responses.Enqueue(Two(0x01, 0x00));
        Check.Equal("mcp4461 read wiper0", 256, nv.ReadWiper(0));
        Expect.LastWrite("mcp4461 read command", "0C", sim61);
    }

    private static byte[] Two(int a, int b)
    {
        byte[] r = new byte[2];
        r[0] = (byte)a;
        r[1] = (byte)b;
        return r;
    }
}

/// <summary>Read reply: CMDERR high (valid), D8 = 1 in byte 0, D7..D0 = 0 in byte 1 -> 256.</summary>
public class Mcp4251Chip
{
    public byte[] Respond(byte[] tx)
    {
        byte[] rx = new byte[2];
        rx[0] = 0xFF;
        rx[1] = 0x00;
        return rx;
    }
}
