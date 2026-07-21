using RustNet.Core;

namespace RustNet.Buses;

/// <summary>
/// Dallas/Maxim 1-Wire bus master. Timing-critical bit banging happens in
/// the Rust HAL; managed code works at the byte/ROM level.
/// </summary>
public static class OneWire
{
    /// <summary>Bus reset; true when at least one slave answered presence.</summary>
    [InternalCall]
    public static bool Reset(int bus) => throw new RuntimeOnlyException();

    /// <summary>Raw ROM list, 8 bytes per device little-endian; prefer <see cref="Search"/>.</summary>
    [InternalCall]
    public static byte[] SearchRaw(int bus) => throw new RuntimeOnlyException();

    /// <summary>Address one slave (MATCH ROM).</summary>
    [InternalCall]
    public static void Select(int bus, long rom) => throw new RuntimeOnlyException();

    /// <summary>Address all slaves (SKIP ROM).</summary>
    [InternalCall]
    public static void Skip(int bus) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Write(int bus, byte[] data) => throw new RuntimeOnlyException();

    [InternalCall]
    public static byte[] Read(int bus, int count) => throw new RuntimeOnlyException();

    /// <summary>Enumerate slave ROM codes on the bus.</summary>
    public static long[] Search(int bus)
    {
        byte[] raw = SearchRaw(bus);
        long[] roms = new long[raw.Length / 8];
        for (int i = 0; i < roms.Length; i++)
        {
            long rom = 0;
            for (int b = 7; b >= 0; b--)
            {
                rom = (rom << 8) | raw[i * 8 + b];
            }
            roms[i] = rom;
        }
        return roms;
    }

    public static void WriteByte(int bus, int value)
    {
        byte[] one = new byte[1];
        one[0] = (byte)value;
        Write(bus, one);
    }
}
