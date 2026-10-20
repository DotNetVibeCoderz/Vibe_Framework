using RustNet.Community.Bus;
using RustNet.Community.Ht16k33;
using RustNet.Community.Mcp23xxx;
using RustNet.Community.Pca95xx;
using RustNet.Community.Pcf857x;

namespace DriversApp;

public static class ExpanderSmoke
{
    /// <summary>MCP23017 BANK=0: IODIRA 00/B 01, IOCON 0A, GPPUA 0C, GPIOA 12/B 13, OLATA 14/B 15.</summary>
    public static void RunMcp23017()
    {
        SimI2cDevice sim = new SimI2cDevice(0x20);
        Mcp23017 io = new Mcp23017(sim);
        io.Begin();
        Expect.Write("mcp23017 IOCON", "0A 00", sim, 0);
        Expect.Write("mcp23017 IODIR both ports", "00 FF FF", sim, 1);
        Expect.Write("mcp23017 GPPU both ports", "0C 00 00", sim, 2);
        Expect.Write("mcp23017 OLAT both ports", "14 00 00", sim, 3);
        io.SetPinMode(9, true);
        Expect.LastWrite("mcp23017 GPB1 output", "01 FD", sim);
        io.WritePin(9, true);
        Expect.LastWrite("mcp23017 OLATB bit 1", "15 02", sim);
        io.SetPullUp(3, true);
        Expect.LastWrite("mcp23017 GPPUA bit 3", "0C 08", sim);
        io.SetInputInverted(8, true);
        Expect.LastWrite("mcp23017 IPOLB bit 0", "03 01", sim);
        io.WriteOutputs(0xBEEF);
        Expect.LastWrite("mcp23017 OLAT A,B sequential", "14 EF BE", sim);
        sim.SetRegister(0x12, 0x00);
        sim.SetRegister(0x13, 0x10);
        Check.True("mcp23017 read GPB4", io.ReadPin(12));
        Check.True("mcp23017 read GPB5 low", !io.ReadPin(13));
    }

    /// <summary>MCP23008: IODIR 00, IOCON 05, GPPU 06, GPIO 09, OLAT 0A.</summary>
    public static void RunMcp23008()
    {
        SimI2cDevice sim = new SimI2cDevice(0x20);
        Mcp23008 io = new Mcp23008(sim);
        io.Begin();
        Expect.Write("mcp23008 IOCON", "05 00", sim, 0);
        Expect.Write("mcp23008 IODIR", "00 FF", sim, 1);
        io.SetPinMode(2, true);
        Expect.LastWrite("mcp23008 GP2 output", "00 FB", sim);
        io.WritePin(2, true);
        Expect.LastWrite("mcp23008 OLAT", "0A 04", sim);
        io.SetPullUp(7, true);
        Expect.LastWrite("mcp23008 GPPU", "06 80", sim);
        sim.SetRegister(0x09, 0x41);
        Check.Equal("mcp23008 GPIO", 0x41, io.ReadInputs());
    }

    /// <summary>MCP23S17/S08 opcode 0100 A2 A1 A0 R/W; HAEN enabled through address 0 first.</summary>
    public static void RunMcp23s17()
    {
        SimSpiDevice spi = Expect.Spi();
        spi.Responder = new Mcp23s17Chip().Respond;
        Mcp23s17 io = new Mcp23s17(spi, 3);
        io.Begin();
        Expect.Bytes("mcp23s17 IOCON.HAEN via address 0", "40 0A 08", spi.Transactions[0]);
        Expect.Bytes("mcp23s17 IODIR at hw addr 3", "46 00 FF FF", spi.Transactions[1]);
        io.SetPinMode(0, true);
        io.WritePin(0, true);
        Expect.LastSpi("mcp23s17 OLATA write", "46 14 01", spi);
        Check.Equal("mcp23s17 read GPIOA/B", 0x1234, io.ReadInputs());
        Expect.LastSpi("mcp23s17 read opcode", "47 12 00 00", spi);

        SimSpiDevice spi8 = Expect.Spi();
        Mcp23s08 io8 = new Mcp23s08(spi8, 1);
        io8.Begin();
        Expect.Bytes("mcp23s08 IOCON.HAEN", "40 05 08", spi8.Transactions[0]);
        io8.SetPinMode(7, true);
        Expect.LastSpi("mcp23s08 IODIR at hw addr 1", "42 00 7F", spi8);
    }

    public static void RunPcf857x()
    {
        SimI2cDevice sim = new SimI2cDevice(0x20);
        Pcf8574 p = new Pcf8574(sim);
        Check.Equal("pcf8574 power-on latches high", 0xFF, p.Latch);
        p.WritePin(0, false);
        Expect.LastWrite("pcf8574 P0 low", "FE", sim);
        p.WritePin(0, true);
        p.WritePin(5, false);
        Expect.LastWrite("pcf8574 P5 low", "DF", sim);
        sim.Responses.Enqueue(Two(0xF7, 0));
        Check.True("pcf8574 P3 pulled low externally", !p.ReadPin(3));

        SimI2cDevice sim16 = new SimI2cDevice(0x21);
        Pcf8575 q = new Pcf8575(sim16);
        q.WritePin(10, false);
        Expect.LastWrite("pcf8575 P12 low (P0..7 then P10..17)", "FF FB", sim16);
        sim16.Responses.Enqueue(Two(0x00, 0x80));
        int all = q.ReadAll();
        Check.Equal("pcf8575 read order", 0x8000, all);
    }

    /// <summary>PCA9555 registers: input 0/1, output 2/3, polarity 4/5, config 6/7. PCA9554: 0..3.</summary>
    public static void RunPca95xx()
    {
        SimI2cDevice sim = new SimI2cDevice(0x20);
        sim.SetRegister(2, 0xFF);
        sim.SetRegister(3, 0xFF);
        sim.SetRegister(6, 0xFF);
        sim.SetRegister(7, 0xFF);
        Pca9555 io = new Pca9555(sim);
        io.Begin();
        io.SetPinMode(10, true);
        Expect.LastWrite("pca9555 config port 1", "07 FB", sim);
        io.WritePin(10, false);
        Expect.LastWrite("pca9555 output port 1", "03 FB", sim);
        io.WriteOutputs(0x1234);
        Expect.LastWrite("pca9555 output pair", "02 34 12", sim);
        io.SetInputInverted(1, true);
        Expect.LastWrite("pca9555 polarity port 0", "04 02", sim);
        sim.SetRegister(0, 0x00);
        sim.SetRegister(1, 0x02);
        Check.True("pca9555 input pin 9", io.ReadPin(9));

        SimI2cDevice sim8 = new SimI2cDevice(0x38);
        sim8.SetRegister(1, 0xFF);
        sim8.SetRegister(3, 0xFF);
        Pca9554 io8 = new Pca9554(sim8);
        io8.Begin();
        io8.SetPinMode(0, true);
        Expect.LastWrite("pca9554 config", "03 FE", sim8);
        io8.WritePin(0, false);
        Expect.LastWrite("pca9554 output", "01 FE", sim8);
        sim8.SetRegister(0, 0x80);
        Check.True("pca9554 input", io8.ReadPin(7));

        Tca9535 t35 = new Tca9535(new SimI2cDevice(0x20));
        Tca9555 t55 = new Tca9555(new SimI2cDevice(0x20));
        Check.Equal("tca9535 config port 1 reg", 7, t35.RegisterAddress(Pca95xx.KindConfig, 1));
        Check.Equal("tca9555 polarity port 0 reg", 4, t55.RegisterAddress(Pca95xx.KindPolarity, 0));
    }

    public static void RunHt16k33()
    {
        SimI2cDevice sim = new SimI2cDevice(0x70);
        Ht16k33 d = new Ht16k33(sim);
        d.Begin();
        Expect.Write("ht16k33 oscillator on", "21", sim, 0);
        Expect.Write("ht16k33 ROW/INT = row", "A0", sim, 1);
        Expect.Write("ht16k33 full brightness", "EF", sim, 2);
        Expect.Write("ht16k33 display on, no blink", "81", sim, 4);
        d.SetLed(3, 10, true);
        d.SetLed(0, 0, true);
        d.Show();
        Expect.LastWrite("ht16k33 RAM (COM3 ROW10 -> addr 7 bit 2)", "00 01 00 00 00 00 00 00 04 00 00 00 00 00 00 00 00", sim);
        d.SetBlink(Ht16k33.Blink1Hz);
        Expect.LastWrite("ht16k33 blink 1 Hz", "85", sim);
        d.SetBrightness(7);
        Expect.LastWrite("ht16k33 dim 8/16", "E7", sim);

        sim.SetRegister(0x40, 0x05);
        sim.SetRegister(0x41, 0x00);
        sim.SetRegister(0x42, 0x00);
        sim.SetRegister(0x43, 0x10);
        sim.SetRegister(0x44, 0xFF);
        sim.SetRegister(0x45, 0xFF);
        int[] keys = d.ReadKeys();
        Check.True("ht16k33 key data", keys[0] == 5 && keys[1] == 0x1000 && keys[2] == 0x1FFF);
        sim.SetRegister(0x60, 0xFF);
        Check.True("ht16k33 key interrupt flag", d.KeyPending());
    }

    private static byte[] Two(int a, int b)
    {
        byte[] r = new byte[2];
        r[0] = (byte)a;
        r[1] = (byte)b;
        return r;
    }
}

/// <summary>Answers a GPIOA/GPIOB sequential read at hardware address 3 with 0x34, 0x12.</summary>
public class Mcp23s17Chip
{
    public byte[] Respond(byte[] tx)
    {
        byte[] rx = new byte[tx.Length];
        if (tx[0] == 0x47 && tx[1] == 0x12)
        {
            rx[2] = 0x34;
            rx[3] = 0x12;
        }
        return rx;
    }
}
