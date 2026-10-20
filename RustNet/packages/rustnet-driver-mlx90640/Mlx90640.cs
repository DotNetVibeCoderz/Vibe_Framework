using RustNet.Community.Bus;

namespace RustNet.Community.Mlx90640;

/// <summary>
/// Calibration parameters extracted from the MLX90640 EEPROM, named as in
/// the Melexis MLX90640 API (MLX90640_ExtractParameters).
/// </summary>
public class Mlx90640Parameters
{
    public int KVdd;
    public int Vdd25;
    public double KvPtat;
    public double KtPtat;
    public int VPtat25;
    public double AlphaPtat;
    public int GainEE;
    public double Tgc;
    public double CpKv;
    public double CpKta;
    public int ResolutionEE;
    public int CalibrationModeEE;
    public double KsTa;
    public double[] KsTo = new double[5];
    public int[] Ct = new int[5];
    public int[] Alpha = new int[768];
    public int AlphaScale;
    public int[] Offset = new int[768];
    public int[] Kta = new int[768];
    public int KtaScale;
    public int[] Kv = new int[768];
    public int KvScale;
    public double[] CpAlpha = new double[2];
    public int[] CpOffset = new int[2];
    public double[] IlChessC = new double[3];

    /// <summary>Pixels the EEPROM marks as broken (all-zero word) or outliers (bit 0).</summary>
    public List<int> BrokenPixels = new List<int>();
    public List<int> OutlierPixels = new List<int>();
}

/// <summary>
/// Melexis MLX90640 32×24 far-infrared thermal sensor at I2C 0x33 (16-bit
/// register addresses, 16-bit big-endian words). This is a port of the
/// Melexis MLX90640 API: every EEPROM parameter is extracted
/// (<see cref="ExtractParameters"/>), and <see cref="CalculateTo"/> is the
/// datasheet's object-temperature calculation (supply voltage, ambient
/// temperature, gain, offset/Kta/Kv, IL/chess pattern compensation,
/// compensation pixel, sensitivity with KsTa and the four KsTo ranges).
/// Each frame from the chip holds one subpage (half the pixels), so two
/// frames make a full image.
///
/// Cost: 768 pixels × two fourth roots per frame is heavy on an
/// interpreter; expect seconds per subpage on small MCUs. Raw frames
/// (<see cref="ReadFrame"/>) are cheap.
/// </summary>
public class Mlx90640
{
    public const int DefaultAddress = 0x33;
    public const int EepromAddress = 0x2400;
    public const int EepromWords = 832;
    public const int RamAddress = 0x0400;
    public const int FrameWords = 834;

    private const int RegStatus = 0x8000;
    private const int RegControl1 = 0x800D;
    private const double ScaleAlpha = 0.000001;

    private readonly I2cDevice _dev;

    public Mlx90640Parameters Params = new Mlx90640Parameters();

    /// <summary>Words per I2C read (keep each transfer within the HAL's buffer).</summary>
    public int WordsPerRead = 64;

    public int ReadyPolls = 1000;

    /// <summary>Supply voltage and ambient temperature of the last <see cref="CalculateTo"/>.</summary>
    public double Vdd;
    public double Ta;

    /// <summary>The last full image in °C (both subpages merged), row-major 24 rows × 32 columns.</summary>
    public double[] Image = new double[768];

    public Mlx90640(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Mlx90640(I2cDevice device)
    {
        _dev = device;
    }

    // ---- I2C access ----

    public int[] ReadWords(int address, int count)
    {
        int[] r = new int[count];
        int done = 0;
        while (done < count)
        {
            int n = count - done < WordsPerRead ? count - done : WordsPerRead;
            byte[] a = new byte[2];
            a[0] = (byte)((address + done) >> 8);
            a[1] = (byte)(address + done);
            byte[] d = _dev.WriteRead(a, n * 2);
            for (int i = 0; i < n; i++)
            {
                r[done + i] = (d[i * 2] << 8) | d[i * 2 + 1];
            }
            done += n;
        }
        return r;
    }

    public int ReadWord(int address) => ReadWords(address, 1)[0];

    public void WriteWord(int address, int value)
    {
        byte[] b = new byte[4];
        b[0] = (byte)(address >> 8);
        b[1] = (byte)address;
        b[2] = (byte)(value >> 8);
        b[3] = (byte)value;
        _dev.Write(b);
    }

    /// <summary>Read the EEPROM and extract every calibration parameter. False if the EEPROM reads blank.</summary>
    public bool Begin()
    {
        int[] ee = ReadWords(EepromAddress, EepromWords);
        if (ee[51] == 0 && ee[49] == 0)
        {
            return false;
        }
        ExtractParameters(ee);
        return true;
    }

    /// <summary>Refresh rate code (control bits 9:7): 0 = 0.5 Hz, 1 = 1, 2 = 2 (default), 3 = 4, 4 = 8, 5 = 16, 6 = 32, 7 = 64 Hz.</summary>
    public void SetRefreshRate(int code)
    {
        int c = ReadWord(RegControl1);
        WriteWord(RegControl1, (c & ~0x0380) | ((code & 7) << 7));
    }

    /// <summary>ADC resolution code (control bits 11:10): 0 = 16 .. 3 = 19 bit (default 2 = 18 bit).</summary>
    public void SetResolution(int code)
    {
        int c = ReadWord(RegControl1);
        WriteWord(RegControl1, (c & ~0x0C00) | ((code & 3) << 10));
    }

    /// <summary>Chess (true, default) or interleaved (TV) reading pattern (control bit 12).</summary>
    public void SetChessMode(bool chess)
    {
        int c = ReadWord(RegControl1);
        WriteWord(RegControl1, chess ? (c | 0x1000) : (c & ~0x1000));
    }

    /// <summary>
    /// Wait for a new subpage, then read RAM (768 pixels + 64 auxiliary words)
    /// and append the control register and the subpage number, as
    /// MLX90640_GetFrameData does. Returns 834 words, or null on timeout.
    /// </summary>
    public int[]? ReadFrame()
    {
        int status = 0;
        int polls = 0;
        while ((status & 0x0008) == 0)
        {
            status = ReadWord(RegStatus);
            if ((status & 0x0008) != 0)
            {
                break;
            }
            if (++polls > ReadyPolls)
            {
                return null;
            }
            Timing.SleepMs(1);
        }
        WriteWord(RegStatus, 0x0030); // clear "new data", keep RAM overwrite enabled
        int[] ram = ReadWords(RamAddress, 832);
        int[] frame = new int[FrameWords];
        for (int i = 0; i < 832; i++)
        {
            frame[i] = ram[i];
        }
        frame[832] = ReadWord(RegControl1);
        frame[833] = status & 0x0001;
        return frame;
    }

    /// <summary>Read one subpage and update its half of <see cref="Image"/>. Returns false on timeout.</summary>
    public bool ReadImage(double emissivity)
    {
        int[]? f = ReadFrame();
        if (f == null)
        {
            return false;
        }
        double ta = GetTa(f);
        // Melexis: reflected temperature = Ta - 8 °C for a sensor in open air.
        CalculateTo(f, emissivity, ta - 8.0, Image);
        return true;
    }

    // ---- Parameter extraction (MLX90640_API.c) ----

    private static int S(int v, int bits)
    {
        int half = 1 << (bits - 1);
        return v >= half ? v - (half << 1) : v;
    }

    private static double Pow2(int n) => Math.Pow(2.0, n);

    public void ExtractParameters(int[] ee)
    {
        Mlx90640Parameters p = new Mlx90640Parameters();
        // VDD
        p.KVdd = S((ee[51] & 0xFF00) >> 8, 8) * 32;
        p.Vdd25 = (((ee[51] & 0x00FF) - 256) << 5) - 8192;
        // PTAT
        p.KvPtat = S((ee[50] & 0xFC00) >> 10, 6) / 4096.0;
        p.KtPtat = S(ee[50] & 0x03FF, 10) / 8.0;
        p.VPtat25 = S(ee[49], 16);
        p.AlphaPtat = ((ee[16] & 0xF000) >> 12) / 4.0 + 8.0;
        // Gain, TGC, resolution, KsTa
        p.GainEE = S(ee[48], 16);
        p.Tgc = S(ee[60] & 0x00FF, 8) / 32.0;
        p.ResolutionEE = (ee[56] & 0x3000) >> 12;
        p.KsTa = S((ee[60] & 0xFF00) >> 8, 8) / 8192.0;
        // KsTo and the corner temperatures
        int step = ((ee[63] & 0x3000) >> 12) * 10;
        p.Ct[0] = -40;
        p.Ct[1] = 0;
        p.Ct[2] = ((ee[63] & 0x00F0) >> 4) * step;
        p.Ct[3] = p.Ct[2] + ((ee[63] & 0x0F00) >> 8) * step;
        p.Ct[4] = 400;
        double ksToScale = Pow2((ee[63] & 0x000F) + 8);
        p.KsTo[0] = S(ee[61] & 0x00FF, 8) / ksToScale;
        p.KsTo[1] = S((ee[61] & 0xFF00) >> 8, 8) / ksToScale;
        p.KsTo[2] = S(ee[62] & 0x00FF, 8) / ksToScale;
        p.KsTo[3] = S((ee[62] & 0xFF00) >> 8, 8) / ksToScale;
        p.KsTo[4] = -0.0002;
        ExtractCp(ee, p);
        ExtractAlpha(ee, p);
        ExtractOffset(ee, p);
        ExtractKta(ee, p);
        ExtractKv(ee, p);
        // CILC
        p.CalibrationModeEE = ((ee[10] & 0x0800) >> 4) ^ 0x80;
        p.IlChessC[0] = S(ee[53] & 0x003F, 6) / 16.0;
        p.IlChessC[1] = S((ee[53] & 0x07C0) >> 6, 5) / 2.0;
        p.IlChessC[2] = S((ee[53] & 0xF800) >> 11, 5) / 8.0;
        // Deviating pixels
        for (int i = 0; i < 768; i++)
        {
            if (ee[64 + i] == 0)
            {
                p.BrokenPixels.Add(i);
            }
            else if ((ee[64 + i] & 0x0001) != 0)
            {
                p.OutlierPixels.Add(i);
            }
        }
        Params = p;
    }

    private static void ExtractCp(int[] ee, Mlx90640Parameters p)
    {
        int alphaScale = ((ee[32] & 0xF000) >> 12) + 27;
        int offsetSp0 = S(ee[58] & 0x03FF, 10);
        int offsetSp1 = S((ee[58] & 0xFC00) >> 10, 6) + offsetSp0;
        double alphaSp0 = S(ee[57] & 0x03FF, 10) / Pow2(alphaScale);
        double alphaSp1 = (1 + S((ee[57] & 0xFC00) >> 10, 6) / 128.0) * alphaSp0;
        int ktaScale1 = ((ee[56] & 0x00F0) >> 4) + 8;
        int kvScale = (ee[56] & 0x0F00) >> 8;
        p.CpKta = S(ee[59] & 0x00FF, 8) / Pow2(ktaScale1);
        p.CpKv = S((ee[59] & 0xFF00) >> 8, 8) / Pow2(kvScale);
        p.CpAlpha[0] = alphaSp0;
        p.CpAlpha[1] = alphaSp1;
        p.CpOffset[0] = offsetSp0;
        p.CpOffset[1] = offsetSp1;
    }

    private static int[] Nibbles(int[] ee, int start, int words)
    {
        int[] r = new int[words * 4];
        for (int i = 0; i < words; i++)
        {
            int w = ee[start + i];
            r[i * 4] = S(w & 0x000F, 4);
            r[i * 4 + 1] = S((w & 0x00F0) >> 4, 4);
            r[i * 4 + 2] = S((w & 0x0F00) >> 8, 4);
            r[i * 4 + 3] = S((w & 0xF000) >> 12, 4);
        }
        return r;
    }

    private static void ExtractAlpha(int[] ee, Mlx90640Parameters p)
    {
        int accRemScale = ee[32] & 0x000F;
        int accColumnScale = (ee[32] & 0x00F0) >> 4;
        int accRowScale = (ee[32] & 0x0F00) >> 8;
        int alphaScale = ((ee[32] & 0xF000) >> 12) + 30;
        int alphaRef = ee[33];
        int[] accRow = Nibbles(ee, 34, 6);
        int[] accColumn = Nibbles(ee, 40, 8);
        double[] alphaTemp = new double[768];
        double cpMean = p.Tgc * (p.CpAlpha[0] + p.CpAlpha[1]) / 2;
        double divisor = Pow2(alphaScale);
        double max = 0;
        for (int i = 0; i < 24; i++)
        {
            for (int j = 0; j < 32; j++)
            {
                int k = 32 * i + j;
                int a = S((ee[64 + k] & 0x03F0) >> 4, 6) * (1 << accRemScale);
                double v = alphaRef + (accRow[i] << accRowScale) + (accColumn[j] << accColumnScale) + a;
                v = v / divisor - cpMean;
                v = ScaleAlpha / v;
                alphaTemp[k] = v;
                if (v > max)
                {
                    max = v;
                }
            }
        }
        int scale = 0;
        while (max < 32767.4)
        {
            max *= 2;
            scale++;
        }
        double f = Pow2(scale);
        for (int k = 0; k < 768; k++)
        {
            p.Alpha[k] = (int)(alphaTemp[k] * f + 0.5);
        }
        p.AlphaScale = scale;
    }

    private static void ExtractOffset(int[] ee, Mlx90640Parameters p)
    {
        int occRemScale = ee[16] & 0x000F;
        int occColumnScale = (ee[16] & 0x00F0) >> 4;
        int occRowScale = (ee[16] & 0x0F00) >> 8;
        int offsetRef = S(ee[17], 16);
        int[] occRow = Nibbles(ee, 18, 6);
        int[] occColumn = Nibbles(ee, 24, 8);
        for (int i = 0; i < 24; i++)
        {
            for (int j = 0; j < 32; j++)
            {
                int k = 32 * i + j;
                int o = S((ee[64 + k] & 0xFC00) >> 10, 6) * (1 << occRemScale);
                p.Offset[k] = offsetRef + (occRow[i] << occRowScale) + (occColumn[j] << occColumnScale) + o;
            }
        }
    }

    private static int Split(int k) => 2 * (k / 32 - (k / 64) * 2) + k % 2;

    private static int RoundToInt(double t) => t < 0 ? (int)(t - 0.5) : (int)(t + 0.5);

    private static void ExtractKta(int[] ee, Mlx90640Parameters p)
    {
        int[] ktaRc = new int[4];
        ktaRc[0] = S((ee[54] & 0xFF00) >> 8, 8); // row odd, column odd
        ktaRc[2] = S(ee[54] & 0x00FF, 8); // row even, column odd
        ktaRc[1] = S((ee[55] & 0xFF00) >> 8, 8); // row odd, column even
        ktaRc[3] = S(ee[55] & 0x00FF, 8); // row even, column even
        int ktaScale1 = ((ee[56] & 0x00F0) >> 4) + 8;
        int ktaScale2 = ee[56] & 0x000F;
        double[] t = new double[768];
        double max = 0;
        double div = Pow2(ktaScale1);
        for (int k = 0; k < 768; k++)
        {
            int v = S((ee[64 + k] & 0x000E) >> 1, 3) * (1 << ktaScale2);
            t[k] = (ktaRc[Split(k)] + v) / div;
            double a = t[k] < 0 ? -t[k] : t[k];
            if (a > max)
            {
                max = a;
            }
        }
        int scale = 0;
        while (max < 63.4)
        {
            max *= 2;
            scale++;
        }
        double f = Pow2(scale);
        for (int k = 0; k < 768; k++)
        {
            p.Kta[k] = RoundToInt(t[k] * f);
        }
        p.KtaScale = scale;
    }

    private static void ExtractKv(int[] ee, Mlx90640Parameters p)
    {
        int[] kvT = new int[4];
        kvT[0] = S((ee[52] & 0xF000) >> 12, 4);
        kvT[2] = S((ee[52] & 0x0F00) >> 8, 4);
        kvT[1] = S((ee[52] & 0x00F0) >> 4, 4);
        kvT[3] = S(ee[52] & 0x000F, 4);
        int kvScale = (ee[56] & 0x0F00) >> 8;
        double div = Pow2(kvScale);
        double[] t = new double[768];
        double max = 0;
        for (int k = 0; k < 768; k++)
        {
            t[k] = kvT[Split(k)] / div;
            double a = t[k] < 0 ? -t[k] : t[k];
            if (a > max)
            {
                max = a;
            }
        }
        int scale = 0;
        // An all-zero Kv table would loop forever; the real API assumes it never is.
        while (max < 63.4 && max > 0)
        {
            max *= 2;
            scale++;
        }
        double f = Pow2(scale);
        for (int k = 0; k < 768; k++)
        {
            p.Kv[k] = RoundToInt(t[k] * f);
        }
        p.KvScale = scale;
    }

    // ---- Temperature calculation ----

    private static int S16(int v) => v > 32767 ? v - 65536 : v;

    /// <summary>Supply voltage in V from a frame (RAM 0x072A).</summary>
    public double GetVdd(int[] frame)
    {
        double vdd = S16(frame[810]);
        int resolutionRam = (frame[832] & 0x0C00) >> 10;
        double correction = Pow2(Params.ResolutionEE) / Pow2(resolutionRam);
        return (correction * vdd - Params.Vdd25) / Params.KVdd + 3.3;
    }

    /// <summary>Ambient (sensor) temperature in °C from a frame (PTAT 0x0720, Vbe 0x0700).</summary>
    public double GetTa(int[] frame)
    {
        double vdd = GetVdd(frame);
        double ptat = S16(frame[800]);
        double ptatArt = S16(frame[768]);
        ptatArt = ptat / (ptat * Params.AlphaPtat + ptatArt) * 262144.0;
        double ta = ptatArt / (1 + Params.KvPtat * (vdd - 3.3)) - Params.VPtat25;
        return ta / Params.KtPtat + 25;
    }

    /// <summary>
    /// Object temperatures (°C) for the pixels of the frame's subpage, written
    /// into <paramref name="result"/> (768 entries; the other subpage's pixels
    /// are left untouched). <paramref name="tr"/> is the reflected temperature.
    /// </summary>
    public void CalculateTo(int[] frame, double emissivity, double tr, double[] result)
    {
        Mlx90640Parameters p = Params;
        int subPage = frame[833];
        double vdd = GetVdd(frame);
        double ta = GetTa(frame);
        Vdd = vdd;
        Ta = ta;
        double ta4 = Math.Pow(ta + 273.15, 4);
        double tr4 = Math.Pow(tr + 273.15, 4);
        double taTr = tr4 - (tr4 - ta4) / emissivity;

        double ktaScale = Pow2(p.KtaScale);
        double kvScale = Pow2(p.KvScale);
        double alphaScale = Pow2(p.AlphaScale);

        double[] alphaCorrR = new double[4];
        alphaCorrR[0] = 1 / (1 + p.KsTo[0] * 40);
        alphaCorrR[1] = 1;
        alphaCorrR[2] = 1 + p.KsTo[1] * p.Ct[2];
        alphaCorrR[3] = alphaCorrR[2] * (1 + p.KsTo[2] * (p.Ct[3] - p.Ct[2]));

        double gain = p.GainEE / (double)S16(frame[778]);
        int mode = (frame[832] & 0x1000) >> 5;

        double taFactor = 1 + p.CpKta * (ta - 25);
        double vddFactor = 1 + p.CpKv * (vdd - 3.3);
        double[] irDataCp = new double[2];
        irDataCp[0] = S16(frame[776]) * gain - p.CpOffset[0] * taFactor * vddFactor;
        if (mode == p.CalibrationModeEE)
        {
            irDataCp[1] = S16(frame[808]) * gain - p.CpOffset[1] * taFactor * vddFactor;
        }
        else
        {
            irDataCp[1] = S16(frame[808]) * gain - (p.CpOffset[1] + p.IlChessC[0]) * taFactor * vddFactor;
        }

        for (int k = 0; k < 768; k++)
        {
            int ilPattern = k / 32 - (k / 64) * 2;
            int chessPattern = ilPattern ^ (k - (k / 2) * 2);
            int conversionPattern = ((k + 2) / 4 - (k + 3) / 4 + (k + 1) / 4 - k / 4) * (1 - 2 * ilPattern);
            int pattern = mode == 0 ? ilPattern : chessPattern;
            if (pattern != subPage)
            {
                continue;
            }
            double irData = S16(frame[k]) * gain;
            double kta = p.Kta[k] / ktaScale;
            double kv = p.Kv[k] / kvScale;
            irData = irData - p.Offset[k] * (1 + kta * (ta - 25)) * (1 + kv * (vdd - 3.3));
            if (mode != p.CalibrationModeEE)
            {
                irData = irData + p.IlChessC[2] * (2 * ilPattern - 1) - p.IlChessC[1] * conversionPattern;
            }
            irData = irData - p.Tgc * irDataCp[subPage];
            irData = irData / emissivity;

            double alphaCompensated = ScaleAlpha * alphaScale / p.Alpha[k];
            alphaCompensated = alphaCompensated * (1 + p.KsTa * (ta - 25));

            double sx = alphaCompensated * alphaCompensated * alphaCompensated * (irData + alphaCompensated * taTr);
            sx = Math.Sqrt(Math.Sqrt(sx)) * p.KsTo[1];

            double to = Math.Sqrt(Math.Sqrt(irData / (alphaCompensated * (1 - p.KsTo[1] * 273.15) + sx) + taTr)) - 273.15;
            int range;
            if (to < p.Ct[1])
            {
                range = 0;
            }
            else if (to < p.Ct[2])
            {
                range = 1;
            }
            else if (to < p.Ct[3])
            {
                range = 2;
            }
            else
            {
                range = 3;
            }
            to = Math.Sqrt(Math.Sqrt(irData / (alphaCompensated * alphaCorrR[range] * (1 + p.KsTo[range] * (to - p.Ct[range]))) + taTr)) - 273.15;
            result[k] = to;
        }
    }
}
