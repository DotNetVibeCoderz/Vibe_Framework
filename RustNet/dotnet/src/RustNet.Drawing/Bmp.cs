namespace RustNet.Drawing;

/// <summary>
/// Windows BMP decoder — uncompressed 24-bit and 32-bit (BITMAPINFOHEADER),
/// bottom-up or top-down. The common export format for embedded assets.
/// </summary>
public static class Bmp
{
    public static Bitmap Decode(byte[] d)
    {
        if (d.Length < 54 || d[0] != 'B' || d[1] != 'M')
        {
            throw new System.Exception("not a BMP");
        }
        int dataOffset = ReadI32(d, 10);
        int headerSize = ReadI32(d, 14);
        int width = ReadI32(d, 18);
        int rawHeight = ReadI32(d, 22);
        int bpp = ReadU16(d, 28);
        int compression = ReadI32(d, 30);

        if (compression != 0)
        {
            throw new System.Exception("compressed BMP not supported");
        }
        if (bpp != 24 && bpp != 32)
        {
            throw new System.Exception("BMP must be 24- or 32-bit");
        }

        bool topDown = rawHeight < 0;
        int height = topDown ? -rawHeight : rawHeight;
        var bmp = new Bitmap(width, height);

        int bytesPerPixel = bpp / 8;
        // Rows are padded to a 4-byte boundary.
        int rowSize = ((width * bytesPerPixel + 3) / 4) * 4;

        for (int row = 0; row < height; row++)
        {
            int srcRow = topDown ? row : (height - 1 - row);
            int rowStart = dataOffset + srcRow * rowSize;
            for (int x = 0; x < width; x++)
            {
                int p = rowStart + x * bytesPerPixel;
                if (p + 2 >= d.Length)
                {
                    continue;
                }
                int b = d[p];
                int g = d[p + 1];
                int r = d[p + 2];
                bmp.SetPixel(x, row, Bitmap.Rgb565(r, g, b));
            }
        }
        _ = headerSize;
        return bmp;
    }

    private static int ReadI32(byte[] d, int o)
        => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);

    private static int ReadU16(byte[] d, int o) => d[o] | (d[o + 1] << 8);
}
