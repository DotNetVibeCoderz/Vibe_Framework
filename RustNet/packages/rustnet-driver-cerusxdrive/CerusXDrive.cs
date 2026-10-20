using RustNet.Community.Bus;

namespace RustNet.Community.CerusXDrive;

/// <summary>
/// Modbus master transport used by the VFD driver. The default goes through
/// <see cref="RustNet.Buses.Modbus"/> (the device's RTU master); every call is
/// virtual so <see cref="SimModbusTransport"/> (or a TCP gateway) can stand in.
/// </summary>
public class ModbusTransport
{
    /// <summary>Function 0x06, write single holding register.</summary>
    public virtual void WriteRegister(int unit, int address, int value) => RustNet.Buses.Modbus.WriteRegister(unit, address, value);

    /// <summary>Function 0x10, write multiple holding registers.</summary>
    public virtual void WriteRegisters(int unit, int address, int[] values) => RustNet.Buses.Modbus.WriteRegisters(unit, address, values);

    /// <summary>Function 0x03, read holding registers.</summary>
    public virtual int[] ReadHoldingRegisters(int unit, int address, int count) => RustNet.Buses.Modbus.ReadHoldingRegisters(unit, address, count);
}

/// <summary>
/// A register-map Modbus slave for tests: holding registers live in a
/// dictionary, every write is logged as (unit, address, value).
/// </summary>
public class SimModbusTransport : ModbusTransport
{
    public Dictionary<int, int> Registers = new Dictionary<int, int>();

    /// <summary>Logged writes, three ints each: unit, address, value.</summary>
    public List<int> Writes = new List<int>();

    public int ReadCount;

    public override void WriteRegister(int unit, int address, int value)
    {
        Writes.Add(unit);
        Writes.Add(address);
        Writes.Add(value & 0xFFFF);
        Registers[address] = value & 0xFFFF;
    }

    public override void WriteRegisters(int unit, int address, int[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            WriteRegister(unit, address + i, values[i]);
        }
    }

    public override int[] ReadHoldingRegisters(int unit, int address, int count)
    {
        ReadCount++;
        int[] r = new int[count];
        for (int i = 0; i < count; i++)
        {
            r[i] = Registers.ContainsKey(address + i) ? Registers[address + i] : 0;
        }
        return r;
    }

    /// <summary>Number of logged writes.</summary>
    public int WriteCount => Writes.Count / 3;

    public int WriteAddress(int index) => Writes[index * 3 + 1];

    public int WriteValue(int index) => Writes[index * 3 + 2];
}

/// <summary>Snapshot of the drive's 0x2100..0x2106 status block.</summary>
public class CerusStatus
{
    public int ErrorCode;
    public int StatusWord;
    public double FrequencyCommandHz;
    public double OutputFrequencyHz;
    public double OutputCurrentA;
    public double DcBusVoltage;
    public double OutputVoltage;

    /// <summary>Status word bits 1..0 = 11: running.</summary>
    public bool Running => (StatusWord & 0x03) == 0x03;

    /// <summary>Status word bits 1..0 = 00: stopped.</summary>
    public bool Stopped => (StatusWord & 0x03) == 0x00;

    /// <summary>Status word bit 2: JOG active.</summary>
    public bool Jogging => (StatusWord & 0x04) != 0;

    /// <summary>Status word bits 4..3 = 11: running in reverse.</summary>
    public bool Reverse => ((StatusWord >> 3) & 0x03) == 0x03;
}

/// <summary>
/// Franklin Electric Cerus X-Drive VFD over Modbus RTU (RS-485). The drive
/// uses the Delta-compatible communication map from its manual:
/// <code>
/// 0x2000 W  run command: bits 3..0 0001 stop, 0010 run, 0011 jog+run;
///           bits 5..4 01 forward, 10 reverse, 11 change direction
/// 0x2001 W  frequency command, 0.01 Hz
/// 0x2002 W  bit 0 external fault (EF) on, bit 1 fault reset
/// 0x2100 R  error code          0x2101 R  status word
/// 0x2102 R  frequency command   0x2103 R  output frequency (0.01 Hz)
/// 0x2104 R  output current (0.1 A)   0x2105 R DC bus voltage (0.1 V)
/// 0x2106 R  output voltage (0.1 V)
/// 0x220E R  IGBT temperature (0.1 °C)   0x220F R ambient temperature (0.1 °C)
/// 0x2210 R  digital input status   0x2211 R digital output status
/// </code>
/// The drive only obeys run/frequency commands when its command and frequency
/// sources are set to RS-485 in the drive parameters.
/// </summary>
public class CerusXDrive
{
    public const int RegRunCommand = 0x2000;
    public const int RegFrequencyCommand = 0x2001;
    public const int RegFaultControl = 0x2002;
    public const int RegErrorCode = 0x2100;
    public const int RegStatusWord = 0x2101;
    public const int RegFrequencyCommandValue = 0x2102;
    public const int RegOutputFrequency = 0x2103;
    public const int RegOutputCurrent = 0x2104;
    public const int RegDcBusVoltage = 0x2105;
    public const int RegOutputVoltage = 0x2106;
    public const int RegIgbtTemperature = 0x220E;
    public const int RegAmbientTemperature = 0x220F;
    public const int RegDigitalInputStatus = 0x2210;
    public const int RegDigitalOutputStatus = 0x2211;

    public const int CommandStop = 0x0001;
    public const int CommandRun = 0x0002;
    public const int CommandJogRun = 0x0003;
    public const int DirectionForward = 0x0010;
    public const int DirectionReverse = 0x0020;
    public const int DirectionChange = 0x0030;

    private readonly ModbusTransport _bus;

    /// <summary>Modbus slave address of the drive (1..247).</summary>
    public int Unit;

    /// <summary>Uses the device's Modbus RTU master (configure the RS-485 port first).</summary>
    public CerusXDrive(int unit) : this(new ModbusTransport(), unit)
    {
    }

    public CerusXDrive(ModbusTransport transport, int unit)
    {
        _bus = transport;
        Unit = unit;
    }

    /// <summary>Start the motor in the given direction.</summary>
    public void Run(bool forward) => _bus.WriteRegister(Unit, RegRunCommand, CommandRun | (forward ? DirectionForward : DirectionReverse));

    /// <summary>Jog in the given direction at the drive's jog frequency.</summary>
    public void Jog(bool forward) => _bus.WriteRegister(Unit, RegRunCommand, CommandJogRun | (forward ? DirectionForward : DirectionReverse));

    /// <summary>Ramp to a stop.</summary>
    public void Stop() => _bus.WriteRegister(Unit, RegRunCommand, CommandStop);

    /// <summary>Reverse while running.</summary>
    public void ChangeDirection() => _bus.WriteRegister(Unit, RegRunCommand, DirectionChange);

    /// <summary>Frequency set-point in Hz (written in 0.01 Hz units).</summary>
    public void SetFrequency(double hz)
    {
        if (hz < 0)
        {
            hz = 0;
        }
        _bus.WriteRegister(Unit, RegFrequencyCommand, (int)(hz * 100.0 + 0.5));
    }

    /// <summary>Clear a latched fault.</summary>
    public void ResetFault() => _bus.WriteRegister(Unit, RegFaultControl, 0x0002);

    /// <summary>Trip the drive with an external fault (EF).</summary>
    public void ExternalFault() => _bus.WriteRegister(Unit, RegFaultControl, 0x0001);

    private int ReadOne(int register) => _bus.ReadHoldingRegisters(Unit, register, 1)[0];

    public int ReadErrorCode() => ReadOne(RegErrorCode);

    public int ReadStatusWord() => ReadOne(RegStatusWord);

    public double ReadOutputFrequency() => ReadOne(RegOutputFrequency) / 100.0;

    public double ReadOutputCurrent() => ReadOne(RegOutputCurrent) / 10.0;

    public double ReadDcBusVoltage() => ReadOne(RegDcBusVoltage) / 10.0;

    public double ReadOutputVoltage() => ReadOne(RegOutputVoltage) / 10.0;

    public double ReadIgbtTemperature() => BitOps.ToInt16(ReadOne(RegIgbtTemperature)) / 10.0;

    public double ReadAmbientTemperature() => BitOps.ToInt16(ReadOne(RegAmbientTemperature)) / 10.0;

    public int ReadDigitalInputs() => ReadOne(RegDigitalInputStatus);

    public int ReadDigitalOutputs() => ReadOne(RegDigitalOutputStatus);

    /// <summary>Read 0x2100..0x2106 in one transaction.</summary>
    public CerusStatus ReadStatus()
    {
        int[] r = _bus.ReadHoldingRegisters(Unit, RegErrorCode, 7);
        CerusStatus s = new CerusStatus();
        s.ErrorCode = r[0];
        s.StatusWord = r[1];
        s.FrequencyCommandHz = r[2] / 100.0;
        s.OutputFrequencyHz = r[3] / 100.0;
        s.OutputCurrentA = r[4] / 10.0;
        s.DcBusVoltage = r[5] / 10.0;
        s.OutputVoltage = r[6] / 10.0;
        return s;
    }
}
