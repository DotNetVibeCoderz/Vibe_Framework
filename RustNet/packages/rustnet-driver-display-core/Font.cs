namespace RustNet.Community.DisplayCore;

/// <summary>
/// Decodes lookup tables kept as hex strings. The RustNet dialect has no
/// array initializers, so init sequences and fonts live in <c>const string</c>s
/// and are turned into <c>byte[]</c> on first use. Whitespace is ignored.
/// </summary>
public static class HexTable
{
    public static byte[] Decode(string hex)
    {
        int digits = 0;
        for (int i = 0; i < hex.Length; i++)
        {
            if (Nibble(hex[i]) >= 0)
            {
                digits++;
            }
        }
        byte[] r = new byte[digits / 2];
        int n = 0;
        int hi = -1;
        for (int i = 0; i < hex.Length; i++)
        {
            int v = Nibble(hex[i]);
            if (v < 0)
            {
                continue;
            }
            if (hi < 0)
            {
                hi = v;
            }
            else
            {
                r[n] = (byte)((hi << 4) | v);
                n++;
                hi = -1;
            }
        }
        return r;
    }

    private static int Nibble(char c)
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
}

/// <summary>
/// The classic 5×7 ASCII font (0x20..0x7F), five column bytes per glyph,
/// bit 0 = top row — the layout used by the Adafruit GFX "glcdfont" and most
/// character LCD/OLED libraries. Glyphs occupy a 6×8 cell when drawn.
/// </summary>
public static class Font5x7
{
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 7;
    public const int CellWidth = 6;
    public const int CellHeight = 8;
    public const int First = 0x20;
    public const int Last = 0x7F;

    private const string Table =
        ""
        + "000000000000005F00000007000700147F147F14242A7F2A12231308646236495522500005030000"
        + "001C2241000041221C0014083E081408083E08080050300000080808080800606000002010080402"
        + "3E5149453E00427F400042615149462141454B311814127F1027454545393C4A4949300171090503"
        + "3649494936064949291E003636000000563600000814224100141414141400412214080201510906"
        + "324979413E7E1111117E7F494949363E414141227F4141221C7F494949417F090909013E4149497A"
        + "7F0808087F00417F41002040413F017F081422417F404040407F020C027F7F0408107F3E4141413E"
        + "7F090909063E4151215E7F09192946464949493101017F01013F4040403F1F2040201F3F4038403F"
        + "631408146307087008076151494543007F41410002040810200041417F0004020102044040404040"
        + "000102040020545454787F484444383844444420384444487F3854545418087E0901020C5252523E"
        + "7F0804047800447D40002040443D007F1028440000417F40007C041804787C080404783844444438"
        + "7C14141408081414187C7C080404084854545420043F4440203C4040207C1C2040201C3C4030403C"
        + "44281028440C5050503C4464544C44000836410000007F0000004136080010080810087846414678"
        ;

    // A bool flag, not a null check: on the interpreter an unassigned
    // reference field reads as integer 0, and comparing that to null throws.
    private static bool _loaded;
    private static byte[] _glyphs = new byte[0];

    /// <summary>All glyphs, decoded on first use (96 × 5 bytes).</summary>
    public static byte[] Glyphs()
    {
        if (!_loaded)
        {
            _glyphs = HexTable.Decode(Table);
            _loaded = true;
        }
        return _glyphs;
    }

    /// <summary>Column <paramref name="column"/> (0..4) of a character; unknown characters render as '?'.</summary>
    public static int Column(char c, int column)
    {
        int code = c;
        if (code < First || code > Last)
        {
            code = '?';
        }
        return Glyphs()[(code - First) * 5 + column];
    }
}
