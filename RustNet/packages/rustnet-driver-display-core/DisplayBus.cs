using RustNet.Community.Bus;

namespace RustNet.Community.DisplayCore;

/// <summary>
/// The command/data channel of a display controller. Every write is one bus
/// transaction (one <c>byte[]</c>), so drivers batch a whole command with its
/// parameters, or a whole pixel burst, into a single call.
/// </summary>
public abstract class DisplayBus
{
    /// <summary>Send bytes the controller interprets as commands (D/C low on SPI, control byte 0x00 on I2C).</summary>
    public abstract void WriteCommands(byte[] commands);

    /// <summary>Send <paramref name="count"/> bytes of display data / command parameters (D/C high, control byte 0x40).</summary>
    public abstract void WriteData(byte[] data, int offset, int count);

    public void WriteData(byte[] data) => WriteData(data, 0, data.Length);

    public void Command(int command)
    {
        byte[] b = new byte[1];
        b[0] = (byte)command;
        WriteCommands(b);
    }

    /// <summary>A command followed by one parameter, both sent as commands (SSD1306 family style).</summary>
    public void Command(int command, int argument)
    {
        byte[] b = new byte[2];
        b[0] = (byte)command;
        b[1] = (byte)argument;
        WriteCommands(b);
    }

    public void DataByte(int value)
    {
        byte[] b = new byte[1];
        b[0] = (byte)value;
        WriteData(b, 0, 1);
    }

    /// <summary>A command (D/C low) followed by its parameters as data (D/C high) — MIPI DCS style.</summary>
    public void CommandWithData(int command, byte[] parameters)
    {
        Command(command);
        if (parameters.Length > 0)
        {
            WriteData(parameters, 0, parameters.Length);
        }
    }

    public void CommandWithData(int command, int parameter)
    {
        Command(command);
        DataByte(parameter);
    }
}

/// <summary>
/// 4-wire SPI: a D/C (data/command, a.k.a. RS or A0) GPIO selects the meaning
/// of the bytes. D/C is only written when it changes, so a run of data
/// bursts costs no extra GPIO calls.
/// </summary>
public class SpiDisplayBus : DisplayBus
{
    public SpiDevice Spi;
    public DigitalOutput Dc;

    /// <summary>Largest single SPI transfer; longer data is split. 0 = unlimited.</summary>
    public int MaxTransfer;

    private int _dcState = -1;

    public SpiDisplayBus(SpiDevice spi, DigitalOutput dc)
    {
        Spi = spi;
        Dc = dc;
    }

    /// <summary>Convenience: SPI bus + chip-select pin + D/C pin (SPI mode 0).</summary>
    public SpiDisplayBus(int spiBus, int chipSelectPin, int dcPin, int frequencyHz)
        : this(new SpiDevice(spiBus, chipSelectPin, frequencyHz, 0), new DigitalOutput(dcPin, true))
    {
    }

    private void SetDc(bool data)
    {
        int want = data ? 1 : 0;
        if (_dcState != want)
        {
            Dc.Write(data);
            _dcState = want;
        }
    }

    public override void WriteCommands(byte[] commands)
    {
        SetDc(false);
        Spi.Write(commands);
    }

    public override void WriteData(byte[] data, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }
        SetDc(true);
        int chunk = MaxTransfer > 0 && MaxTransfer < count ? MaxTransfer : count;
        if (offset == 0 && chunk == data.Length)
        {
            Spi.Write(data);
            return;
        }
        byte[] buf = new byte[chunk];
        int done = 0;
        while (done < count)
        {
            int n = count - done < chunk ? count - done : chunk;
            if (n != buf.Length)
            {
                buf = new byte[n];
            }
            for (int i = 0; i < n; i++)
            {
                buf[i] = data[offset + done + i];
            }
            Spi.Write(buf);
            done += n;
        }
    }
}

/// <summary>
/// The SSD1306-family I2C framing: every write starts with a control byte,
/// 0x00 (Co = 0, D/C# = 0) for a command stream or 0x40 (D/C# = 1) for data.
/// </summary>
public class I2cDisplayBus : DisplayBus
{
    public I2cDevice Device;
    public int CommandControl = 0x00;
    public int DataControl = 0x40;

    /// <summary>Largest payload per I2C write (excluding the control byte); 0 = unlimited.</summary>
    public int MaxChunk;

    public I2cDisplayBus(I2cDevice device)
    {
        Device = device;
    }

    public I2cDisplayBus(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public override void WriteCommands(byte[] commands)
    {
        byte[] b = new byte[commands.Length + 1];
        b[0] = (byte)CommandControl;
        for (int i = 0; i < commands.Length; i++)
        {
            b[i + 1] = commands[i];
        }
        Device.Write(b);
    }

    public override void WriteData(byte[] data, int offset, int count)
    {
        int chunk = MaxChunk > 0 && MaxChunk < count ? MaxChunk : count;
        int done = 0;
        while (done < count)
        {
            int n = count - done < chunk ? count - done : chunk;
            byte[] b = new byte[n + 1];
            b[0] = (byte)DataControl;
            for (int i = 0; i < n; i++)
            {
                b[i + 1] = data[offset + done + i];
            }
            Device.Write(b);
            done += n;
        }
    }
}

/// <summary>Reset and busy-pin handling shared by the display drivers.</summary>
public static class DisplayPins
{
    /// <summary>
    /// Hardware reset: RST high, then low for <paramref name="lowMs"/>, then
    /// high and wait <paramref name="afterMs"/>. A null pin is skipped.
    /// </summary>
    public static void ResetPulse(DigitalOutput? reset, int lowMs, int afterMs)
    {
        if (reset == null)
        {
            return;
        }
        reset.Write(true);
        Timing.SleepMs(1);
        reset.Write(false);
        Timing.SleepMs(lowMs);
        reset.Write(true);
        Timing.SleepMs(afterMs);
    }
}

/// <summary>
/// A controller BUSY output. <see cref="WaitIdle"/> polls until the pin
/// leaves its busy level or the timeout passes. The pin is read before the
/// clock is ever consulted, so an idle panel costs a single GPIO read.
/// </summary>
public class BusyPin
{
    public DigitalInput? Pin;

    /// <summary>Level that means "busy": true for SSD16xx (BUSY high), false for UC81xx/IL03xx (BUSY_N low).</summary>
    public bool BusyLevel;

    public int TimeoutMs = 20000;
    public int PollMs = 10;

    /// <summary>How many waits gave up.</summary>
    public int Timeouts;

    public BusyPin(DigitalInput? pin, bool busyLevel)
    {
        Pin = pin;
        BusyLevel = busyLevel;
    }

    public bool IsBusy() => Pin != null && Pin.Read() == BusyLevel;

    /// <summary>True once idle; false (and <see cref="Timeouts"/> incremented) on timeout. Without a pin, waits <paramref name="fallbackMs"/>.</summary>
    public bool WaitIdle(int fallbackMs)
    {
        if (Pin == null)
        {
            Timing.SleepMs(fallbackMs);
            return true;
        }
        if (Pin.Read() != BusyLevel)
        {
            return true;
        }
        long start = Timing.NowMs();
        while (Pin.Read() == BusyLevel)
        {
            if (Timing.NowMs() - start >= TimeoutMs)
            {
                Timeouts++;
                return false;
            }
            Timing.SleepMs(PollMs);
        }
        return true;
    }
}

/// <summary>
/// Grows a <c>byte[]</c> for a command sequence whose bytes depend on the
/// panel size (so it cannot be a constant table). <c>List&lt;byte&gt;.ToArray()</c>
/// does not give a real byte array on the interpreter; this does.
/// </summary>
public class ByteBuilder
{
    private byte[] _buf = new byte[32];
    public int Count;

    public ByteBuilder Add(int value)
    {
        if (Count == _buf.Length)
        {
            byte[] n = new byte[_buf.Length * 2];
            for (int i = 0; i < Count; i++)
            {
                n[i] = _buf[i];
            }
            _buf = n;
        }
        _buf[Count] = (byte)value;
        Count++;
        return this;
    }

    public ByteBuilder Add(int a, int b) => Add(a).Add(b);

    /// <summary>Append bytes written as hex ("AE D5 80").</summary>
    public ByteBuilder AddHex(string hex)
    {
        byte[] b = HexTable.Decode(hex);
        for (int i = 0; i < b.Length; i++)
        {
            Add(b[i]);
        }
        return this;
    }

    public byte[] ToArray()
    {
        byte[] r = new byte[Count];
        for (int i = 0; i < Count; i++)
        {
            r[i] = _buf[i];
        }
        return r;
    }
}
