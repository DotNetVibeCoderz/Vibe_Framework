using RustNet.Core;

namespace RustNet.Drawing;

/// <summary>
/// Native image decoders provided by the runtime. Heavy formats (PNG, JPEG)
/// are decoded by the Rust firmware (via the <c>image</c> crate) rather than
/// in managed IL — the interpreter would be far slower and the codecs large.
/// The blob returned is <c>[width:u16 LE][height:u16 LE][rgb565 LE ...]</c>.
/// </summary>
internal static class Native
{
    [InternalCall]
    public static byte[] DecodeRgb565(byte[] data) => throw new RuntimeOnlyException();
}
