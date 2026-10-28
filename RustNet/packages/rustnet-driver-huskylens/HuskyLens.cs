using RustNet.Community.Bus;

namespace RustNet.Community.HuskyLens;

/// <summary>A box the sensor reports: centre, size, and learned ID (0 = not learned).</summary>
public class HuskyBlock
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public int Id;
}

/// <summary>A line the sensor reports in line tracking: origin, target, learned ID.</summary>
public class HuskyArrow
{
    public int XOrigin;
    public int YOrigin;
    public int XTarget;
    public int YTarget;
    public int Id;
}

/// <summary>What one request returned.</summary>
public class HuskyResult
{
    /// <summary>Blocks and arrows the sensor said it would send.</summary>
    public int Count;

    /// <summary>How many IDs the current algorithm has learned.</summary>
    public int LearnedIds;

    /// <summary>The sensor's frame counter, to tell a fresh result from a repeat.</summary>
    public int Frame;

    public List<HuskyBlock> Blocks = new List<HuskyBlock>();
    public List<HuskyArrow> Arrows = new List<HuskyArrow>();
}

/// <summary>
/// DFRobot HuskyLens (SEN0305) on a UART (9600 baud by default) or on I2C at
/// 0x32 — the same protocol either way, chosen in the sensor's own menu.
///
/// Frames both ways are <c>55 AA 11 len cmd data… sum</c>, where the last byte
/// is the low byte of the sum of everything before it and values are
/// little-endian 16-bit. A request (0x20..0x28) is answered by an info frame
/// (0x29: count, learned IDs, frame number) followed by one block (0x2A) or
/// arrow (0x2B) frame per object; most other commands by OK (0x2E).
/// Coordinates are on the sensor's 320×240 screen.
/// </summary>
public class HuskyLens
{
    public const int I2cAddress = 0x32;

    public const int FaceRecognition = 0;
    public const int ObjectTracking = 1;
    public const int ObjectRecognition = 2;
    public const int LineTracking = 3;
    public const int ColorRecognition = 4;
    public const int TagRecognition = 5;
    public const int ObjectClassification = 6;

    private const int CmdRequest = 0x20;
    private const int CmdRequestBlocks = 0x21;
    private const int CmdRequestArrows = 0x22;
    private const int CmdRequestLearned = 0x23;
    private const int CmdRequestById = 0x26;
    private const int ReturnInfo = 0x29;
    private const int ReturnBlock = 0x2A;
    private const int ReturnArrow = 0x2B;
    private const int CmdKnock = 0x2C;
    private const int CmdAlgorithm = 0x2D;
    private const int ReturnOk = 0x2E;
    private const int CmdCustomName = 0x2F;
    private const int CmdTakePhoto = 0x30;
    private const int CmdCustomText = 0x34;
    private const int CmdClearText = 0x35;
    private const int CmdLearn = 0x36;
    private const int CmdForget = 0x37;
    private const int CmdSaveScreenshot = 0x39;

    private readonly SerialDevice? _serial;
    private readonly I2cDevice? _i2c;
    private readonly List<byte> _rx = new List<byte>();

    /// <summary>Read polls (1 ms apart) allowed for each reply frame.</summary>
    public int ReplyPolls = 200;

    /// <summary>Bytes discarded looking for a frame header before giving up.</summary>
    public int MaxSkipped = 64;

    /// <summary>The last frame's command byte and data, for diagnostics.</summary>
    public int LastCommand;
    public byte[] LastData = new byte[0];

    /// <summary>On a UART port, at the sensor's default 9600 baud.</summary>
    public HuskyLens(int uartPort) : this(new SerialDevice(uartPort, 9600))
    {
    }

    public HuskyLens(SerialDevice serial) : this(serial, null)
    {
    }

    // One constructor per transport would be HuskyLens(SerialDevice) and
    // HuskyLens(I2cDevice): both canonicalise to .ctor(object) on the device,
    // which the MetadataProcessor rejects. Hence a factory for I2C.
    private HuskyLens(SerialDevice? serial, I2cDevice? i2c)
    {
        _serial = serial;
        _i2c = i2c;
    }

    /// <summary>On I2C bus <paramref name="bus"/>, at the sensor's fixed address.</summary>
    public static HuskyLens OnI2c(int bus) => OnI2c(new I2cDevice(bus, I2cAddress));

    /// <summary>On an I2C device object (what tests pass a simulator as).</summary>
    public static HuskyLens OnI2c(I2cDevice device) => new HuskyLens(null, device);

    // ---- framing ----------------------------------------------------------

    /// <summary>The bytes of a command frame, checksum included.</summary>
    public static byte[] Frame(int cmd, byte[] data)
    {
        byte[] f = new byte[data.Length + 6];
        f[0] = 0x55;
        f[1] = 0xAA;
        f[2] = 0x11;
        f[3] = (byte)data.Length;
        f[4] = (byte)cmd;
        int sum = 0x55 + 0xAA + 0x11 + data.Length + cmd;
        for (int i = 0; i < data.Length; i++)
        {
            f[5 + i] = data[i];
            sum += data[i];
        }
        f[data.Length + 5] = (byte)sum;
        return f;
    }

    private void Send(int cmd, byte[] data)
    {
        _rx.Clear();
        byte[] f = Frame(cmd, data);
        if (_serial != null)
        {
            _serial.Write(f);
        }
        else if (_i2c != null)
        {
            _i2c.Write(f);
        }
    }

    private byte[] Pull(int n)
    {
        if (_serial != null)
        {
            return _serial.Read(n);
        }
        if (_i2c != null)
        {
            return _i2c.Read(n);
        }
        return new byte[0];
    }

    /// <summary>Wait until <paramref name="count"/> bytes are buffered; false on timeout.</summary>
    private bool Fill(int count)
    {
        int polls = 0;
        while (_rx.Count < count)
        {
            byte[] d = Pull(count - _rx.Count);
            if (d.Length == 0)
            {
                if (++polls > ReplyPolls)
                {
                    return false;
                }
                Timing.SleepMs(1);
                continue;
            }
            for (int i = 0; i < d.Length; i++)
            {
                _rx.Add(d[i]);
            }
        }
        return true;
    }

    /// <summary>
    /// Read one reply frame into <see cref="LastCommand"/> / <see cref="LastData"/>.
    /// Bytes before a frame header are skipped; a bad checksum fails the read.
    /// </summary>
    private bool ReadFrame()
    {
        // Resynchronise on 55 AA 11 — but not forever: an I2C read always
        // returns the bytes asked for, so a silent sensor reads as junk, not
        // as a timeout.
        int skipped = 0;
        while (true)
        {
            if (!Fill(3))
            {
                return false;
            }
            if (_rx[0] == 0x55 && _rx[1] == 0xAA && _rx[2] == 0x11)
            {
                break;
            }
            _rx.RemoveAt(0);
            if (++skipped > MaxSkipped)
            {
                return false;
            }
        }
        if (!Fill(5))
        {
            return false;
        }
        int len = _rx[3];
        if (!Fill(len + 6))
        {
            return false;
        }
        int sum = 0;
        for (int i = 0; i < len + 5; i++)
        {
            sum += _rx[i];
        }
        if ((sum & 0xFF) != _rx[len + 5])
        {
            _rx.RemoveRange(0, len + 6);
            return false;
        }
        LastCommand = _rx[4];
        LastData = new byte[len];
        for (int i = 0; i < len; i++)
        {
            LastData[i] = _rx[5 + i];
        }
        _rx.RemoveRange(0, len + 6);
        return true;
    }

    private int Word(int at) => LastData[at] | (LastData[at + 1] << 8);

    private bool Ok(int cmd, byte[] data)
    {
        Send(cmd, data);
        return ReadFrame() && LastCommand == ReturnOk;
    }

    private static byte[] Id16(int id)
    {
        byte[] d = new byte[2];
        d[0] = (byte)id;
        d[1] = (byte)(id >> 8);
        return d;
    }

    // ---- commands ---------------------------------------------------------

    /// <summary>Is a HuskyLens answering? (KNOCK, answered by OK.)</summary>
    public bool Knock() => Ok(CmdKnock, new byte[0]);

    /// <summary>Switch to one of the algorithm constants (FaceRecognition…).</summary>
    public bool SetAlgorithm(int algorithm) => Ok(CmdAlgorithm, Id16(algorithm));

    /// <summary>Everything the current algorithm sees.</summary>
    public HuskyResult? Request() => Collect(CmdRequest, new byte[0]);

    /// <summary>Boxes only.</summary>
    public HuskyResult? RequestBlocks() => Collect(CmdRequestBlocks, new byte[0]);

    /// <summary>Arrows only (line tracking).</summary>
    public HuskyResult? RequestArrows() => Collect(CmdRequestArrows, new byte[0]);

    /// <summary>Only objects with a learned ID.</summary>
    public HuskyResult? RequestLearned() => Collect(CmdRequestLearned, new byte[0]);

    /// <summary>Only objects with learned ID <paramref name="id"/>.</summary>
    public HuskyResult? RequestById(int id) => Collect(CmdRequestById, Id16(id));

    /// <summary>Learn what is in the frame now as ID <paramref name="id"/>.</summary>
    public bool Learn(int id) => Ok(CmdLearn, Id16(id));

    /// <summary>Forget everything the current algorithm has learned.</summary>
    public bool Forget() => Ok(CmdForget, new byte[0]);

    /// <summary>Name learned ID <paramref name="id"/> on the sensor's screen.</summary>
    public bool SetCustomName(int id, string name)
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(name);
        // id, name length including the terminator, the name, 0.
        byte[] d = new byte[text.Length + 3];
        d[0] = (byte)id;
        d[1] = (byte)(text.Length + 1);
        for (int i = 0; i < text.Length; i++)
        {
            d[2 + i] = text[i];
        }
        d[text.Length + 2] = 0;
        return Ok(CmdCustomName, d);
    }

    /// <summary>Draw <paramref name="text"/> on the sensor's screen at (x, y).</summary>
    public bool CustomText(string text, int x, int y)
    {
        byte[] t = System.Text.Encoding.UTF8.GetBytes(text);
        // length, x high flag (x > 255), x low byte, y, text.
        byte[] d = new byte[t.Length + 4];
        d[0] = (byte)t.Length;
        d[1] = (byte)(x > 255 ? 0xFF : 0x00);
        d[2] = (byte)(x % 256);
        d[3] = (byte)y;
        for (int i = 0; i < t.Length; i++)
        {
            d[4 + i] = t[i];
        }
        return Ok(CmdCustomText, d);
    }

    /// <summary>Remove all custom text from the screen.</summary>
    public bool ClearText() => Ok(CmdClearText, new byte[0]);

    /// <summary>Save a camera photo to the sensor's microSD card.</summary>
    public bool TakePhoto() => Ok(CmdTakePhoto, new byte[0]);

    /// <summary>Save a screenshot to the sensor's microSD card.</summary>
    public bool SaveScreenshot() => Ok(CmdSaveScreenshot, new byte[0]);

    private HuskyResult? Collect(int cmd, byte[] data)
    {
        Send(cmd, data);
        if (!ReadFrame() || LastCommand != ReturnInfo || LastData.Length < 6)
        {
            return null;
        }
        HuskyResult r = new HuskyResult();
        r.Count = Word(0);
        r.LearnedIds = Word(2);
        r.Frame = Word(4);
        for (int n = 0; n < r.Count; n++)
        {
            if (!ReadFrame() || LastData.Length < 10)
            {
                return null;
            }
            if (LastCommand == ReturnBlock)
            {
                HuskyBlock b = new HuskyBlock();
                b.X = Word(0);
                b.Y = Word(2);
                b.Width = Word(4);
                b.Height = Word(6);
                b.Id = Word(8);
                r.Blocks.Add(b);
            }
            else if (LastCommand == ReturnArrow)
            {
                HuskyArrow a = new HuskyArrow();
                a.XOrigin = Word(0);
                a.YOrigin = Word(2);
                a.XTarget = Word(4);
                a.YTarget = Word(6);
                a.Id = Word(8);
                r.Arrows.Add(a);
            }
            else
            {
                return null;
            }
        }
        return r;
    }
}
