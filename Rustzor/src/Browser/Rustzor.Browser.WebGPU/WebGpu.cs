using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.WebGPU;

/// <summary>The GPU adapter the browser exposes.</summary>
public sealed record GpuInfo(string Vendor, string Architecture, string Description, bool HasF16);

/// <summary>
/// WebGPU from C#: run WGSL compute shaders over float arrays, and draw
/// full-screen WGSL fragment shaders (visualisations, effects, shader art) on a
/// canvas.
/// </summary>
public static class Gpu
{
    public static bool IsSupported => Js.Call("gpu.supported").AsBool();

    /// <summary>Requests the adapter and device once; later calls reuse them.</summary>
    public static async Task<GpuInfo> InitializeAsync()
    {
        var v = await Js.CallAsync("gpu.init");
        return new GpuInfo(v["vendor"].AsString(), v["architecture"].AsString(), v["description"].AsString(), v["f16"].AsBool());
    }

    /// <summary>
    /// Runs a compute shader. Binding 0 is <paramref name="input"/> (read-only
    /// storage, <c>array&lt;f32&gt;</c>), binding 1 the output
    /// (<c>array&lt;f32&gt;</c> of <paramref name="outputLength"/>), binding 2 a
    /// uniform <c>array&lt;vec4&lt;f32&gt;, 1&gt;</c> of <paramref name="parameters"/>
    /// (up to four floats). Entry point <c>main</c>.
    /// </summary>
    public static async Task<float[]> ComputeAsync(string wgsl, float[] input, int outputLength, int workgroupsX, int workgroupsY = 1, params float[] parameters)
    {
        var v = await Js.CallAsync("gpu.compute", Js.Args(
            "code", wgsl,
            "input", Convert.ToBase64String(ToBytes(input)),
            "outLen", outputLength,
            "wx", workgroupsX,
            "wy", workgroupsY,
            "params", parameters));
        return FromBytes(Convert.FromBase64String(v.AsString()));
    }

    internal static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            var bits = BitConverter.SingleToInt32Bits(values[i]);
            bytes[i * 4] = (byte)bits;
            bytes[i * 4 + 1] = (byte)(bits >> 8);
            bytes[i * 4 + 2] = (byte)(bits >> 16);
            bytes[i * 4 + 3] = (byte)(bits >> 24);
        }
        return bytes;
    }

    internal static float[] FromBytes(byte[] bytes)
    {
        var values = new float[bytes.Length / 4];
        for (var i = 0; i < values.Length; i++)
            values[i] = BitConverter.Int32BitsToSingle(bytes[i * 4] | bytes[i * 4 + 1] << 8 | bytes[i * 4 + 2] << 16 | bytes[i * 4 + 3] << 24);
        return values;
    }
}

/// <summary>
/// A canvas drawn by a WGSL fragment shader over the whole surface. The shader
/// receives <c>u: Uniforms</c> at group 0 binding 0 with
/// <c>time: f32, width: f32, height: f32, mouseX: f32, mouseY: f32</c> and up
/// to eleven custom floats in <c>p: array&lt;vec4&lt;f32&gt;, 3&gt;</c>.
/// </summary>
public sealed class ShaderCanvas : IDisposable
{
    private readonly int _handle;

    private ShaderCanvas(int handle) => _handle = handle;

    /// <summary>Compiles <paramref name="fragmentWgsl"/>; it must define <c>fn fs(@builtin(position) pos: vec4f) -> @location(0) vec4f</c>.</summary>
    public static async Task<ShaderCanvas> CreateAsync(ElementReference canvas, string fragmentWgsl) =>
        new((await Js.CallAsync("gpu.shaderCanvas", Js.Args("el", canvas.Id, "code", fragmentWgsl))).AsInt());

    /// <summary>Draws one frame with the given time, pointer and custom values.</summary>
    public void Draw(double timeSeconds, double mouseX = 0, double mouseY = 0, params float[] custom) =>
        Js.Send("gpu.draw", Js.Args("h", _handle, "t", timeSeconds, "mx", mouseX, "my", mouseY, "p", custom));

    public void Dispose() => Js.Send("js.release", Js.Args("h", _handle));
}
