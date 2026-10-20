using RustNet.Community.Bus;

namespace RustNet.Community.Si1145;

/// <summary>
/// Silicon Labs SI1145 UV index / ambient light / proximity sensor at I2C
/// address 0x60. The UV index is computed on-chip from the visible and IR
/// photodiodes once the UCOEF registers hold Silicon Labs' default
/// coefficients (0x7B 0x6B 0x01 0x00). Parameters are written through
/// PARAM_WR + a PARAM_SET command.
/// </summary>
public class Si1145
{
    public const int DefaultAddress = 0x60;
    public const int PartIdSi1145 = 0x45;
    public const int PartIdSi1146 = 0x46;
    public const int PartIdSi1147 = 0x47;

    public const int RegPartId = 0x00;
    public const int RegIntCfg = 0x03;
    public const int RegIrqEnable = 0x04;
    public const int RegHwKey = 0x07;
    public const int RegMeasRate0 = 0x08;
    public const int RegMeasRate1 = 0x09;
    public const int RegPsLed21 = 0x0F;
    public const int RegUcoef0 = 0x13;
    public const int RegParamWr = 0x17;
    public const int RegCommand = 0x18;
    public const int RegResponse = 0x20;
    public const int RegIrqStatus = 0x21;
    public const int RegAlsVisData0 = 0x22;
    public const int RegAlsIrData0 = 0x24;
    public const int RegPs1Data0 = 0x26;
    public const int RegUvIndex0 = 0x2C;
    public const int RegParamRd = 0x2E;

    public const int CmdReset = 0x01;
    public const int CmdPsAlsForce = 0x07;
    public const int CmdPsAlsAuto = 0x0F;
    public const int CmdParamQuery = 0x80;
    public const int CmdParamSet = 0xA0;

    /// <summary>Parameter RAM addresses.</summary>
    public const int ParamChList = 0x01;
    public const int ParamPs1AdcMux = 0x07;
    public const int ParamAlsIrAdcMux = 0x0E;
    public const int ParamAlsVisAdcCounter = 0x10;
    public const int ParamAlsVisAdcGain = 0x11;
    public const int ParamAlsVisAdcMisc = 0x12;
    public const int ParamAlsIrAdcCounter = 0x1D;
    public const int ParamAlsIrAdcGain = 0x1E;
    public const int ParamAlsIrAdcMisc = 0x1F;

    /// <summary>CHLIST bits.</summary>
    public const int ChannelUv = 0x80;
    public const int ChannelAux = 0x40;
    public const int ChannelAlsIr = 0x20;
    public const int ChannelAlsVis = 0x10;
    public const int ChannelPs1 = 0x01;

    private readonly I2cDevice _dev;

    public int PartId;

    /// <summary>Raw visible, IR and proximity counts and UV index ×100 from the last read.</summary>
    public int Visible, Infrared, Proximity, UvIndexX100;

    public Si1145(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Si1145(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>UV index (0..11+) from the last read.</summary>
    public double UvIndex => UvIndexX100 / 100.0;

    /// <summary>Check the part ID, reset, load UV coefficients, enable UV/VIS/IR/PS1 and start autonomous mode.</summary>
    public bool Begin()
    {
        try
        {
            PartId = _dev.ReadRegister(RegPartId);
        }
        catch (Exception)
        {
            return false;
        }
        if (PartId != PartIdSi1145 && PartId != PartIdSi1146 && PartId != PartIdSi1147)
        {
            return false;
        }
        Reset();
        byte[] uc = new byte[4];
        uc[0] = 0x7B;
        uc[1] = 0x6B;
        uc[2] = 0x01;
        uc[3] = 0x00;
        _dev.WriteRegisters(RegUcoef0, uc);
        SetParameter(ParamChList, ChannelUv | ChannelAlsIr | ChannelAlsVis | ChannelPs1);
        _dev.WriteRegister(RegIntCfg, 0x01);       // INT pin enabled
        _dev.WriteRegister(RegIrqEnable, 0x01);    // ALS interrupt
        _dev.WriteRegister(RegPsLed21, 0x03);      // LED1 22.4 mA
        SetParameter(ParamPs1AdcMux, 0x03);        // large IR photodiode
        SetParameter(ParamAlsIrAdcMux, 0x00);      // small IR photodiode
        SetParameter(ParamAlsIrAdcGain, 0x00);
        SetParameter(ParamAlsIrAdcCounter, 0x70);  // 511 clocks recovery
        SetParameter(ParamAlsIrAdcMisc, 0x20);     // high signal range
        SetParameter(ParamAlsVisAdcGain, 0x00);
        SetParameter(ParamAlsVisAdcCounter, 0x70);
        SetParameter(ParamAlsVisAdcMisc, 0x20);
        _dev.WriteRegister(RegMeasRate0, 0xFF);    // 255 x 31.25 us = 8 ms auto rate
        _dev.WriteRegister(RegCommand, CmdPsAlsAuto);
        return true;
    }

    /// <summary>Stop measurements, clear interrupts, send RESET and unlock with HW_KEY 0x17.</summary>
    public void Reset()
    {
        _dev.WriteRegister(RegMeasRate0, 0);
        _dev.WriteRegister(RegMeasRate1, 0);
        _dev.WriteRegister(RegIrqEnable, 0);
        _dev.WriteRegister(RegIntCfg, 0);
        _dev.WriteRegister(RegIrqStatus, 0xFF);
        _dev.WriteRegister(RegCommand, CmdReset);
        Timing.SleepMs(10);
        _dev.WriteRegister(RegHwKey, 0x17);
        Timing.SleepMs(10);
    }

    /// <summary>PARAM_WR = value, COMMAND = PARAM_SET | address; returns PARAM_RD.</summary>
    public int SetParameter(int address, int value)
    {
        _dev.WriteRegister(RegParamWr, value);
        _dev.WriteRegister(RegCommand, CmdParamSet | (address & 0x1F));
        return _dev.ReadRegister(RegParamRd);
    }

    public int QueryParameter(int address)
    {
        _dev.WriteRegister(RegCommand, CmdParamQuery | (address & 0x1F));
        return _dev.ReadRegister(RegParamRd);
    }

    /// <summary>One forced PS + ALS measurement (instead of autonomous mode), then <see cref="Read"/>.</summary>
    public void Force()
    {
        _dev.WriteRegister(RegCommand, CmdPsAlsForce);
        Timing.SleepMs(10);
        Read();
    }

    /// <summary>Read visible, IR, PS1 (0x22..0x27) and the UV index (0x2C..0x2D); all little-endian.</summary>
    public void Read()
    {
        byte[] d = _dev.ReadRegisters(RegAlsVisData0, 6);
        Visible = d[0] | (d[1] << 8);
        Infrared = d[2] | (d[3] << 8);
        Proximity = d[4] | (d[5] << 8);
        UvIndexX100 = _dev.ReadUInt16LittleEndian(RegUvIndex0);
    }
}
