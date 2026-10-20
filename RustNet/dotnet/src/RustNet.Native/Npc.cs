using RustNet.Core;

namespace RustNet.Native;

/// <summary>
/// Native Procedure Caller — RustNet's analogue of TinyCLR's RLP. Loads a
/// native module that was signed with the device's key and pushed with
/// <c>rustnet native push</c>, then calls its procedures with byte arrays.
///
/// Every procedure has one C ABI:
/// <c>extern "C" fn(args: *const u8, args_len: u32, out: *mut u8, out_cap: u32) -> i32</c>,
/// returning the bytes written (or a negative error, which surfaces here as an
/// exception). Wider values travel little-endian, exactly as on every other
/// host call.
///
/// Chip-gated: the virtual device loads modules as dynamic libraries; a chip
/// that cannot execute loaded code answers <see cref="IsSupported"/> = false
/// and <see cref="Load"/> throws. Native code runs unsandboxed — signing
/// controls who may load it, not what it does.
/// </summary>
public static class Npc
{
    /// <summary>True when this device can execute loaded native code.</summary>
    [InternalCall]
    public static bool IsSupported() => throw new RuntimeOnlyException();

    /// <summary>
    /// Verify and load <paramref name="module"/> (pushed as
    /// <c>/data/native/&lt;module&gt;.rnsb</c>) and resolve the exported
    /// <paramref name="procedure"/>. Returns a handle for <see cref="Invoke"/>.
    /// Loading the same module twice reuses it.
    /// </summary>
    [InternalCall]
    public static int Load(string module, string procedure) => throw new RuntimeOnlyException();

    /// <summary>Call a loaded procedure; the result holds at most <paramref name="maxOutput"/> bytes.</summary>
    [InternalCall]
    public static byte[] Invoke(int handle, byte[] args, int maxOutput) => throw new RuntimeOnlyException();
}

/// <summary>A resolved native procedure, with helpers for the common argument shapes.</summary>
public class NativeProcedure
{
    private readonly int _handle;

    /// <summary>Default result capacity: 4 KB.</summary>
    public int MaxOutput = 4096;

    public NativeProcedure(string module, string procedure)
    {
        _handle = Npc.Load(module, procedure);
    }

    public byte[] Invoke(byte[] args) => Npc.Invoke(_handle, args, MaxOutput);

    /// <summary>Pass 32-bit integers (little-endian) and read one back.</summary>
    public int InvokeInt32(int[] args)
    {
        byte[] a = new byte[args.Length * 4];
        for (int i = 0; i < args.Length; i++)
        {
            a[i * 4] = (byte)args[i];
            a[i * 4 + 1] = (byte)(args[i] >> 8);
            a[i * 4 + 2] = (byte)(args[i] >> 16);
            a[i * 4 + 3] = (byte)(args[i] >> 24);
        }
        byte[] r = Npc.Invoke(_handle, a, 4);
        if (r.Length < 4)
        {
            throw new InvalidOperationException("native procedure returned fewer than 4 bytes");
        }
        return r[0] | (r[1] << 8) | (r[2] << 16) | (r[3] << 24);
    }
}
