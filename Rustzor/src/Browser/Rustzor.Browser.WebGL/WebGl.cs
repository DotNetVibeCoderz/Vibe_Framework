using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.WebGL;

/// <summary>
/// A WebGL 2 context. Resources (programs, buffers, textures) are created
/// immediately and named by number; per-frame state and draw calls are
/// batched like <c>Canvas2D</c> and sent by <see cref="Flush"/>.
/// </summary>
public sealed class GlContext
{
    private JsonWriter _cmds = new();
    private int _count;

    public const int Points = 0, Lines = 1, LineStrip = 3, Triangles = 4, TriangleStrip = 5, TriangleFan = 6;

    private GlContext(int handle)
    {
        Handle = handle;
        Begin();
    }

    public int Handle { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public static GlContext FromElement(ElementReference canvas, bool antialias = true, bool xrCompatible = false)
    {
        var r = Js.Call("gl.ctx", Js.Args("el", canvas.Id, "aa", antialias, "xr", xrCompatible));
        return new GlContext(r["h"].AsInt()) { Width = r["w"].AsInt(), Height = r["hh"].AsInt() };
    }

    /// <summary>Compiles and links a program; throws with the GLSL log on error.</summary>
    public int CreateProgram(string vertexGlsl, string fragmentGlsl) =>
        Js.Call("gl.program", Js.Args("h", Handle, "vs", vertexGlsl, "fs", fragmentGlsl)).AsInt();

    /// <summary>A vertex buffer of floats.</summary>
    public int CreateBuffer(float[] data, bool dynamic = false) =>
        Js.Call("gl.buffer", Js.Args("h", Handle, "data", data, "dynamic", dynamic)).AsInt();

    public void UpdateBuffer(int buffer, float[] data) { Cmd(1).Number(buffer).Raw(FloatArray(data)).EndArray(); }

    /// <summary>An index buffer of 16-bit indices.</summary>
    public int CreateIndexBuffer(int[] indices) => Js.Call("gl.indices", Js.Args("h", Handle, "data", indices)).AsInt();

    /// <summary>A texture from an image URL; resolves when uploaded.</summary>
    public async Task<int> LoadTextureAsync(string url) => (await Js.CallAsync("gl.texture", Js.Args("h", Handle, "url", url))).AsInt();

    private void Begin()
    {
        _cmds = new JsonWriter();
        _cmds.BeginArray();
        _count = 0;
    }

    private JsonWriter Cmd(int code)
    {
        _cmds.BeginArray().Number(code);
        _count++;
        return _cmds;
    }

    private static string FloatArray(float[] data)
    {
        var w = new JsonWriter();
        w.BeginArray();
        foreach (var f in data) w.Number((double)f);
        w.EndArray();
        return w.ToString();
    }

    public GlContext Viewport(int x, int y, int w, int h) { Cmd(2).Number(x).Number(y).Number(w).Number(h).EndArray(); return this; }
    public GlContext ClearColor(double r, double g, double b, double a = 1) { Cmd(3).Number(r).Number(g).Number(b).Number(a).EndArray(); return this; }
    /// <summary>Clears colour and depth.</summary>
    public GlContext Clear() { Cmd(4).EndArray(); return this; }
    public GlContext UseProgram(int program) { Cmd(5).Number(program).EndArray(); return this; }
    /// <summary>Binds a float buffer to a named attribute with <paramref name="size"/> components.</summary>
    public GlContext Attribute(int program, string name, int buffer, int size, int stride = 0, int offset = 0) { Cmd(6).Number(program).String(name).Number(buffer).Number(size).Number(stride).Number(offset).EndArray(); return this; }
    public GlContext Uniform(int program, string name, double x) { Cmd(7).Number(program).String(name).BeginArray().Number(x).EndArray().EndArray(); return this; }
    public GlContext Uniform(int program, string name, double x, double y) { Cmd(7).Number(program).String(name).BeginArray().Number(x).Number(y).EndArray().EndArray(); return this; }
    public GlContext Uniform(int program, string name, double x, double y, double z) { Cmd(7).Number(program).String(name).BeginArray().Number(x).Number(y).Number(z).EndArray().EndArray(); return this; }
    public GlContext Uniform(int program, string name, double x, double y, double z, double w) { Cmd(7).Number(program).String(name).BeginArray().Number(x).Number(y).Number(z).Number(w).EndArray().EndArray(); return this; }
    /// <summary>A 4x4 matrix uniform, column-major.</summary>
    public GlContext UniformMatrix(int program, string name, float[] matrix4x4) { Cmd(8).Number(program).String(name).Raw(FloatArray(matrix4x4)).EndArray(); return this; }
    public GlContext BindTexture(int program, string sampler, int texture, int unit = 0) { Cmd(9).Number(program).String(sampler).Number(texture).Number(unit).EndArray(); return this; }
    public GlContext DrawArrays(int mode, int first, int count) { Cmd(10).Number(mode).Number(first).Number(count).EndArray(); return this; }
    public GlContext DrawElements(int mode, int indexBuffer, int count) { Cmd(11).Number(mode).Number(indexBuffer).Number(count).EndArray(); return this; }
    public GlContext DepthTest(bool on) { Cmd(12).Bool(on).EndArray(); return this; }
    public GlContext Blend(bool on) { Cmd(13).Bool(on).EndArray(); return this; }

    public void Flush()
    {
        if (_count == 0) return;
        _cmds.EndArray();
        var json = _cmds.ToString();
        Begin();
        Js.InvokeVoid("gl.batch", "{\"h\":" + Handle + ",\"c\":" + json + "}");
    }

    /// <summary>Matches the drawing buffer to the element size; returns whether it changed.</summary>
    public bool Fit()
    {
        var r = Js.Call("gl.fit", Js.Args("h", Handle));
        Width = r["w"].AsInt();
        Height = r["hh"].AsInt();
        return r["changed"].AsBool();
    }
}

/// <summary>Column-major 4x4 matrices for 3D views, as WebGL expects them.</summary>
public static class Mat4
{
    public static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    public static float[] Perspective(double fovYRadians, double aspect, double near, double far)
    {
        var f = (float)(1.0 / Math.Tan(fovYRadians / 2));
        var nf = (float)(1 / (near - far));
        return new[] { f / (float)aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (float)(far + near) * nf, -1, 0, 0, (float)(2 * far * near) * nf, 0 };
    }

    public static float[] Multiply(float[] a, float[] b)
    {
        var r = new float[16];
        for (var col = 0; col < 4; col++)
            for (var row = 0; row < 4; row++)
            {
                float s = 0;
                for (var k = 0; k < 4; k++) s += a[k * 4 + row] * b[col * 4 + k];
                r[col * 4 + row] = s;
            }
        return r;
    }

    public static float[] Translation(double x, double y, double z) => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, (float)x, (float)y, (float)z, 1 };

    public static float[] RotationY(double radians)
    {
        float c = (float)Math.Cos(radians), s = (float)Math.Sin(radians);
        return new[] { c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1f };
    }

    public static float[] RotationX(double radians)
    {
        float c = (float)Math.Cos(radians), s = (float)Math.Sin(radians);
        return new[] { 1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1f };
    }

    public static float[] Scale(double x, double y, double z) => new float[] { (float)x, 0, 0, 0, 0, (float)y, 0, 0, 0, 0, (float)z, 0, 0, 0, 0, 1 };
}
