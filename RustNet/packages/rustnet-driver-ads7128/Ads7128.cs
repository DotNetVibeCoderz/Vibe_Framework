using RustNet.Community.Bus;

namespace RustNet.Community.Ads7128;

/// <summary>
/// TI ADS7128: 8 channels, each an analog input (12-bit SAR) or GPIO, on
/// I2C. Every register access starts with an opcode byte:
/// <code>
/// 0x10 single register read    [0x10, reg] then read 1 byte
/// 0x08 single register write   [0x08, reg, value]
/// 0x18 set bits                [0x18, reg, mask]
/// 0x20 clear bits              [0x20, reg, mask]
/// 0x30 continuous read  / 0x28 continuous write (block)
/// </code>
/// Registers used: SYSTEM_STATUS 0x00, GENERAL_CFG 0x01 (bit0 RST, bit1 CAL),
/// DATA_CFG 0x02, OSR_CFG 0x03, OPMODE_CFG 0x04, PIN_CFG 0x05 (1 = GPIO),
/// GPIO_CFG 0x07 (1 = output), GPO_DRIVE_CFG 0x09 (1 = push-pull),
/// GPO_VALUE 0x0B, GPI_VALUE 0x0D, SEQUENCE_CFG 0x10, CHANNEL_SEL 0x11.
/// In manual mode a read frame after CHANNEL_SEL returns the conversion:
/// 12 bits left-aligned in two bytes (D11..D4, D3..D0 then 4 zero bits).
/// Address 0x10..0x17 set by the ADDR resistor; reference = AVDD.
/// </summary>
public class Ads7128
{
    public const int DefaultAddress = 0x10;

    public const int OpRead = 0x10;
    public const int OpWrite = 0x08;
    public const int OpSetBits = 0x18;
    public const int OpClearBits = 0x20;
    public const int OpBlockRead = 0x30;
    public const int OpBlockWrite = 0x28;

    public const int RegSystemStatus = 0x00;
    public const int RegGeneralCfg = 0x01;
    public const int RegDataCfg = 0x02;
    public const int RegOsrCfg = 0x03;
    public const int RegOpmodeCfg = 0x04;
    public const int RegPinCfg = 0x05;
    public const int RegGpioCfg = 0x07;
    public const int RegGpoDriveCfg = 0x09;
    public const int RegGpoValue = 0x0B;
    public const int RegGpiValue = 0x0D;
    public const int RegSequenceCfg = 0x10;
    public const int RegChannelSel = 0x11;

    private readonly I2cDevice _dev;

    /// <summary>Reference (AVDD) in millivolts.</summary>
    public int ReferenceMillivolts = 3300;

    public Ads7128(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Ads7128(I2cDevice device)
    {
        _dev = device;
    }

    private void Op(int opcode, int register, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)opcode;
        b[1] = (byte)register;
        b[2] = (byte)value;
        _dev.Write(b);
    }

    public void WriteRegister(int register, int value) => Op(OpWrite, register, value);

    public void SetBits(int register, int mask) => Op(OpSetBits, register, mask);

    public void ClearBits(int register, int mask) => Op(OpClearBits, register, mask);

    public int ReadRegister(int register)
    {
        byte[] b = new byte[2];
        b[0] = OpRead;
        b[1] = (byte)register;
        return _dev.WriteRead(b, 1)[0];
    }

    /// <summary>Software reset, then manual mode with all pins analog. Returns false if the chip does not answer.</summary>
    public bool Begin()
    {
        SetBits(RegGeneralCfg, 0x01);
        Timing.SleepMs(1);
        WriteRegister(RegPinCfg, 0x00);
        WriteRegister(RegSequenceCfg, 0x00);
        // SYSTEM_STATUS bit 7 is reserved and reads 1: a cheap presence check.
        return (ReadRegister(RegSystemStatus) & 0x80) != 0;
    }

    /// <summary>Run the offset calibration (GENERAL_CFG.CAL, self-clearing).</summary>
    public void Calibrate()
    {
        SetBits(RegGeneralCfg, 0x02);
        for (int i = 0; i < 10 && (ReadRegister(RegGeneralCfg) & 0x02) != 0; i++)
        {
            Timing.SleepMs(1);
        }
    }

    /// <summary>Convert <paramref name="channel"/> (manual mode) and return the 12-bit code.</summary>
    public int ReadRaw(int channel)
    {
        WriteRegister(RegChannelSel, channel & 0x0F);
        byte[] d = _dev.Read(2);
        return (d[0] << 4) | (d[1] >> 4);
    }

    /// <summary>Code to millivolts: code × AVDD / 4096.</summary>
    public double ToMillivolts(int code) => code * (double)ReferenceMillivolts / 4096.0;

    public double ReadMillivolts(int channel) => ToMillivolts(ReadRaw(channel));

    /// <summary>Make <paramref name="pin"/> a push-pull (or open-drain) digital output.</summary>
    public void ConfigureDigitalOutput(int pin, bool pushPull = true)
    {
        int mask = 1 << pin;
        SetBits(RegPinCfg, mask);
        SetBits(RegGpioCfg, mask);
        if (pushPull)
        {
            SetBits(RegGpoDriveCfg, mask);
        }
        else
        {
            ClearBits(RegGpoDriveCfg, mask);
        }
    }

    public void ConfigureDigitalInput(int pin)
    {
        int mask = 1 << pin;
        SetBits(RegPinCfg, mask);
        ClearBits(RegGpioCfg, mask);
    }

    public void ConfigureAnalog(int pin) => ClearBits(RegPinCfg, 1 << pin);

    public void WriteDigital(int pin, bool high)
    {
        if (high)
        {
            SetBits(RegGpoValue, 1 << pin);
        }
        else
        {
            ClearBits(RegGpoValue, 1 << pin);
        }
    }

    public bool ReadDigital(int pin) => (ReadRegister(RegGpiValue) & (1 << pin)) != 0;
}
