using RustNet.Community.Bus;
using RustNet.Community.HuskyLens;

namespace DriversApp;

/// <summary>
/// The HuskyLens side of the protocol, shared by the UART and I2C simulators:
/// takes a command frame, returns the reply bytes. Frames are built here
/// independently of the driver, so a checksum or layout mistake in either
/// shows up as a mismatch.
/// </summary>
public class HuskyLensModel
{
    public int Algorithm = HuskyLens.FaceRecognition;
    public int LastLearned = -1;
    public bool Forgot;
    public string LastName = "";
    public string LastText = "";
    public int LastTextX;
    public int LastTextY;
    public int Frame = 7;
    public bool CorruptNext;

    private static void Add(List<byte> out_, int cmd, int[] words)
    {
        int start = out_.Count;
        out_.Add(0x55);
        out_.Add(0xAA);
        out_.Add(0x11);
        out_.Add((byte)(words.Length * 2));
        out_.Add((byte)cmd);
        for (int i = 0; i < words.Length; i++)
        {
            out_.Add((byte)(words[i] & 0xFF));
            out_.Add((byte)((words[i] >> 8) & 0xFF));
        }
        int sum = 0;
        for (int i = start; i < out_.Count; i++)
        {
            sum += out_[i];
        }
        out_.Add((byte)sum);
    }

    private static int[] Words5(int a, int b, int c, int d, int e)
    {
        int[] w = new int[5];
        w[0] = a;
        w[1] = b;
        w[2] = c;
        w[3] = d;
        w[4] = e;
        return w;
    }

    private void Info(List<byte> r, int count, int learned)
    {
        Add(r, 0x29, Words5(count, learned, Frame, 0, 0));
    }

    /// <summary>Answer a command frame; empty when the frame is not valid.</summary>
    public List<byte> Answer(byte[] f)
    {
        List<byte> r = new List<byte>();
        if (f.Length < 6 || f[0] != 0x55 || f[1] != 0xAA || f[2] != 0x11 || f.Length != f[3] + 6)
        {
            return r;
        }
        int sum = 0;
        for (int i = 0; i < f.Length - 1; i++)
        {
            sum += f[i];
        }
        if ((sum & 0xFF) != f[f.Length - 1])
        {
            return r;
        }
        int cmd = f[4];
        int arg = f.Length >= 8 ? f[5] | (f[6] << 8) : -1;
        int[] none = new int[0];
        if (cmd == 0x2C || cmd == 0x30 || cmd == 0x35 || cmd == 0x39)
        {
            Add(r, 0x2E, none);
        }
        else if (cmd == 0x2D)
        {
            Algorithm = arg;
            Add(r, 0x2E, none);
        }
        else if (cmd == 0x36)
        {
            LastLearned = arg;
            Add(r, 0x2E, none);
        }
        else if (cmd == 0x37)
        {
            Forgot = true;
            Add(r, 0x2E, none);
        }
        else if (cmd == 0x2F)
        {
            LastName = System.Text.Encoding.UTF8.GetString(Slice(f, 7, f[6] - 1));
            Add(r, 0x2E, none);
        }
        else if (cmd == 0x34)
        {
            LastText = System.Text.Encoding.UTF8.GetString(Slice(f, 9, f[5]));
            LastTextX = (f[6] == 0xFF ? 256 : 0) + f[7];
            LastTextY = f[8];
            Add(r, 0x2E, none);
        }
        else if (cmd == 0x20)
        {
            // One learned face and one unlearned, plus a line.
            Info(r, 3, 1);
            Add(r, 0x2A, Words5(160, 120, 50, 60, 1));
            Add(r, 0x2A, Words5(300, 40, 20, 24, 0));
            Add(r, 0x2B, Words5(10, 230, 170, 15, 2));
        }
        else if (cmd == 0x21)
        {
            Info(r, 2, 1);
            Add(r, 0x2A, Words5(160, 120, 50, 60, 1));
            Add(r, 0x2A, Words5(300, 40, 20, 24, 0));
        }
        else if (cmd == 0x26)
        {
            if (arg == 1)
            {
                Info(r, 1, 1);
                Add(r, 0x2A, Words5(160, 120, 50, 60, 1));
            }
            else
            {
                Info(r, 0, 1);
            }
        }
        if (CorruptNext && r.Count > 0)
        {
            r[r.Count - 1] = (byte)(r[r.Count - 1] ^ 0x01);
            CorruptNext = false;
        }
        Frame++;
        return r;
    }

    private static byte[] Slice(byte[] f, int at, int n)
    {
        byte[] s = new byte[n < 0 ? 0 : n];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = f[at + i];
        }
        return s;
    }
}

/// <summary>A HuskyLens on a UART; <see cref="Noise"/> bytes go out before each reply.</summary>
public class SimHuskyLensSerial : SimSerialDevice
{
    public HuskyLensModel Model = new HuskyLensModel();
    public int Noise;

    public override void Write(byte[] d)
    {
        base.Write(d);
        for (int i = 0; i < Noise; i++)
        {
            Inbox.Add(0x00);
        }
        List<byte> r = Model.Answer(d);
        for (int i = 0; i < r.Count; i++)
        {
            Inbox.Add(r[i]);
        }
    }
}

/// <summary>A HuskyLens on I2C: every read returns the bytes asked for, zeros once the reply is drained.</summary>
public class SimHuskyLensI2c : SimI2cDevice
{
    public HuskyLensModel Model = new HuskyLensModel();
    private readonly List<byte> _stream = new List<byte>();

    public SimHuskyLensI2c() : base(HuskyLens.I2cAddress)
    {
    }

    public override void Write(byte[] d)
    {
        Writes.Add(d);
        List<byte> r = Model.Answer(d);
        for (int i = 0; i < r.Count; i++)
        {
            _stream.Add(r[i]);
        }
    }

    public override byte[] Read(int length)
    {
        byte[] b = new byte[length];
        int n = _stream.Count < length ? _stream.Count : length;
        for (int i = 0; i < n; i++)
        {
            b[i] = _stream[i];
        }
        _stream.RemoveRange(0, n);
        return b;
    }
}

public static class HuskyLensSmoke
{
    private static string Hex(byte[] b)
    {
        string digits = "0123456789ABCDEF";
        string s = "";
        for (int i = 0; i < b.Length; i++)
        {
            if (i > 0)
            {
                s += " ";
            }
            s += digits[b[i] >> 4].ToString() + digits[b[i] & 0xF].ToString();
        }
        return s;
    }

    public static void Run()
    {
        // Frames from DFRobot's protocol document.
        Check.True("knock frame", Hex(HuskyLens.Frame(0x2C, new byte[0])) == "55 AA 11 00 2C 3C");
        Check.True("request-blocks frame", Hex(HuskyLens.Frame(0x21, new byte[0])) == "55 AA 11 00 21 31");
        byte[] face = new byte[2];
        Check.True("algorithm=face frame", Hex(HuskyLens.Frame(0x2D, face)) == "55 AA 11 02 2D 00 00 3F");

        // UART.
        SimHuskyLensSerial uart = new SimHuskyLensSerial();
        HuskyLens h = new HuskyLens(uart);
        h.ReplyPolls = 3;
        Check.True("uart knock", h.Knock());
        Check.True("uart set algorithm", h.SetAlgorithm(HuskyLens.ObjectTracking));
        Check.Equal("algorithm reached the sensor", HuskyLens.ObjectTracking, uart.Model.Algorithm);

        HuskyResult? all = h.Request();
        Check.True("request answered", all != null);
        if (all != null)
        {
            Check.Equal("count", 3, all.Count);
            Check.Equal("learned ids", 1, all.LearnedIds);
            Check.Equal("blocks", 2, all.Blocks.Count);
            Check.Equal("arrows", 1, all.Arrows.Count);
            Check.Equal("block x", 160, all.Blocks[0].X);
            Check.Equal("block y", 120, all.Blocks[0].Y);
            Check.Equal("block w", 50, all.Blocks[0].Width);
            Check.Equal("block h", 60, all.Blocks[0].Height);
            Check.Equal("block id", 1, all.Blocks[0].Id);
            Check.Equal("x above 255 survives", 300, all.Blocks[1].X);
            Check.Equal("arrow origin x", 10, all.Arrows[0].XOrigin);
            Check.Equal("arrow target y", 15, all.Arrows[0].YTarget);
            Check.Equal("arrow id", 2, all.Arrows[0].Id);
        }

        HuskyResult? byId = h.RequestById(1);
        Check.True("by id 1", byId != null && byId.Blocks.Count == 1 && byId.Blocks[0].Id == 1);
        HuskyResult? none = h.RequestById(5);
        Check.True("by id 5 is empty", none != null && none.Count == 0);

        Check.True("learn", h.Learn(3));
        Check.Equal("learned id", 3, uart.Model.LastLearned);
        Check.True("forget", h.Forget());
        Check.True("forgot", uart.Model.Forgot);
        Check.True("custom name", h.SetCustomName(1, "Ana"));
        Check.True("name reached the sensor", uart.Model.LastName == "Ana");
        Check.True("custom text", h.CustomText("hi", 300, 20));
        Check.True("text reached the sensor", uart.Model.LastText == "hi");
        Check.Equal("text x above 255", 300, uart.Model.LastTextX);
        Check.Equal("text y", 20, uart.Model.LastTextY);
        Check.True("clear text", h.ClearText());

        // Junk ahead of a reply is skipped; a corrupted reply is refused.
        uart.Noise = 5;
        Check.True("knock through line noise", h.Knock());
        uart.Noise = 0;
        uart.Model.CorruptNext = true;
        Check.True("bad checksum refused", !h.Knock());
        Check.True("recovers after a bad frame", h.Knock());

        // A silent UART times out instead of hanging.
        HuskyLens deaf = new HuskyLens(new SimSerialDevice());
        deaf.ReplyPolls = 3;
        Check.True("no sensor, no knock", !deaf.Knock());

        // I2C: the same protocol over reads that always return bytes.
        SimHuskyLensI2c bus = new SimHuskyLensI2c();
        HuskyLens hi = HuskyLens.OnI2c(bus);
        Check.True("i2c knock", hi.Knock());
        HuskyResult? blocks = hi.RequestBlocks();
        Check.True("i2c blocks", blocks != null && blocks.Blocks.Count == 2 && blocks.Arrows.Count == 0);
        Check.Equal("i2c address", 0x32, bus.Address);

        // A silent I2C sensor reads as zeros: give up, do not spin.
        SimI2cDevice silent = new SimI2cDevice(HuskyLens.I2cAddress);
        HuskyLens quiet = HuskyLens.OnI2c(silent);
        Check.True("silent i2c gives up", !quiet.Knock());
    }
}
