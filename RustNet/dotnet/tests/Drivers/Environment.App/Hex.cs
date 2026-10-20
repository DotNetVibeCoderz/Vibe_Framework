namespace DriversApp;

/// <summary>Builds byte arrays from hex text ("66 66 93"), since array initializers are not in the device dialect.</summary>
public static class Hex
{
    public static byte[] Bytes(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (Digit(text[i]) >= 0)
            {
                count++;
            }
        }
        byte[] r = new byte[count / 2];
        int n = 0;
        int acc = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int d = Digit(text[i]);
            if (d < 0)
            {
                continue;
            }
            acc = (acc << 4) | d;
            n++;
            if ((n & 1) == 0)
            {
                r[n / 2 - 1] = (byte)acc;
                acc = 0;
            }
        }
        return r;
    }

    private static int Digit(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }
        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }
        return -1;
    }

    /// <summary>True when a write equal to <paramref name="hex"/> was recorded.</summary>
    public static bool Wrote(RustNet.Community.Bus.SimI2cDevice sim, string hex)
    {
        byte[] want = Bytes(hex);
        for (int i = 0; i < sim.Writes.Count; i++)
        {
            if (Same(sim.Writes[i], want))
            {
                return true;
            }
        }
        return false;
    }

    public static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int j = 0; j < a.Length; j++)
        {
            if (a[j] != b[j])
            {
                return false;
            }
        }
        return true;
    }
}
