using RustNet.Community.Bus;

namespace RustNet.Community.Mpr121;

/// <summary>
/// NXP/Freescale MPR121 12-electrode capacitive touch controller at I2C
/// 0x5A..0x5D (ADDR to GND/VDD/SDA/SCL). Configured as in NXP's AN3944
/// quick-start (baseline filter values, touch 12 / release 6), with all 12
/// electrodes enabled and baseline tracking on.
/// </summary>
public class Mpr121
{
    public const int DefaultAddress = 0x5A;

    private const int RegTouchStatus = 0x00;
    private const int RegFilteredData0 = 0x04;
    private const int RegBaseline0 = 0x1E;
    private const int RegMhdRising = 0x2B;
    private const int RegTouchThreshold0 = 0x41;
    private const int RegDebounce = 0x5B;
    private const int RegConfig1 = 0x5C;
    private const int RegConfig2 = 0x5D;
    private const int RegEcr = 0x5E;
    private const int RegSoftReset = 0x80;

    private readonly I2cDevice _dev;
    private int _last;

    public int TouchThreshold = 12;
    public int ReleaseThreshold = 6;

    /// <summary>Electrode → key character for <see cref="ReadKey"/>; default "0123456789*#".</summary>
    public string KeyMap = "0123456789*#";

    /// <summary>Called with the electrode number when an electrode becomes touched / released.</summary>
    public Action<int>? Touched;
    public Action<int>? Released;

    public Mpr121(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Mpr121(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Mpr121(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>
    /// Soft-reset and verify CONFIG2 reads its reset value 0x24 (the closest
    /// thing the MPR121 has to an ID), then configure and start. False if absent.
    /// </summary>
    public bool Begin()
    {
        _dev.WriteRegister(RegSoftReset, 0x63);
        Timing.SleepMs(1);
        _dev.WriteRegister(RegEcr, 0x00); // stop mode: registers are writable
        if (_dev.ReadRegister(RegConfig2) != 0x24)
        {
            return false;
        }
        SetThresholds(TouchThreshold, ReleaseThreshold);
        // MHD, NHD, NCL, FDL for rising, falling, touched (AN3944 values).
        byte[] f = new byte[11];
        f[0] = 0x01; // MHDR
        f[1] = 0x01; // NHDR
        f[2] = 0x0E; // NCLR
        f[3] = 0x00; // FDLR
        f[4] = 0x01; // MHDF
        f[5] = 0x05; // NHDF
        f[6] = 0x01; // NCLF
        f[7] = 0x00; // FDLF
        f[8] = 0x00; // NHDT
        f[9] = 0x00; // NCLT
        f[10] = 0x00; // FDLT
        _dev.WriteRegisters(RegMhdRising, f);
        _dev.WriteRegister(RegDebounce, 0x00);
        _dev.WriteRegister(RegConfig1, 0x10); // FFI 6 samples, 16 µA
        _dev.WriteRegister(RegConfig2, 0x20); // 0.5 µs, SFI 4, ESI 1 ms
        _dev.WriteRegister(RegEcr, 0x8F); // CL = 10 (baseline from first 5 bits), 12 electrodes
        return true;
    }

    /// <summary>Touch/release thresholds for all electrodes (stop mode required; Begin handles it).</summary>
    public void SetThresholds(int touch, int release)
    {
        byte[] t = new byte[24];
        for (int i = 0; i < 12; i++)
        {
            t[i * 2] = (byte)touch;
            t[i * 2 + 1] = (byte)release;
        }
        _dev.WriteRegisters(RegTouchThreshold0, t);
    }

    /// <summary>12-bit touch mask (bit n = electrode n); bit 15 of the raw register flags over-current.</summary>
    public int ReadTouched()
    {
        byte[] d = _dev.ReadRegisters(RegTouchStatus, 2);
        return (d[0] | (d[1] << 8)) & 0x0FFF;
    }

    public bool IsTouched(int electrode) => ((ReadTouched() >> electrode) & 1) != 0;

    /// <summary>10-bit filtered electrode data.</summary>
    public int ReadFiltered(int electrode)
    {
        byte[] d = _dev.ReadRegisters(RegFilteredData0 + electrode * 2, 2);
        return (d[0] | (d[1] << 8)) & 0x3FF;
    }

    /// <summary>Baseline value (the register holds the upper 8 of 10 bits).</summary>
    public int ReadBaseline(int electrode) => _dev.ReadRegister(RegBaseline0 + electrode) << 2;

    /// <summary>Read the status, fire <see cref="Touched"/>/<see cref="Released"/> for changes, return the mask.</summary>
    public int Poll()
    {
        int now = ReadTouched();
        int changed = now ^ _last;
        for (int i = 0; i < 12; i++)
        {
            if (((changed >> i) & 1) != 0)
            {
                if (((now >> i) & 1) != 0)
                {
                    Touched?.Invoke(i);
                }
                else
                {
                    Released?.Invoke(i);
                }
            }
        }
        _last = now;
        return now;
    }

    /// <summary>The key of the lowest touched electrode via <see cref="KeyMap"/>, or '\0'.</summary>
    public char ReadKey()
    {
        int t = ReadTouched();
        for (int i = 0; i < 12 && i < KeyMap.Length; i++)
        {
            if (((t >> i) & 1) != 0)
            {
                return KeyMap[i];
            }
        }
        return '\0';
    }
}
