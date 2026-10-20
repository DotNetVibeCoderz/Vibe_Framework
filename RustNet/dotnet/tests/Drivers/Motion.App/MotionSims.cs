using RustNet.Community.Bus;

namespace DriversApp;

/// <summary>
/// An I2C device whose registers are whole words (INA2xx, MAX1704x, TSC2004):
/// the first written byte selects a register, the rest of the write replaces
/// its contents, and a read returns that register's bytes. The byte-addressed
/// <see cref="SimI2cDevice"/> cannot model these, because neighbouring
/// registers there overlap.
/// </summary>
public class SimWordI2c : I2cDevice
{
    public byte[][] Regs = new byte[256][];
    public int Pointer;
    public List<byte[]> Writes = new List<byte[]>();

    /// <summary>Masks applied to the written register number (e.g. a read flag in the address byte).</summary>
    public int PointerMask = 0xFF;

    public SimWordI2c(int address)
    {
        Address = address;
    }

    public override void Write(byte[] data)
    {
        Writes.Add(data);
        if (data.Length == 0)
        {
            return;
        }
        Pointer = data[0] & PointerMask;
        if (data.Length > 1)
        {
            byte[] v = new byte[data.Length - 1];
            for (int i = 1; i < data.Length; i++)
            {
                v[i - 1] = data[i];
            }
            Regs[Pointer] = v;
        }
    }

    public override byte[] Read(int length)
    {
        byte[] r = new byte[length];
        byte[]? v = Regs[Pointer];
        if (v != null)
        {
            for (int i = 0; i < length && i < v.Length; i++)
            {
                r[i] = v[i];
            }
        }
        return r;
    }

    /// <summary>Store <paramref name="bytes"/> bytes of <paramref name="value"/> big-endian.</summary>
    public void Set(int register, long value, int bytes)
    {
        byte[] v = new byte[bytes];
        for (int i = 0; i < bytes; i++)
        {
            v[i] = (byte)(value >> (8 * (bytes - 1 - i)));
        }
        Regs[register] = v;
    }

    /// <summary>A register's contents as a big-endian number (0 when never written).</summary>
    public long Get(int register)
    {
        byte[]? v = Regs[register];
        if (v == null)
        {
            return 0;
        }
        long r = 0;
        for (int i = 0; i < v.Length; i++)
        {
            r = (r << 8) | v[i];
        }
        return r;
    }

    /// <summary>The last write of a value (not a bare pointer) to a register, or null.</summary>
    public byte[]? LastWriteTo(int register)
    {
        for (int i = Writes.Count - 1; i >= 0; i--)
        {
            if (Writes[i].Length > 1 && (Writes[i][0] & PointerMask) == register)
            {
                return Writes[i];
            }
        }
        return null;
    }
}

/// <summary>Byte helpers for test data.</summary>
public static class Hex
{
    /// <summary>"B5 62 06" or "B56206" to bytes.</summary>
    public static byte[] Bytes(string hex)
    {
        List<int> vals = new List<int>();
        int cur = -1;
        for (int i = 0; i < hex.Length; i++)
        {
            int d = Digit(hex[i]);
            if (d < 0)
            {
                continue;
            }
            if (cur < 0)
            {
                cur = d;
            }
            else
            {
                vals.Add(cur * 16 + d);
                cur = -1;
            }
        }
        byte[] r = new byte[vals.Count];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = (byte)vals[i];
        }
        return r;
    }

    private static int Digit(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }
        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }
        return -1;
    }

    public static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    public static byte[] FromList(List<byte> list)
    {
        byte[] r = new byte[list.Count];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = list[i];
        }
        return r;
    }

    public static string Text(List<byte> list)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            sb.Append((char)list[i]);
        }
        return sb.ToString();
    }
}
