using RustNet.Community.Amg8833;
using RustNet.Community.ArduCam;
using RustNet.Community.Bus;
using RustNet.Community.Vc0706;

namespace DriversApp;

/// <summary>A VC0706 answering the documented command/reply frames, holding one JPEG in its frame buffer.</summary>
public class SimVc0706 : SimSerialDevice
{
    public byte[] Jpeg;
    public int ImageSize = 0x00;
    public List<string> Commands = new List<string>();

    public SimVc0706(byte[] jpeg)
    {
        Jpeg = jpeg;
    }

    private void Reply(int cmd, byte[] data)
    {
        Inbox.Add(0x76);
        Inbox.Add(0x00);
        Inbox.Add((byte)cmd);
        Inbox.Add(0x00);
        Inbox.Add((byte)data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            Inbox.Add(data[i]);
        }
    }

    public override void Write(byte[] d)
    {
        base.Write(d);
        if (d.Length < 4 || d[0] != 0x56)
        {
            return;
        }
        int cmd = d[2];
        Commands.Add(Hexs(d));
        if (cmd == 0x11)
        {
            Reply(cmd, System.Text.Encoding.UTF8.GetBytes("VC0703 1.00"));
        }
        else if (cmd == 0x31)
        {
            if (d[4] == 0x04 && d[7] == 0x19)
            {
                ImageSize = d[8];
            }
            Reply(cmd, new byte[0]);
        }
        else if (cmd == 0x30)
        {
            byte[] r = new byte[1];
            r[0] = (byte)ImageSize;
            Reply(cmd, r);
        }
        else if (cmd == 0x36 || cmd == 0x26)
        {
            Reply(cmd, new byte[0]);
        }
        else if (cmd == 0x34)
        {
            byte[] r = new byte[4];
            r[2] = (byte)(Jpeg.Length >> 8);
            r[3] = (byte)Jpeg.Length;
            Reply(cmd, r);
        }
        else if (cmd == 0x32)
        {
            int addr = (d[6] << 24) | (d[7] << 16) | (d[8] << 8) | d[9];
            int len = (d[10] << 24) | (d[11] << 16) | (d[12] << 8) | d[13];
            Reply(cmd, new byte[0]);
            for (int i = 0; i < len; i++)
            {
                Inbox.Add(Jpeg[addr + i]);
            }
            Reply(cmd, new byte[0]);
        }
    }

    public static string Hexs(byte[] d)
    {
        string h = "0123456789ABCDEF";
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < d.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(h[d[i] >> 4]);
            sb.Append(h[d[i] & 15]);
        }
        return sb.ToString();
    }
}

/// <summary>The ArduChip: registers, CAP_DONE after a start, and a FIFO read by bursts.</summary>
public class ArduChipModel
{
    public byte[] Regs = new byte[128];
    public byte[] Fifo;
    public int ReadPointer;
    public bool LeadingDummy;

    public ArduChipModel(byte[] fifo)
    {
        Fifo = fifo;
        Regs[0x40] = 0x73;
    }

    public byte[] Respond(byte[] tx)
    {
        byte[] rx = new byte[tx.Length];
        if (tx[0] == 0x3C)
        {
            int start = 1;
            if (LeadingDummy)
            {
                start = 2;
                LeadingDummy = false;
            }
            for (int i = start; i < tx.Length; i++)
            {
                rx[i] = Fifo[ReadPointer++];
            }
            return rx;
        }
        if ((tx[0] & 0x80) != 0)
        {
            int reg = tx[0] & 0x7F;
            Regs[reg] = tx[1];
            if (reg == 0x04 && (tx[1] & 0x01) != 0)
            {
                ReadPointer = 0;
                Regs[0x41] = 0;
            }
            if (reg == 0x04 && (tx[1] & 0x02) != 0)
            {
                Regs[0x41] = 0x08; // CAP_DONE
                Regs[0x42] = (byte)Fifo.Length;
                Regs[0x43] = (byte)(Fifo.Length >> 8);
                Regs[0x44] = (byte)(0x80 | (Fifo.Length >> 16)); // bit 7 is not part of the length
            }
            return rx;
        }
        rx[1] = Regs[tx[0] & 0x7F];
        return rx;
    }
}

/// <summary>A sensor with 16-bit register addresses (OV5642).</summary>
public class SimSccb16 : I2cDevice
{
    public int[] Regs = new int[65536];
    public int Pointer;

    public SimSccb16(int address)
    {
        Address = address;
    }

    public override void Write(byte[] d)
    {
        Pointer = (d[0] << 8) | d[1];
        if (d.Length > 2)
        {
            Regs[Pointer] = d[2];
        }
    }

    public override byte[] Read(int length)
    {
        byte[] r = new byte[length];
        r[0] = (byte)Regs[Pointer];
        return r;
    }
}

public static class CameraSmoke
{
    public static void RunAmg8833()
    {
        // Datasheet output tables: pixel 12-bit two's complement at 0.25 °C,
        // thermistor 12-bit sign + magnitude at 0.0625 °C.
        Check.Near("amg8833 pixel +125", 125.0, Amg8833.PixelToCelsius(0x1F4), 1e-12);
        Check.Near("amg8833 pixel +25", 25.0, Amg8833.PixelToCelsius(0x064), 1e-12);
        Check.Near("amg8833 pixel +0.25", 0.25, Amg8833.PixelToCelsius(0x001), 1e-12);
        Check.Near("amg8833 pixel -0.25", -0.25, Amg8833.PixelToCelsius(0xFFF), 1e-12);
        Check.Near("amg8833 pixel -25", -25.0, Amg8833.PixelToCelsius(0xF9C), 1e-12);
        Check.Near("amg8833 pixel -55", -55.0, Amg8833.PixelToCelsius(0xF24), 1e-12);
        Check.Near("amg8833 therm +125", 125.0, Amg8833.ThermistorToCelsius(0x7D0), 1e-12);
        Check.Near("amg8833 therm +25", 25.0, Amg8833.ThermistorToCelsius(0x190), 1e-12);
        Check.Near("amg8833 therm -0.25", -0.25, Amg8833.ThermistorToCelsius(0x804), 1e-12);
        Check.Near("amg8833 therm -20", -20.0, Amg8833.ThermistorToCelsius(0x940), 1e-12);

        SimI2cDevice sim = new SimI2cDevice(0x69);
        for (int i = 0; i < 64; i++)
        {
            sim.SetRegister16Le(0x80 + i * 2, 0x064); // 25 °C
        }
        sim.SetRegister16Le(0x80 + 27 * 2, 0x0F0); // 60 °C hot spot
        sim.SetRegister16Le(0x80 + 63 * 2, 0xF9C); // -25 °C
        sim.SetRegister16Le(0x0E, 0x1A0); // 26 °C
        Amg8833 cam = new Amg8833(sim);
        cam.Begin();
        Check.Equal("amg8833 normal mode", 0x00, sim.Registers[0x00]);
        Check.Equal("amg8833 initial reset", 0x3F, sim.Registers[0x01]);
        Check.Equal("amg8833 10 fps", 0x00, sim.Registers[0x02]);
        double[] t = cam.ReadPixels();
        Check.Near("amg8833 pixel 0", 25.0, t[0], 1e-12);
        Check.Near("amg8833 pixel 27", 60.0, t[27], 1e-12);
        Check.Near("amg8833 pixel 63", -25.0, t[63], 1e-12);
        Check.Equal("amg8833 hottest", 27, cam.HottestPixel());
        Check.Near("amg8833 thermistor", 26.0, cam.ReadThermistor(), 1e-12);
        cam.SetMovingAverage(true);
        Check.Equal("amg8833 moving average", 0x20, sim.Registers[0x07]);
        Check.Equal("amg8833 ave lock restored", 0x00, sim.Registers[0x1F]);
    }

    private static byte[] FakeJpeg(int length)
    {
        byte[] j = new byte[length];
        for (int i = 0; i < length; i++)
        {
            j[i] = (byte)(i * 13 + 7);
        }
        j[0] = 0xFF;
        j[1] = 0xD8;
        j[length - 2] = 0xFF;
        j[length - 1] = 0xD9;
        return j;
    }

    public static void RunVc0706()
    {
        byte[] jpeg = FakeJpeg(300);
        SimVc0706 sim = new SimVc0706(jpeg);
        Vc0706 cam = new Vc0706(sim);
        cam.ReplyPolls = 2;
        string? ver = cam.GetVersion();
        Check.True("vc0706 version", ver == "VC0703 1.00");
        Check.True("vc0706 get version frame", sim.Commands[0] == "56 00 11 00");
        // Documented command frames (Adafruit/LinkSprite).
        Check.True("vc0706 set size", cam.SetImageSize(Vc0706.Size320x240));
        Check.True("vc0706 set size frame", sim.Commands[1] == "56 00 31 05 04 01 00 19 11");
        Check.Equal("vc0706 get size", 0x11, cam.GetImageSize());
        Check.True("vc0706 compression", cam.SetCompression(0x36));
        Check.True("vc0706 compression frame", sim.Commands[3] == "56 00 31 05 01 01 12 04 36");
        byte[]? img = cam.Capture(64);
        Check.True("vc0706 capture", img != null);
        if (img != null)
        {
            Check.True("vc0706 image intact", Hex.Same(jpeg, img));
        }
        Check.True("vc0706 stop frame", sim.Commands[4] == "56 00 36 01 00");
        Check.True("vc0706 fbuf len", sim.Commands[5] == "56 00 34 01 00");
        Check.True("vc0706 read fbuf", sim.Commands[6] == "56 00 32 0C 00 0A 00 00 00 00 00 00 00 40 00 0A");
        Check.True("vc0706 last chunk", sim.Commands[10] == "56 00 32 0C 00 0A 00 00 01 00 00 00 00 2C 00 0A");
        Check.True("vc0706 resume", sim.Commands[11] == "56 00 36 01 03");
    }

    public static void RunArduCam()
    {
        byte[] fifo = new byte[1000];
        byte[] jpeg = FakeJpeg(700);
        for (int i = 0; i < 700; i++)
        {
            fifo[i] = jpeg[i];
        }
        ArduChipModel chip = new ArduChipModel(fifo);
        SimSpiDevice spi = new SimSpiDevice();
        spi.Responder = chip.Respond;
        SimI2cDevice ov2640 = new SimI2cDevice(0x30);
        ov2640.SetRegister(0x0A, 0x26);
        ov2640.SetRegister(0x0B, 0x42);
        ArduCam cam = new ArduCam(spi, ov2640);
        Check.True("arducam spi test register", cam.CheckSpi());
        Check.True("arducam write bit", spi.Transactions[0][0] == 0x80 && spi.Transactions[0][1] == 0x55);
        Check.True("arducam ov2640 id", cam.DetectOv2640());
        Check.Equal("arducam sensor bank", 0x01, ov2640.Registers[0xFF]);
        byte[] table = new byte[6];
        table[0] = 0x12;
        table[1] = 0x80;
        table[2] = 0xFF;
        table[3] = 0xFF;
        table[4] = 0x13;
        table[5] = 0x01;
        cam.WriteSensorRegisters8(table);
        Check.Equal("arducam table written", 0x80, ov2640.Registers[0x12]);
        Check.Equal("arducam table stops at ff ff", 0x00, ov2640.Registers[0x13]);

        byte[]? frame = cam.CaptureFrame(256);
        Check.True("arducam capture", frame != null);
        if (frame != null)
        {
            Check.Equal("arducam fifo length (bit 7 of size3 masked)", 1000, frame.Length);
            Check.Equal("arducam jpeg length", 700, ArduCam.JpegLength(frame));
            Check.True("arducam first byte", frame[0] == 0xFF && frame[1] == 0xD8);
            Check.Equal("arducam byte 699", 0xD9, frame[699]);
        }
        Check.Equal("arducam fifo start", 0x02, chip.Regs[0x04]);
        // 1000 bytes in 256-byte bursts: 4 bursts, each 0x3C + data.
        int bursts = 0;
        for (int i = 0; i < spi.Transactions.Count; i++)
        {
            if (spi.Transactions[i][0] == 0x3C)
            {
                bursts++;
            }
        }
        Check.Equal("arducam bursts", 4, bursts);

        // Leading dummy byte variant.
        chip.LeadingDummy = true;
        cam.SkipFirstBurstByte = true;
        byte[]? again = cam.CaptureFrame(512);
        Check.True("arducam dummy skipped", again != null && again[0] == 0xFF && again[1] == 0xD8 && again[699] == 0xD9);

        SimSccb16 ov5642 = new SimSccb16(0x3C);
        ov5642.Regs[0x300A] = 0x56;
        ov5642.Regs[0x300B] = 0x42;
        ArduCam cam5 = new ArduCam(spi, ov5642);
        Check.True("arducam ov5642 id", cam5.DetectOv5642());
        byte[] t16 = new byte[6];
        t16[0] = 0x30;
        t16[1] = 0x08;
        t16[2] = 0x80;
        t16[3] = 0xFF;
        t16[4] = 0xFF;
        cam5.WriteSensorRegisters16(t16);
        Check.Equal("arducam 16-bit table", 0x80, ov5642.Regs[0x3008]);
    }
}
