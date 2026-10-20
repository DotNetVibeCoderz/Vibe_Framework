namespace RustNet.Community.Bus;

/// <summary>
/// A register-file I2C device for testing drivers without hardware. The
/// first byte of a write sets the register pointer and any further bytes are
/// stored from there (auto-incrementing, like nearly every sensor); a read
/// returns registers from the pointer onwards. Command-style devices
/// (Sensirion, AHT20, SCD4x…) queue replies in <see cref="Responses"/> or
/// react through <see cref="OnWrite"/>.
/// </summary>
public class SimI2cDevice : I2cDevice
{
    public byte[] Registers = new byte[256];
    public int Pointer;

    /// <summary>Every write, in order.</summary>
    public List<byte[]> Writes = new List<byte[]>();

    /// <summary>Replies served (in order) to reads before falling back to the register file.</summary>
    public Queue<byte[]> Responses = new Queue<byte[]>();

    /// <summary>Called after each write is recorded — lets a test model device behaviour.</summary>
    public Action<SimI2cDevice, byte[]>? OnWrite;

    /// <summary>Set false to make every transfer fail as a NACK would.</summary>
    public bool Present = true;

    public SimI2cDevice(int address)
    {
        Address = address;
    }

    public override void Write(byte[] data)
    {
        if (!Present)
        {
            throw new InvalidOperationException("i2c nack");
        }
        Writes.Add(data);
        if (data.Length > 0)
        {
            Pointer = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                Registers[(Pointer + i - 1) & 0xFF] = data[i];
            }
        }
        OnWrite?.Invoke(this, data);
    }

    public override byte[] Read(int length)
    {
        if (!Present)
        {
            throw new InvalidOperationException("i2c nack");
        }
        byte[] r = new byte[length];
        if (Responses.Count > 0)
        {
            byte[] q = Responses.Dequeue();
            for (int i = 0; i < length && i < q.Length; i++)
            {
                r[i] = q[i];
            }
            return r;
        }
        for (int i = 0; i < length; i++)
        {
            r[i] = Registers[(Pointer + i) & 0xFF];
        }
        return r;
    }

    public void SetRegister(int register, int value) => Registers[register & 0xFF] = (byte)value;

    /// <summary>Store a 16-bit value big-endian at <paramref name="register"/>.</summary>
    public void SetRegister16(int register, int value)
    {
        Registers[register & 0xFF] = (byte)(value >> 8);
        Registers[(register + 1) & 0xFF] = (byte)value;
    }

    /// <summary>Store a 16-bit value little-endian at <paramref name="register"/>.</summary>
    public void SetRegister16Le(int register, int value)
    {
        Registers[register & 0xFF] = (byte)value;
        Registers[(register + 1) & 0xFF] = (byte)(value >> 8);
    }

    /// <summary>Queue a reply made of 16-bit words, each followed by its Sensirion CRC.</summary>
    public void QueueSensirionWords(int[] words)
    {
        byte[] r = new byte[words.Length * 3];
        for (int i = 0; i < words.Length; i++)
        {
            r[i * 3] = (byte)(words[i] >> 8);
            r[i * 3 + 1] = (byte)words[i];
            r[i * 3 + 2] = (byte)BitOps.Crc8Sensirion(r, i * 3, 2);
        }
        Responses.Enqueue(r);
    }

    /// <summary>The last write that started with <paramref name="register"/>, or null.</summary>
    public byte[]? LastWriteTo(int register)
    {
        for (int i = Writes.Count - 1; i >= 0; i--)
        {
            if (Writes[i].Length > 0 && Writes[i][0] == (byte)register)
            {
                return Writes[i];
            }
        }
        return null;
    }
}

/// <summary>An SPI device that records every transaction and answers through <see cref="Responder"/>.</summary>
public class SimSpiDevice : SpiDevice
{
    public List<byte[]> Transactions = new List<byte[]>();

    /// <summary>Every byte ever written, concatenated — handy for display drivers.</summary>
    public List<byte> Bytes = new List<byte>();

    /// <summary>Produces the bytes shifted in for a transfer; null answers zeros.</summary>
    public Func<byte[], byte[]>? Responder;

    public int SelectCount;

    public SimSpiDevice()
    {
    }

    public override byte[] Transfer(byte[] data)
    {
        Transactions.Add(data);
        for (int i = 0; i < data.Length; i++)
        {
            Bytes.Add(data[i]);
        }
        if (Responder != null)
        {
            byte[] r = Responder(data);
            byte[] fixedLength = new byte[data.Length];
            for (int i = 0; i < data.Length && i < r.Length; i++)
            {
                fixedLength[i] = r[i];
            }
            return fixedLength;
        }
        return new byte[data.Length];
    }

    public override void Write(byte[] data) => Transfer(data);

    public override void Select(bool active)
    {
        if (active)
        {
            SelectCount++;
        }
    }
}

/// <summary>An output pin that remembers what was written.</summary>
public class SimDigitalOutput : DigitalOutput
{
    public List<bool> History = new List<bool>();

    public SimDigitalOutput()
    {
    }

    public override void Write(bool high)
    {
        State = high;
        History.Add(high);
    }
}

/// <summary>An input pin whose level the test sets (or computes per read).</summary>
public class SimDigitalInput : DigitalInput
{
    public bool Level;
    public Func<bool>? Source;

    public SimDigitalInput()
    {
    }

    public override bool Read() => Source != null ? Source() : Level;
}

/// <summary>A PWM channel that only records its duty.</summary>
public class SimPwmOutput : PwmOutput
{
    public List<int> History = new List<int>();

    public SimPwmOutput(int frequencyHz)
    {
        FrequencyHz = frequencyHz;
    }

    public override void SetDuty(int permyriad)
    {
        if (permyriad < 0)
        {
            permyriad = 0;
        }
        if (permyriad > 10000)
        {
            permyriad = 10000;
        }
        Duty = permyriad;
        History.Add(permyriad);
    }
}

/// <summary>An ADC channel reading whatever the test sets.</summary>
public class SimAnalogInput : AnalogInput
{
    public int Raw;
    public int Millivolts;

    public SimAnalogInput()
    {
    }

    public override int ReadRaw() => Raw;

    public override int ReadMillivolts() => Millivolts;
}

/// <summary>A UART with an inbox the test fills and an outbox it inspects.</summary>
public class SimSerialDevice : SerialDevice
{
    public List<byte> Inbox = new List<byte>();
    public List<byte> Outbox = new List<byte>();

    public SimSerialDevice()
    {
    }

    public override void Write(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            Outbox.Add(data[i]);
        }
    }

    public override byte[] Read(int maxBytes)
    {
        int n = Inbox.Count < maxBytes ? Inbox.Count : maxBytes;
        byte[] r = new byte[n];
        for (int i = 0; i < n; i++)
        {
            r[i] = Inbox[i];
        }
        Inbox.RemoveRange(0, n);
        return r;
    }

    public void Receive(string text)
    {
        byte[] b = System.Text.Encoding.UTF8.GetBytes(text);
        for (int i = 0; i < b.Length; i++)
        {
            Inbox.Add(b[i]);
        }
    }
}
