using RustNet.Community.Bus;

namespace RustNet.Community.Mcp9600;

/// <summary>
/// Microchip MCP9600 / MCP9601 (MCP960x) thermocouple EMF to temperature
/// converter over I2C. Address 0x60..0x67 (0x67 on most breakouts). It
/// linearises types K, J, T, N, S, E, B and R itself; temperatures are
/// 16-bit two's complement in 1/16 °C (0.0625 °C).
/// </summary>
public class Mcp9600
{
    public const int DefaultAddress = 0x67;
    public const int DeviceIdMcp9600 = 0x40;
    public const int DeviceIdMcp9601 = 0x41;

    public const int RegHotJunction = 0x00;
    public const int RegJunctionDelta = 0x01;
    public const int RegColdJunction = 0x02;
    public const int RegRawAdc = 0x03;
    public const int RegStatus = 0x04;
    public const int RegSensorConfig = 0x05;
    public const int RegDeviceConfig = 0x06;
    public const int RegDeviceId = 0x20;

    /// <summary>Thermocouple type codes (sensor configuration bits 6:4).</summary>
    public const int TypeK = 0;
    public const int TypeJ = 1;
    public const int TypeT = 2;
    public const int TypeN = 3;
    public const int TypeS = 4;
    public const int TypeE = 5;
    public const int TypeB = 6;
    public const int TypeR = 7;

    /// <summary>ADC resolution codes (device configuration bits 6:5).</summary>
    public const int Adc18Bit = 0;
    public const int Adc16Bit = 1;
    public const int Adc14Bit = 2;
    public const int Adc12Bit = 3;

    /// <summary>STATUS bits.</summary>
    public const int StatusBurstComplete = 0x80;
    public const int StatusThUpdate = 0x40;
    public const int StatusInputRange = 0x10;

    private readonly I2cDevice _dev;

    public int DeviceId;
    public int Revision;

    public Mcp9600(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Mcp9600(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Mcp9600(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Read the device ID (0x40 MCP9600, 0x41 MCP9601) and revision. False when neither answers.</summary>
    public bool Begin()
    {
        try
        {
            byte[] id = _dev.ReadRegisters(RegDeviceId, 2);
            DeviceId = id[0];
            Revision = id[1];
            return DeviceId == DeviceIdMcp9600 || DeviceId == DeviceIdMcp9601;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Thermocouple type (Type* code) and digital filter coefficient 0 (off) .. 7 (maximum).</summary>
    public void SetThermocouple(int type, int filter) => _dev.WriteRegister(RegSensorConfig, ((type & 7) << 4) | (filter & 7));

    /// <summary>
    /// Device configuration: cold-junction resolution (true = 0.25 °C, false = 0.0625 °C), ADC resolution code,
    /// burst samples code (1,2,4..128 as 0..7) and mode (0 normal, 1 shutdown, 2 burst).
    /// </summary>
    public void SetDeviceConfig(bool coldJunctionCoarse, int adcResolution, int burstSamples, int mode)
    {
        _dev.WriteRegister(RegDeviceConfig,
            (coldJunctionCoarse ? 0x80 : 0) | ((adcResolution & 3) << 5) | ((burstSamples & 7) << 2) | (mode & 3));
    }

    /// <summary>Hot-junction (thermocouple, cold-junction compensated) temperature in °C.</summary>
    public double ReadHotJunctionCelsius() => DecodeTemperature(_dev.ReadUInt16BigEndian(RegHotJunction));

    /// <summary>Junction temperature delta (hot minus cold) in °C.</summary>
    public double ReadDeltaCelsius() => DecodeTemperature(_dev.ReadUInt16BigEndian(RegJunctionDelta));

    /// <summary>Cold-junction (device ambient) temperature in °C.</summary>
    public double ReadColdJunctionCelsius() => DecodeTemperature(_dev.ReadUInt16BigEndian(RegColdJunction));

    /// <summary>Datasheet: T = UpperByte · 16 + LowerByte / 16, minus 4096 when negative — i.e. int16 / 16.</summary>
    public static double DecodeTemperature(int raw16) => BitOps.ToInt16(raw16) / 16.0;

    /// <summary>Raw thermocouple EMF in µV (ADC data, 2 µV/LSB at 18-bit, sign-extended 24-bit).</summary>
    public int ReadRawMicrovolts()
    {
        byte[] d = _dev.ReadRegisters(RegRawAdc, 3);
        return BitOps.SignExtend((d[0] << 16) | (d[1] << 8) | d[2], 24) * 2;
    }

    public int ReadStatus() => _dev.ReadRegister(RegStatus);

    /// <summary>True when the input exceeds the thermocouple range (open or shorted thermocouple).</summary>
    public bool InputOutOfRange() => (ReadStatus() & StatusInputRange) != 0;

    /// <summary>Clear the burst-complete and TH-update flags.</summary>
    public void ClearStatus() => _dev.WriteRegister(RegStatus, 0x00);
}
