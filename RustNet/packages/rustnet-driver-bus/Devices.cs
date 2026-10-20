using RustNet.Hal;

namespace RustNet.Community.Bus;

/// <summary>
/// Delays and time for drivers. Goes through <see cref="RustNet.Threading.Sleep"/>
/// on a device; tests and desktop simulations swap in their own clock.
/// </summary>
public static class Timing
{
    /// <summary>When set, replaces the real sleep (tests set a no-op or a fake clock).</summary>
    public static Action<int>? SleepOverride;

    /// <summary>When set, replaces the real uptime clock.</summary>
    public static Func<long>? ClockOverride;

    public static void SleepMs(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return;
        }
        if (SleepOverride != null)
        {
            SleepOverride(milliseconds);
            return;
        }
        RustNet.Threading.Sleep.Ms(milliseconds);
    }

    /// <summary>Milliseconds since boot.</summary>
    public static long NowMs()
    {
        if (ClockOverride != null)
        {
            return ClockOverride();
        }
        return RustNet.Sys.Uptime.Ms();
    }
}

/// <summary>
/// One device on an I2C bus. Every transfer goes through the three virtual
/// methods, so a driver written against this class runs unchanged against
/// <see cref="SimI2cDevice"/> — on a desktop, in a unit test, or on the
/// virtual device.
/// </summary>
public class I2cDevice
{
    public int Bus;
    public int Address;

    protected I2cDevice()
    {
    }

    public I2cDevice(int bus, int address)
    {
        Bus = bus;
        Address = address;
    }

    public virtual void Write(byte[] data) => I2c.Write(Bus, Address, data);

    public virtual byte[] Read(int length) => I2c.Read(Bus, Address, length);

    /// <summary>Write then read (register pointer, then data) — the common "read register" shape.</summary>
    public virtual byte[] WriteRead(byte[] write, int readLength)
    {
        Write(write);
        return Read(readLength);
    }

    public void WriteByte(int value)
    {
        byte[] b = new byte[1];
        b[0] = (byte)value;
        Write(b);
    }

    public void WriteRegister(int register, int value)
    {
        byte[] b = new byte[2];
        b[0] = (byte)register;
        b[1] = (byte)value;
        Write(b);
    }

    public void WriteRegisters(int register, byte[] values)
    {
        byte[] b = new byte[values.Length + 1];
        b[0] = (byte)register;
        for (int i = 0; i < values.Length; i++)
        {
            b[i + 1] = values[i];
        }
        Write(b);
    }

    public int ReadRegister(int register) => ReadRegisters(register, 1)[0];

    public byte[] ReadRegisters(int register, int count)
    {
        byte[] r = new byte[1];
        r[0] = (byte)register;
        return WriteRead(r, count);
    }

    /// <summary>Read-modify-write: replace the bits in <paramref name="mask"/> with <paramref name="value"/>.</summary>
    public void UpdateRegister(int register, int mask, int value)
    {
        int v = ReadRegister(register);
        WriteRegister(register, (v & ~mask) | (value & mask));
    }

    public int ReadUInt16BigEndian(int register)
    {
        byte[] d = ReadRegisters(register, 2);
        return (d[0] << 8) | d[1];
    }

    public int ReadUInt16LittleEndian(int register)
    {
        byte[] d = ReadRegisters(register, 2);
        return d[0] | (d[1] << 8);
    }

    public int ReadInt16BigEndian(int register) => BitOps.ToInt16(ReadUInt16BigEndian(register));

    public int ReadInt16LittleEndian(int register) => BitOps.ToInt16(ReadUInt16LittleEndian(register));

    /// <summary>Send a 16-bit command word, most significant byte first (Sensirion style).</summary>
    public void WriteCommand16(int command)
    {
        byte[] b = new byte[2];
        b[0] = (byte)(command >> 8);
        b[1] = (byte)command;
        Write(b);
    }
}

/// <summary>
/// One device on an SPI bus, with its chip-select pin (or -1 when CS is
/// wired low or handled elsewhere). CS is held low across the whole call.
/// </summary>
public class SpiDevice
{
    public int Bus;
    public int ChipSelect = -1;

    protected SpiDevice()
    {
    }

    public SpiDevice(int bus, int chipSelectPin, int frequencyHz, int mode)
    {
        Bus = bus;
        ChipSelect = chipSelectPin;
        if (chipSelectPin >= 0)
        {
            Gpio.SetMode(chipSelectPin, PinMode.Output);
            Gpio.Write(chipSelectPin, true);
        }
        Spi.Configure(bus, frequencyHz, mode);
    }

    public virtual byte[] Transfer(byte[] data)
    {
        Select(true);
        byte[] r = Spi.Transfer(Bus, data);
        Select(false);
        return r;
    }

    public virtual void Write(byte[] data)
    {
        Select(true);
        Spi.Write(Bus, data);
        Select(false);
    }

    /// <summary>Drive chip select: true = asserted (low).</summary>
    public virtual void Select(bool active)
    {
        if (ChipSelect >= 0)
        {
            Gpio.Write(ChipSelect, !active);
        }
    }

    public void WriteByte(int value)
    {
        byte[] b = new byte[1];
        b[0] = (byte)value;
        Write(b);
    }

    /// <summary>Register read the way most SPI sensors do it: address with a read bit, then dummy bytes.</summary>
    public byte[] ReadRegisters(int register, int count, int readFlag)
    {
        byte[] tx = new byte[count + 1];
        tx[0] = (byte)(register | readFlag);
        byte[] rx = Transfer(tx);
        byte[] r = new byte[count];
        for (int i = 0; i < count; i++)
        {
            r[i] = rx[i + 1];
        }
        return r;
    }

    public void WriteRegister(int register, int value)
    {
        byte[] b = new byte[2];
        b[0] = (byte)register;
        b[1] = (byte)value;
        Write(b);
    }
}

/// <summary>A GPIO configured as an output.</summary>
public class DigitalOutput
{
    public int Pin;
    public bool State;

    protected DigitalOutput()
    {
    }

    public DigitalOutput(int pin, bool initial)
    {
        Pin = pin;
        Gpio.SetMode(pin, PinMode.Output);
        Write(initial);
    }

    public virtual void Write(bool high)
    {
        State = high;
        Gpio.Write(Pin, high);
    }

    public void Toggle() => Write(!State);
}

/// <summary>A GPIO configured as an input.</summary>
public class DigitalInput
{
    public int Pin;

    protected DigitalInput()
    {
    }

    public DigitalInput(int pin, int mode)
    {
        Pin = pin;
        Gpio.SetMode(pin, mode);
    }

    public virtual bool Read() => Gpio.Read(Pin);
}

/// <summary>A PWM channel. Duty is in permyriad (0..10000 = 0..100 %).</summary>
public class PwmOutput
{
    public int Channel;
    public int FrequencyHz;
    public int Duty;

    protected PwmOutput()
    {
    }

    public PwmOutput(int channel, int frequencyHz)
    {
        Channel = channel;
        FrequencyHz = frequencyHz;
        SetDuty(0);
    }

    public virtual void SetDuty(int permyriad)
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
        Pwm.Configure(Channel, FrequencyHz, permyriad);
    }

    public void SetFrequency(int hz)
    {
        FrequencyHz = hz;
        SetDuty(Duty);
    }

    /// <summary>Duty as a fraction, 0.0..1.0.</summary>
    public void SetDutyCycle(double fraction) => SetDuty((int)(fraction * 10000.0 + 0.5));
}

/// <summary>An ADC channel.</summary>
public class AnalogInput
{
    public int Channel;

    /// <summary>Full-scale reading; 4095 for the 12-bit converters RustNet targets.</summary>
    public int MaxRaw = 4095;

    protected AnalogInput()
    {
    }

    public AnalogInput(int channel)
    {
        Channel = channel;
    }

    public virtual int ReadRaw() => Adc.ReadRaw(Channel);

    public virtual int ReadMillivolts() => Adc.ReadMillivolts(Channel);

    /// <summary>Reading as 0.0..1.0 of full scale.</summary>
    public double ReadRatio() => (double)ReadRaw() / MaxRaw;
}

/// <summary>A UART port.</summary>
public class SerialDevice
{
    public int Port;

    protected SerialDevice()
    {
    }

    public SerialDevice(int port, int baud)
    {
        Port = port;
        Uart.Configure(port, baud);
    }

    public virtual void Write(byte[] data) => Uart.Write(Port, data);

    public virtual byte[] Read(int maxBytes) => Uart.Read(Port, maxBytes);

    public void WriteText(string text) => Write(System.Text.Encoding.UTF8.GetBytes(text));
}

/// <summary>Bit and checksum helpers drivers keep needing.</summary>
public static class BitOps
{
    /// <summary>Sign-extend a 16-bit value.</summary>
    public static int ToInt16(int v)
    {
        v = v & 0xFFFF;
        return v >= 0x8000 ? v - 0x10000 : v;
    }

    /// <summary>Sign-extend an n-bit value.</summary>
    public static int SignExtend(int v, int bits)
    {
        int m = 1 << (bits - 1);
        v = v & ((1 << bits) - 1);
        return (v ^ m) - m;
    }

    /// <summary>CRC-8 with polynomial 0x31, init 0xFF — Sensirion (SHT3x/4x, SCD4x, SGP40) and AHT20.</summary>
    public static int Crc8Sensirion(byte[] data, int offset, int length)
    {
        int crc = 0xFF;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[offset + i];
            for (int b = 0; b < 8; b++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x31) & 0xFF : (crc << 1) & 0xFF;
            }
        }
        return crc;
    }

    /// <summary>BCD byte to binary (RTC registers).</summary>
    public static int FromBcd(int v) => (v >> 4) * 10 + (v & 0x0F);

    public static int ToBcd(int v) => ((v / 10) << 4) | (v % 10);
}
