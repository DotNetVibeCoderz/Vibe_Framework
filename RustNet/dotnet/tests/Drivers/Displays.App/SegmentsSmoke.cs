using RustNet.Community.Segments;

namespace DriversApp;

public static class SegmentsSmoke
{
    /// <summary>Mask from segment names: 7-seg letters a..g, '.' = dp.</summary>
    private static int Seven(string segs)
    {
        int m = 0;
        for (int i = 0; i < segs.Length; i++)
        {
            char c = segs[i];
            m |= c == '.' ? 0x80 : 1 << (c - 'a');
        }
        return m;
    }

    /// <summary>
    /// 14-segment mask from names separated by spaces: A B C D E F G1 G2 H J K L M N DP
    /// (H/K upper diagonals left/right, J upper vertical, L/N lower diagonals left/right, M lower vertical).
    /// </summary>
    private static int Fourteen(string segs)
    {
        int m = 0;
        int i = 0;
        while (i < segs.Length)
        {
            char c = segs[i];
            if (c == ' ')
            {
                i++;
                continue;
            }
            int bit;
            if (c == 'G')
            {
                bit = segs[i + 1] == '1' ? 6 : 7;
                i++;
            }
            else if (c == 'H')
            {
                bit = 8;
            }
            else if (c == 'J')
            {
                bit = 9;
            }
            else if (c == 'K')
            {
                bit = 10;
            }
            else if (c == 'L')
            {
                bit = 11;
            }
            else if (c == 'M')
            {
                bit = 12;
            }
            else if (c == 'N')
            {
                bit = 13;
            }
            else
            {
                bit = c - 'A';
            }
            m |= 1 << bit;
            i++;
        }
        return m;
    }

    public static void Run()
    {
        // Digits as the segments a lit 7-segment display shows them.
        string[] digits = new string[16];
        digits[0] = "abcdef";
        digits[1] = "bc";
        digits[2] = "abdeg";
        digits[3] = "abcdg";
        digits[4] = "bcfg";
        digits[5] = "acdfg";
        digits[6] = "acdefg";
        digits[7] = "abc";
        digits[8] = "abcdefg";
        digits[9] = "abcdfg";
        digits[10] = "abcefg";  // A
        digits[11] = "cdefg";   // b
        digits[12] = "adef";    // C
        digits[13] = "bcdeg";   // d
        digits[14] = "adefg";   // E
        digits[15] = "aefg";    // F
        for (int d = 0; d < 16; d++)
        {
            Check.Equal($"7seg hex {d}", Seven(digits[d]), SevenSegment.EncodeHex(d));
        }
        Check.Equal("7seg 0x3F", 0x3F, SevenSegment.Encode('0'));
        Check.Equal("7seg minus", Seven("g"), SevenSegment.Encode('-'));
        Check.Equal("7seg H", Seven("bcefg"), SevenSegment.Encode('H'));
        Check.Equal("7seg blank", 0, SevenSegment.Encode(' '));
        byte[] t = SevenSegment.EncodeText("1.2E", 4);
        Check.Equal("7seg text dp", Seven("bc."), t[0]);
        Check.Equal("7seg text 2", Seven("abdeg"), t[1]);
        Check.Equal("7seg text E", Seven("adefg"), t[2]);
        Check.Equal("7seg text pad", 0, t[3]);
        byte[] n = SevenSegment.EncodeNumber(-42, 4, false);
        Check.True("7seg -42", n[0] == 0 && n[1] == Seven("g") && n[2] == Seven("bcfg") && n[3] == Seven("abdeg"));
        byte[] z = SevenSegment.EncodeNumber(7, 3, true);
        Check.True("7seg 007", z[0] == 0x3F && z[1] == 0x3F && z[2] == 0x07);

        // 14-segment (Adafruit alphafonttable glyph shapes).
        Check.Equal("14seg 0", Fourteen("A B C D E F K L"), FourteenSegment.Encode('0'));
        Check.Equal("14seg 1", Fourteen("B C"), FourteenSegment.Encode('1'));
        Check.Equal("14seg 2", Fourteen("A B D E G1 G2"), FourteenSegment.Encode('2'));
        Check.Equal("14seg 3", Fourteen("A B C D G2"), FourteenSegment.Encode('3'));
        Check.Equal("14seg 4", Fourteen("B C F G1 G2"), FourteenSegment.Encode('4'));
        Check.Equal("14seg 5", Fourteen("A D F G1 N"), FourteenSegment.Encode('5'));
        Check.Equal("14seg 6", Fourteen("A C D E F G1 G2"), FourteenSegment.Encode('6'));
        Check.Equal("14seg 7", Fourteen("A B C"), FourteenSegment.Encode('7'));
        Check.Equal("14seg 8", Fourteen("A B C D E F G1 G2"), FourteenSegment.Encode('8'));
        Check.Equal("14seg 9", Fourteen("A B C D F G1 G2"), FourteenSegment.Encode('9'));
        Check.Equal("14seg A", Fourteen("A B C E F G1 G2"), FourteenSegment.EncodeHex(10));
        Check.Equal("14seg B", Fourteen("A B C D G2 J M"), FourteenSegment.EncodeHex(11));
        Check.Equal("14seg C", Fourteen("A D E F"), FourteenSegment.EncodeHex(12));
        Check.Equal("14seg D", Fourteen("A B C D J M"), FourteenSegment.EncodeHex(13));
        Check.Equal("14seg E", Fourteen("A D E F G1 G2"), FourteenSegment.EncodeHex(14));
        Check.Equal("14seg F", Fourteen("A E F G1"), FourteenSegment.EncodeHex(15));
        Check.Equal("14seg X", Fourteen("H K L N"), FourteenSegment.Encode('X'));
        Check.Equal("14seg T", Fourteen("A J M"), FourteenSegment.Encode('T'));
        Check.Equal("14seg dot", 1 << 14, FourteenSegment.Encode('.'));

        // 16-segment: bars split. '0' = A1 A2 B C D1 D2 E F + diagonals K, L.
        int s0 = SixteenSegment.A1 | SixteenSegment.A2 | SixteenSegment.B | SixteenSegment.C | SixteenSegment.D1
            | SixteenSegment.D2 | SixteenSegment.E | SixteenSegment.F | SixteenSegment.K | SixteenSegment.L;
        Check.Equal("16seg 0", 0x30FF, s0);
        Check.Equal("16seg 0 encode", s0, SixteenSegment.Encode('0'));
        Check.Equal("16seg 1", (1 << 2) | (1 << 3), SixteenSegment.EncodeHex(1));
        Check.Equal("16seg T", 0x4803, SixteenSegment.Encode('T'));
    }
}
