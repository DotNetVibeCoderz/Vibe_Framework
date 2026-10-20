using RustNet.Community.Bus;

namespace RustNet.Community.Vc0706;

/// <summary>
/// VC0706 / VC0703 serial JPEG camera (Adafruit TTL Serial Camera,
/// LinkSprite LS-Y201) on a UART, 38400 baud by default. Commands are
/// 0x56, serial number, command, argument length, arguments; replies are
/// 0x76, serial number, command, status (0 = OK), data length, data.
/// Capture = stop the frame buffer, read its length, read it in chunks,
/// resume video.
/// </summary>
public class Vc0706
{
    public const int Size640x480 = 0x00;
    public const int Size320x240 = 0x11;
    public const int Size160x120 = 0x22;

    private const int CmdGetVersion = 0x11;
    private const int CmdSetPort = 0x24;
    private const int CmdReset = 0x26;
    private const int CmdReadData = 0x30;
    private const int CmdWriteData = 0x31;
    private const int CmdReadFbuf = 0x32;
    private const int CmdGetFbufLen = 0x34;
    private const int CmdFbufCtrl = 0x36;

    private readonly SerialDevice _serial;
    private readonly List<byte> _rx = new List<byte>();

    /// <summary>Camera serial number byte (0 unless changed).</summary>
    public int SerialNumber;

    /// <summary>Read polls (1 ms apart) allowed per reply.</summary>
    public int ReplyPolls = 500;

    /// <summary>Inter-chunk delay the camera inserts, in 0.01 ms units (Adafruit uses 10).</summary>
    public int ReadDelay = 10;

    /// <summary>Data bytes of the last reply.</summary>
    public byte[] LastReply = new byte[0];

    public Vc0706(int uartPort) : this(new SerialDevice(uartPort, 38400))
    {
    }

    public Vc0706(SerialDevice serial)
    {
        _serial = serial;
    }

    private void Send(int cmd, byte[] args)
    {
        byte[] b = new byte[args.Length + 4];
        b[0] = 0x56;
        b[1] = (byte)SerialNumber;
        b[2] = (byte)cmd;
        b[3] = (byte)args.Length;
        for (int i = 0; i < args.Length; i++)
        {
            b[4 + i] = args[i];
        }
        _rx.Clear();
        _serial.Write(b);
    }

    /// <summary>Wait until <paramref name="count"/> bytes are buffered; false on timeout.</summary>
    private bool Fill(int count)
    {
        int polls = 0;
        while (_rx.Count < count)
        {
            byte[] d = _serial.Read(count - _rx.Count);
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

    /// <summary>Read and check a reply header (76 sn cmd 00 len), then its data into <see cref="LastReply"/>.</summary>
    private bool Reply(int cmd)
    {
        if (!Fill(5))
        {
            return false;
        }
        if (_rx[0] != 0x76 || _rx[1] != SerialNumber || _rx[2] != cmd || _rx[3] != 0)
        {
            return false;
        }
        int len = _rx[4];
        if (!Fill(5 + len))
        {
            return false;
        }
        LastReply = new byte[len];
        for (int i = 0; i < len; i++)
        {
            LastReply[i] = _rx[5 + i];
        }
        _rx.RemoveRange(0, 5 + len);
        return true;
    }

    private bool Run(int cmd, byte[] args)
    {
        Send(cmd, args);
        return Reply(cmd);
    }

    private static byte[] Args(int a)
    {
        byte[] r = new byte[1];
        r[0] = (byte)a;
        return r;
    }

    /// <summary>System reset (the camera then prints its boot banner, which <see cref="Reply"/> skips on the next command).</summary>
    public bool Reset()
    {
        bool ok = Run(CmdReset, new byte[0]);
        Timing.SleepMs(500);
        _serial.Read(256); // discard the boot text
        return ok;
    }

    /// <summary>Firmware version string, e.g. "VC0703 1.00"; null on failure.</summary>
    public string? GetVersion()
    {
        if (!Run(CmdGetVersion, new byte[0]))
        {
            return null;
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < LastReply.Length; i++)
        {
            sb.Append((char)LastReply[i]);
        }
        return sb.ToString();
    }

    /// <summary>Set the JPEG resolution (<see cref="Size640x480"/> …). Takes effect after <see cref="Reset"/>.</summary>
    public bool SetImageSize(int sizeCode)
    {
        byte[] a = new byte[5];
        a[0] = 0x04; // data type 4: the camera's I2C EEPROM (persistent setting)
        a[1] = 0x01; // one byte
        a[2] = 0x00; // address 0x0019
        a[3] = 0x19;
        a[4] = (byte)sizeCode;
        return Run(CmdWriteData, a);
    }

    /// <summary>Current resolution code, or -1.</summary>
    public int GetImageSize()
    {
        byte[] a = new byte[4];
        a[0] = 0x04;
        a[1] = 0x01;
        a[2] = 0x00;
        a[3] = 0x19;
        if (!Run(CmdReadData, a) || LastReply.Length < 1)
        {
            return -1;
        }
        return LastReply[0];
    }

    /// <summary>JPEG compression ratio 0x00..0xFF (default 0x36; higher = smaller files).</summary>
    public bool SetCompression(int ratio)
    {
        byte[] a = new byte[5];
        a[0] = 0x01; // data type 1: chip register
        a[1] = 0x01; // one byte
        a[2] = 0x12; // register 0x1204
        a[3] = 0x04;
        a[4] = (byte)ratio;
        return Run(CmdWriteData, a);
    }

    /// <summary>Freeze the current frame in the buffer (FBUF_CTRL stop-current-frame).</summary>
    public bool TakePicture() => Run(CmdFbufCtrl, Args(0x00));

    /// <summary>Resume live video after a capture (FBUF_CTRL resume).</summary>
    public bool ResumeVideo() => Run(CmdFbufCtrl, Args(0x03));

    /// <summary>Length in bytes of the frozen JPEG, or -1.</summary>
    public long FrameLength()
    {
        if (!Run(CmdGetFbufLen, Args(0x00)) || LastReply.Length < 4)
        {
            return -1;
        }
        return ((long)LastReply[0] << 24) | ((long)LastReply[1] << 16) | ((long)LastReply[2] << 8) | LastReply[3];
    }

    /// <summary>
    /// Read <paramref name="count"/> bytes of the frozen JPEG from
    /// <paramref name="address"/>: reply header, data, reply header again.
    /// Returns null on timeout or a bad header.
    /// </summary>
    public byte[]? ReadChunk(long address, int count)
    {
        byte[] a = new byte[12];
        a[0] = 0x00; // frame type: current frame
        a[1] = 0x0A; // transfer mode: MCU mode (data on this UART)
        a[2] = (byte)(address >> 24);
        a[3] = (byte)(address >> 16);
        a[4] = (byte)(address >> 8);
        a[5] = (byte)address;
        a[6] = (byte)(count >> 24);
        a[7] = (byte)(count >> 16);
        a[8] = (byte)(count >> 8);
        a[9] = (byte)count;
        a[10] = (byte)(ReadDelay >> 8);
        a[11] = (byte)ReadDelay;
        Send(CmdReadFbuf, a);
        if (!Fill(5 + count + 5))
        {
            return null;
        }
        if (_rx[0] != 0x76 || _rx[2] != CmdReadFbuf || _rx[3] != 0)
        {
            return null;
        }
        byte[] data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = _rx[5 + i];
        }
        _rx.Clear();
        return data;
    }

    /// <summary>
    /// Capture a complete JPEG: freeze, read the length, read in
    /// <paramref name="chunkSize"/>-byte pieces (multiples of 8 work best),
    /// resume video. Null on failure.
    /// </summary>
    public byte[]? Capture(int chunkSize)
    {
        if (!TakePicture())
        {
            return null;
        }
        // int, not long: a long array size/index compiles to conv.ovf.i, which the interpreter lacks.
        long length = FrameLength();
        if (length <= 0 || length > 0x7FFFFFFF)
        {
            return null;
        }
        int len = (int)length;
        byte[] jpeg = new byte[len];
        int at = 0;
        while (at < len)
        {
            int n = len - at < chunkSize ? len - at : chunkSize;
            byte[]? part = ReadChunk(at, n);
            if (part == null)
            {
                return null;
            }
            for (int i = 0; i < n; i++)
            {
                jpeg[at + i] = part[i];
            }
            at += n;
        }
        ResumeVideo();
        return jpeg;
    }
}
