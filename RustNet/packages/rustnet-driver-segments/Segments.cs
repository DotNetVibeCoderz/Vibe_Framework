namespace RustNet.Community.Segments;

/// <summary>Shared table decoding (no array initializers in the RustNet dialect).</summary>
internal static class SegmentTables
{
    public static int[] Decode(string hex, int digitsPerEntry)
    {
        int n = hex.Length / digitsPerEntry;
        int[] r = new int[n];
        for (int i = 0; i < n; i++)
        {
            int v = 0;
            for (int d = 0; d < digitsPerEntry; d++)
            {
                char c = hex[i * digitsPerEntry + d];
                int x = c <= '9' ? c - '0' : c - 'A' + 10;
                v = (v << 4) | x;
            }
            r[i] = v;
        }
        return r;
    }
}

/// <summary>
/// 7-segment encoder. Bit 0 = segment a (top), then b (top right), c
/// (bottom right), d (bottom), e (bottom left), f (top left), g (middle);
/// bit 7 = decimal point (or the colon on clock modules). This is the bit
/// order TM1637/TM1638 and HT16K33 7-segment backpacks use. The ASCII table
/// follows the widely used "SevenSegmentASCII" glyph set; letters a
/// 7-segment display cannot show are approximated.
/// </summary>
public static class SevenSegment
{
    public const int A = 0x01;
    public const int B = 0x02;
    public const int C = 0x04;
    public const int D = 0x08;
    public const int E = 0x10;
    public const int F = 0x20;
    public const int G = 0x40;
    public const int Dp = 0x80;

    // 0x20..0x7F, one byte each.
    private const string Table =
        "0086227E6DD24620290B2170104080523F065B4F666D7D077F6F090D614843D3"
        + "5F777C395E79713D76301E753815373F736B336D783E3E2A766E5B39640F2308"
        + "025F7C585E7B716F74100C753014545C7367506D781C1C14766E5B4630700100";

    // A flag rather than a null check: on the interpreter an unassigned
    // reference field reads as integer 0 and comparing it to null throws.
    private static bool _loaded;
    private static int[] _map = new int[0];

    /// <summary>Segments for an ASCII character; characters outside 0x20..0x7F are blank.</summary>
    public static int Encode(char c)
    {
        int code = c;
        if (code < 0x20 || code > 0x7F)
        {
            return 0;
        }
        if (!_loaded)
        {
            _map = SegmentTables.Decode(Table, 2);
            _loaded = true;
        }
        return _map[code - 0x20];
    }

    /// <summary>Segments for a hexadecimal digit 0..15 (shown as 0-9 A b C d E F).</summary>
    public static int EncodeHex(int digit)
    {
        digit = digit & 0x0F;
        return digit < 10 ? Encode((char)('0' + digit)) : Encode((char)('A' + digit - 10));
    }

    /// <summary>
    /// Encode text for a display with <paramref name="digits"/> positions. A
    /// '.' after a character lights that character's decimal point instead of
    /// taking a position. Shorter text is padded with blanks on the right.
    /// </summary>
    public static byte[] EncodeText(string text, int digits)
    {
        byte[] r = new byte[digits];
        int pos = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '.' && pos >= 0 && pos < digits && (r[pos] & Dp) == 0)
            {
                r[pos] = (byte)(r[pos] | Dp);
                continue;
            }
            pos++;
            if (pos >= digits)
            {
                break;
            }
            r[pos] = (byte)Encode(c);
        }
        return r;
    }

    /// <summary>A decimal number right-aligned in <paramref name="digits"/> positions ('-' in front of negatives).</summary>
    public static byte[] EncodeNumber(int value, int digits, bool leadingZeros)
    {
        byte[] r = new byte[digits];
        bool negative = value < 0;
        long v = value;
        if (negative)
        {
            v = -v;
        }
        int pos = digits - 1;
        while (pos >= 0)
        {
            int d = (int)(v % 10);
            r[pos] = (byte)Encode((char)('0' + d));
            v = v / 10;
            pos--;
            if (v == 0)
            {
                break;
            }
        }
        int firstDigit = pos + 1;
        while (pos >= 0)
        {
            r[pos] = (byte)(leadingZeros ? Encode('0') : 0);
            pos--;
        }
        if (negative)
        {
            int at = leadingZeros ? 0 : firstDigit - 1;
            if (at < 0)
            {
                at = 0;
            }
            r[at] = (byte)G;
        }
        return r;
    }
}

/// <summary>
/// 14-segment ("alphanumeric", starburst) encoder in the Adafruit LED
/// Backpack (HT16K33) bit layout: bit 0 A (top), 1 B (top right), 2 C (bottom
/// right), 3 D (bottom), 4 E (bottom left), 5 F (top left), 6 G1 (middle
/// left), 7 G2 (middle right), 8 H (upper-left diagonal), 9 J (upper
/// vertical), 10 K (upper-right diagonal), 11 L (lower-left diagonal), 12 M
/// (lower vertical), 13 N (lower-right diagonal), 14 DP. Glyphs follow
/// Adafruit's <c>alphafonttable</c>.
/// </summary>
public static class FourteenSegment
{
    public const int A = 1 << 0;
    public const int B = 1 << 1;
    public const int C = 1 << 2;
    public const int D = 1 << 3;
    public const int E = 1 << 4;
    public const int F = 1 << 5;
    public const int G1 = 1 << 6;
    public const int G2 = 1 << 7;
    public const int H = 1 << 8;
    public const int J = 1 << 9;
    public const int K = 1 << 10;
    public const int L = 1 << 11;
    public const int M = 1 << 12;
    public const int N = 1 << 13;
    public const int Dp = 1 << 14;

    // 0x20..0x7F, four hex digits each.
    private const string Table =
        "00000006022012CE12ED0C24235D0400240009003FC012C0080000C040000C00"
        + "0C3F000600DB008F00E6206900FD000700FF00EF12000A00240000C809001083"
        + "02BB00F7128F0039120F00F9007100BD00F61209001E2470003805362136003F"
        + "00F3203F20F300ED1201003E0C3028362D0015000C0900392100000F0C030008"
        + "01001058207800D8088E08580071048E10701000000E3600003010D4105000DC"
        + "01700486005020880078001C2004281428C0200C084809491200248905203FFF";

    private static bool _loaded;
    private static int[] _map = new int[0];

    public static int Encode(char c)
    {
        int code = c;
        if (code < 0x20 || code > 0x7F)
        {
            return 0;
        }
        if (!_loaded)
        {
            _map = SegmentTables.Decode(Table, 4);
            _loaded = true;
        }
        return _map[code - 0x20];
    }

    public static int EncodeHex(int digit)
    {
        digit = digit & 0x0F;
        return digit < 10 ? Encode((char)('0' + digit)) : Encode((char)('A' + digit - 10));
    }
}

/// <summary>
/// 16-segment encoder: the 14-segment glyphs with the top and bottom bars
/// split in two halves. Bit 0 A1 (top, left half), 1 A2 (top, right half), 2
/// B, 3 C, 4 D1 (bottom, right half), 5 D2 (bottom, left half), 6 E, 7 F, 8
/// G1, 9 G2, 10 H, 11 J, 12 K, 13 L, 14 M, 15 N, 16 DP — H..N being the same
/// diagonals and verticals as in <see cref="FourteenSegment"/>. Both halves
/// of a bar are always lit together.
/// </summary>
public static class SixteenSegment
{
    public const int A1 = 1 << 0;
    public const int A2 = 1 << 1;
    public const int B = 1 << 2;
    public const int C = 1 << 3;
    public const int D1 = 1 << 4;
    public const int D2 = 1 << 5;
    public const int E = 1 << 6;
    public const int F = 1 << 7;
    public const int G1 = 1 << 8;
    public const int G2 = 1 << 9;
    public const int H = 1 << 10;
    public const int J = 1 << 11;
    public const int K = 1 << 12;
    public const int L = 1 << 13;
    public const int M = 1 << 14;
    public const int N = 1 << 15;
    public const int Dp = 1 << 16;

    /// <summary>Convert a 14-segment mask: A becomes A1+A2, D becomes D1+D2, the rest move up.</summary>
    public static int FromFourteen(int s)
    {
        int r = 0;
        if ((s & FourteenSegment.A) != 0)
        {
            r |= A1 | A2;
        }
        if ((s & FourteenSegment.D) != 0)
        {
            r |= D1 | D2;
        }
        r |= ((s >> 1) & 0x3) << 2;   // B, C
        r |= ((s >> 4) & 0x3FF) << 6; // E, F, G1, G2, H, J, K, L, M, N
        if ((s & FourteenSegment.Dp) != 0)
        {
            r |= Dp;
        }
        return r;
    }

    public static int Encode(char c) => FromFourteen(FourteenSegment.Encode(c));

    public static int EncodeHex(int digit) => FromFourteen(FourteenSegment.EncodeHex(digit));
}
