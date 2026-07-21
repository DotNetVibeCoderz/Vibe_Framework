using RustNet.Core;

namespace RustNet.Buses;

/// <summary>One received CAN frame.</summary>
public class CanFrame
{
    public int Id;
    public bool Extended;
    public bool Rtr;
    public byte[] Data = new byte[0];
}

/// <summary>
/// Classic CAN 2.0 bus master. Frames are queued in the controller FIFO;
/// <see cref="Read"/> returns null when nothing is pending.
/// </summary>
public static class Can
{
    [InternalCall]
    public static void Init(int bus, int bitrate, bool loopback) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Write(int bus, int id, byte[] data) => throw new RuntimeOnlyException();

    [InternalCall]
    public static int Available(int bus) => throw new RuntimeOnlyException();

    /// <summary>Raw packed frame: id u32 LE | flags | len | data. Prefer <see cref="Read"/>.</summary>
    [InternalCall]
    public static byte[] ReadRaw(int bus) => throw new RuntimeOnlyException();

    /// <summary>Hardware acceptance filter: accept when (frameId &amp; mask) == (id &amp; mask).</summary>
    [InternalCall]
    public static void SetFilter(int bus, int id, int mask) => throw new RuntimeOnlyException();

    /// <summary>Pop the next received frame, or null when the FIFO is empty.</summary>
    public static CanFrame Read(int bus)
    {
        byte[] raw = ReadRaw(bus);
        if (raw == null)
        {
            return null;
        }
        CanFrame f = new CanFrame();
        f.Id = raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24);
        f.Extended = (raw[4] & 1) != 0;
        f.Rtr = (raw[4] & 2) != 0;
        int len = raw[5];
        byte[] data = new byte[len];
        for (int i = 0; i < len; i++)
        {
            data[i] = raw[6 + i];
        }
        f.Data = data;
        return f;
    }
}
