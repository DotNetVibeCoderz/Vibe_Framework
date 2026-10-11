using System.Globalization;
using Rustzor.Browser.WebGL;

namespace Rustzor.Graphics;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 One = new(1, 1, 1);
    public static readonly Vec3 UnitY = new(0, 1, 0);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Vec3 Normalized() { var l = Length; return l < 1e-12 ? Zero : this / l; }
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static Vec3 Min(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vec3 Max(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);
    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}

/// <summary>Column-major 4x4 matrix helpers beyond <see cref="Mat4"/>.</summary>
public static class Matrix
{
    public static float[] LookAt(Vec3 eye, Vec3 target, Vec3 up)
    {
        var z = (eye - target).Normalized();
        var x = Vec3.Cross(up, z).Normalized();
        var y = Vec3.Cross(z, x);
        return new float[]
        {
            (float)x.X, (float)y.X, (float)z.X, 0,
            (float)x.Y, (float)y.Y, (float)z.Y, 0,
            (float)x.Z, (float)y.Z, (float)z.Z, 0,
            (float)-Vec3.Dot(x, eye), (float)-Vec3.Dot(y, eye), (float)-Vec3.Dot(z, eye), 1,
        };
    }

    /// <summary>Translation, then rotation (Y, then X, then Z, radians), then scale.</summary>
    public static float[] Compose(Vec3 position, Vec3 rotation, Vec3 scale)
    {
        var m = Mat4.Translation(position.X, position.Y, position.Z);
        m = Mat4.Multiply(m, Mat4.RotationY(rotation.Y));
        m = Mat4.Multiply(m, Mat4.RotationX(rotation.X));
        m = Mat4.Multiply(m, RotationZ(rotation.Z));
        return Mat4.Multiply(m, Mat4.Scale(scale.X, scale.Y, scale.Z));
    }

    public static float[] RotationZ(double r)
    {
        float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
        return new float[] { c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    }

    public static Vec3 Transform(float[] m, Vec3 p, double w = 1) => new(
        m[0] * p.X + m[4] * p.Y + m[8] * p.Z + m[12] * w,
        m[1] * p.X + m[5] * p.Y + m[9] * p.Z + m[13] * w,
        m[2] * p.X + m[6] * p.Y + m[10] * p.Z + m[14] * w);

    public static float[]? Invert(float[] m)
    {
        var inv = new double[16];
        inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
        inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
        inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
        inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
        inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
        inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
        inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
        inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
        inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
        inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
        inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
        inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
        inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
        inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
        inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
        inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
        var det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
        if (Math.Abs(det) < 1e-12) return null;
        var outM = new float[16];
        for (var i = 0; i < 16; i++) outM[i] = (float)(inv[i] / det);
        return outM;
    }
}

/// <summary>Triangle geometry: positions, per-vertex normals and indices.</summary>
public sealed class Mesh
{
    public float[] Positions { get; init; } = Array.Empty<float>();
    public float[] Normals { get; init; } = Array.Empty<float>();
    public int[] Indices { get; init; } = Array.Empty<int>();
    public int TriangleCount => Indices.Length / 3;

    public (Vec3 Min, Vec3 Max) Bounds()
    {
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        for (var i = 0; i < Positions.Length; i += 3)
        {
            var p = new Vec3(Positions[i], Positions[i + 1], Positions[i + 2]);
            min = Vec3.Min(min, p);
            max = Vec3.Max(max, p);
        }
        return (min, max);
    }

    public static Mesh Box(double w, double h, double d)
    {
        double x = w / 2, y = h / 2, z = d / 2;
        var faces = new (Vec3 N, Vec3[] C)[]
        {
            (new(0, 0, 1), new Vec3[] { new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z) }),
            (new(0, 0, -1), new Vec3[] { new(x, -y, -z), new(-x, -y, -z), new(-x, y, -z), new(x, y, -z) }),
            (new(1, 0, 0), new Vec3[] { new(x, -y, z), new(x, -y, -z), new(x, y, -z), new(x, y, z) }),
            (new(-1, 0, 0), new Vec3[] { new(-x, -y, -z), new(-x, -y, z), new(-x, y, z), new(-x, y, -z) }),
            (new(0, 1, 0), new Vec3[] { new(-x, y, z), new(x, y, z), new(x, y, -z), new(-x, y, -z) }),
            (new(0, -1, 0), new Vec3[] { new(-x, -y, -z), new(x, -y, -z), new(x, -y, z), new(-x, -y, z) }),
        };
        var pos = new List<float>();
        var nor = new List<float>();
        var idx = new List<int>();
        foreach (var (n, c) in faces)
        {
            var b = pos.Count / 3;
            foreach (var p in c)
            {
                pos.Add((float)p.X); pos.Add((float)p.Y); pos.Add((float)p.Z);
                nor.Add((float)n.X); nor.Add((float)n.Y); nor.Add((float)n.Z);
            }
            idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        return new Mesh { Positions = pos.ToArray(), Normals = nor.ToArray(), Indices = idx.ToArray() };
    }

    public static Mesh Cube(double size = 1) => Box(size, size, size);

    public static Mesh Sphere(double radius = 0.5, int segments = 24, int rings = 16)
    {
        var pos = new List<float>();
        var nor = new List<float>();
        var idx = new List<int>();
        for (var r = 0; r <= rings; r++)
        {
            var phi = Math.PI * r / rings;
            for (var s = 0; s <= segments; s++)
            {
                var theta = 2 * Math.PI * s / segments;
                var n = new Vec3(Math.Sin(phi) * Math.Cos(theta), Math.Cos(phi), Math.Sin(phi) * Math.Sin(theta));
                pos.Add((float)(n.X * radius)); pos.Add((float)(n.Y * radius)); pos.Add((float)(n.Z * radius));
                nor.Add((float)n.X); nor.Add((float)n.Y); nor.Add((float)n.Z);
            }
        }
        for (var r = 0; r < rings; r++)
        for (var s = 0; s < segments; s++)
        {
            var a = r * (segments + 1) + s;
            var b = a + segments + 1;
            idx.AddRange(new[] { a, a + 1, b, b, a + 1, b + 1 });
        }
        return new Mesh { Positions = pos.ToArray(), Normals = nor.ToArray(), Indices = idx.ToArray() };
    }

    public static Mesh Cylinder(double radius = 0.5, double height = 1, int segments = 24)
    {
        var pos = new List<float>();
        var nor = new List<float>();
        var idx = new List<int>();
        var hy = height / 2;
        for (var s = 0; s <= segments; s++)
        {
            var t = 2 * Math.PI * s / segments;
            double cx = Math.Cos(t), cz = Math.Sin(t);
            foreach (var y in new[] { -hy, hy })
            {
                pos.Add((float)(cx * radius)); pos.Add((float)y); pos.Add((float)(cz * radius));
                nor.Add((float)cx); nor.Add(0); nor.Add((float)cz);
            }
        }
        for (var s = 0; s < segments; s++)
        {
            var a = s * 2;
            idx.AddRange(new[] { a, a + 1, a + 2, a + 2, a + 1, a + 3 });
        }
        foreach (var (y, ny) in new[] { (hy, 1f), (-hy, -1f) })
        {
            var centre = pos.Count / 3;
            pos.Add(0); pos.Add((float)y); pos.Add(0);
            nor.Add(0); nor.Add(ny); nor.Add(0);
            for (var s = 0; s <= segments; s++)
            {
                var t = 2 * Math.PI * s / segments;
                pos.Add((float)(Math.Cos(t) * radius)); pos.Add((float)y); pos.Add((float)(Math.Sin(t) * radius));
                nor.Add(0); nor.Add(ny); nor.Add(0);
            }
            for (var s = 0; s < segments; s++)
                if (ny > 0) idx.AddRange(new[] { centre, centre + s + 2, centre + s + 1 });
                else idx.AddRange(new[] { centre, centre + s + 1, centre + s + 2 });
        }
        return new Mesh { Positions = pos.ToArray(), Normals = nor.ToArray(), Indices = idx.ToArray() };
    }

    public static Mesh Plane(double width = 10, double depth = 10) => new()
    {
        Positions = new[] { (float)(-width / 2), 0, (float)(-depth / 2), (float)(width / 2), 0, (float)(-depth / 2), (float)(width / 2), 0, (float)(depth / 2), (float)(-width / 2), 0, (float)(depth / 2) },
        Normals = new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0 },
        Indices = new[] { 0, 2, 1, 0, 3, 2 },
    };

    /// <summary>Parses Wavefront OBJ (v, vn, f; polygons are fanned into triangles). Missing normals are computed.</summary>
    public static Mesh ParseObj(string text)
    {
        var v = new List<Vec3>();
        var vn = new List<Vec3>();
        var pos = new List<float>();
        var nor = new List<float>();
        var idx = new List<int>();
        var cache = new Dictionary<string, int>();
        var needNormals = false;
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "v": v.Add(new Vec3(D(parts[1]), D(parts[2]), D(parts[3]))); break;
                case "vn": vn.Add(new Vec3(D(parts[1]), D(parts[2]), D(parts[3]))); break;
                case "f":
                {
                    var face = new List<int>();
                    for (var i = 1; i < parts.Length; i++)
                    {
                        if (!cache.TryGetValue(parts[i], out var index))
                        {
                            var refs = parts[i].Split('/');
                            var pi = int.Parse(refs[0]);
                            var p = v[pi < 0 ? v.Count + pi : pi - 1];
                            index = pos.Count / 3;
                            pos.Add((float)p.X); pos.Add((float)p.Y); pos.Add((float)p.Z);
                            if (refs.Length > 2 && refs[2].Length > 0)
                            {
                                var ni = int.Parse(refs[2]);
                                var n = vn[ni < 0 ? vn.Count + ni : ni - 1];
                                nor.Add((float)n.X); nor.Add((float)n.Y); nor.Add((float)n.Z);
                            }
                            else
                            {
                                needNormals = true;
                                nor.Add(0); nor.Add(0); nor.Add(0);
                            }
                            cache[parts[i]] = index;
                        }
                        face.Add(index);
                    }
                    for (var i = 1; i + 1 < face.Count; i++) idx.AddRange(new[] { face[0], face[i], face[i + 1] });
                    break;
                }
            }
        }
        var normals = nor.ToArray();
        var positions = pos.ToArray();
        var indices = idx.ToArray();
        if (needNormals) ComputeNormals(positions, indices, normals);
        return new Mesh { Positions = positions, Normals = normals, Indices = indices };
    }

    /// <summary>Area-weighted smooth normals.</summary>
    public static void ComputeNormals(float[] positions, int[] indices, float[] normals)
    {
        Array.Clear(normals);
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = indices[t] * 3, b = indices[t + 1] * 3, c = indices[t + 2] * 3;
            var pa = new Vec3(positions[a], positions[a + 1], positions[a + 2]);
            var n = Vec3.Cross(new Vec3(positions[b], positions[b + 1], positions[b + 2]) - pa, new Vec3(positions[c], positions[c + 1], positions[c + 2]) - pa);
            foreach (var k in new[] { a, b, c })
            {
                normals[k] += (float)n.X;
                normals[k + 1] += (float)n.Y;
                normals[k + 2] += (float)n.Z;
            }
        }
        for (var i = 0; i < normals.Length; i += 3)
        {
            var n = new Vec3(normals[i], normals[i + 1], normals[i + 2]).Normalized();
            normals[i] = (float)n.X;
            normals[i + 1] = (float)n.Y;
            normals[i + 2] = (float)n.Z;
        }
    }
}

/// <summary>A mesh placed in the scene.</summary>
public sealed class Model
{
    private static int _next;

    public Model(Mesh mesh, string color = "#a1a1aa")
    {
        Mesh = mesh;
        Color = color;
    }

    public string Id { get; set; } = "m" + Interlocked.Increment(ref _next);
    public string Name { get; set; } = "";
    public Mesh Mesh { get; set; }
    public Vec3 Position { get; set; }
    /// <summary>Euler angles in radians.</summary>
    public Vec3 Rotation { get; set; }
    public Vec3 Scale { get; set; } = Vec3.One;
    public string Color { get; set; }
    public double Opacity { get; set; } = 1;
    public bool Visible { get; set; } = true;
    public bool Selected { get; set; }
    public Dictionary<string, string> Data { get; } = new();

    public float[] Matrix => Rustzor.Graphics.Matrix.Compose(Position, Rotation, Scale);
}

/// <summary>A camera that orbits a target: drag to orbit, wheel to zoom.</summary>
public sealed class OrbitCamera
{
    public Vec3 Target { get; set; }
    public double Distance { get; set; } = 8;
    /// <summary>Radians around the vertical axis.</summary>
    public double Yaw { get; set; } = 0.8;
    /// <summary>Radians above the horizon.</summary>
    public double Pitch { get; set; } = 0.5;
    public double FieldOfView { get; set; } = Math.PI / 4;
    public double Near { get; set; } = 0.05;
    public double Far { get; set; } = 2000;

    public Vec3 Eye => Target + new Vec3(Math.Cos(Pitch) * Math.Sin(Yaw), Math.Sin(Pitch), Math.Cos(Pitch) * Math.Cos(Yaw)) * Distance;

    public void Orbit(double dxPixels, double dyPixels)
    {
        Yaw -= dxPixels * 0.008;
        Pitch = Math.Clamp(Pitch + dyPixels * 0.008, -1.5, 1.5);
    }

    public void Zoom(double factor) => Distance = Math.Clamp(Distance * factor, 0.1, Far / 2);

    public void Pan(double dxPixels, double dyPixels)
    {
        var forward = (Target - Eye).Normalized();
        var right = Vec3.Cross(forward, Vec3.UnitY).Normalized();
        var up = Vec3.Cross(right, forward);
        var scale = Distance * 0.0015;
        Target = Target - right * (dxPixels * scale) + up * (dyPixels * scale);
    }

    /// <summary>Frames a bounding box.</summary>
    public void Frame(Vec3 min, Vec3 max)
    {
        Target = (min + max) / 2;
        var radius = (max - min).Length / 2;
        Distance = Math.Max(radius / Math.Sin(FieldOfView / 2) * 1.1, 0.5);
    }

    public float[] View => Matrix.LookAt(Eye, Target, Vec3.UnitY);
    public float[] Projection(double aspect) => Mat4.Perspective(FieldOfView, aspect, Near, Far);

    /// <summary>The world-space ray through a screen pixel.</summary>
    public (Vec3 Origin, Vec3 Direction) Ray(double sx, double sy, double width, double height)
    {
        var inv = Matrix.Invert(Mat4.Multiply(Projection(width / Math.Max(height, 1)), View));
        if (inv is null) return (Eye, (Target - Eye).Normalized());
        var nx = sx / width * 2 - 1;
        var ny = 1 - sy / height * 2;
        var near = Unproject(inv, nx, ny, -1);
        var far = Unproject(inv, nx, ny, 1);
        return (near, (far - near).Normalized());
    }

    private static Vec3 Unproject(float[] inv, double x, double y, double z)
    {
        var p = Matrix.Transform(inv, new Vec3(x, y, z));
        var w = inv[3] * x + inv[7] * y + inv[11] * z + inv[15];
        return p / w;
    }
}

/// <summary>Models, a camera and lighting.</summary>
public sealed class Scene3D
{
    public List<Model> Models { get; } = new();
    public OrbitCamera Camera { get; } = new();
    public string Background { get; set; } = "#0f1115";
    /// <summary>Direction toward the light.</summary>
    public Vec3 Light { get; set; } = new(0.5, 1, 0.3);
    public bool ShowGrid { get; set; } = true;
    public double GridSize { get; set; } = 20;

    public Model Add(Model m)
    {
        Models.Add(m);
        return m;
    }

    /// <summary>The nearest model under a screen pixel, by ray-triangle intersection.</summary>
    public Model? Pick(double sx, double sy, double width, double height)
    {
        var (origin, dir) = Camera.Ray(sx, sy, width, height);
        Model? best = null;
        var bestT = double.MaxValue;
        foreach (var m in Models)
        {
            if (!m.Visible) continue;
            var inv = Matrix.Invert(m.Matrix);
            if (inv is null) continue;
            var o = Matrix.Transform(inv, origin);
            var d = Matrix.Transform(inv, dir, 0);
            var p = m.Mesh.Positions;
            var ix = m.Mesh.Indices;
            for (var t = 0; t + 2 < ix.Length; t += 3)
            {
                int a = ix[t] * 3, b = ix[t + 1] * 3, c = ix[t + 2] * 3;
                var hit = Intersect(o, d, new Vec3(p[a], p[a + 1], p[a + 2]), new Vec3(p[b], p[b + 1], p[b + 2]), new Vec3(p[c], p[c + 1], p[c + 2]));
                if (hit > 0)
                {
                    // Compare distances in world space; the model's scale changes them.
                    var world = (Matrix.Transform(m.Matrix, o + d * hit) - origin).Length;
                    if (world < bestT)
                    {
                        bestT = world;
                        best = m;
                    }
                }
            }
        }
        return best;
    }

    /// <summary>Möller–Trumbore; the ray parameter, or -1.</summary>
    private static double Intersect(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var h = Vec3.Cross(d, e2);
        var det = Vec3.Dot(e1, h);
        if (Math.Abs(det) < 1e-12) return -1;
        var f = 1 / det;
        var s = o - a;
        var u = f * Vec3.Dot(s, h);
        if (u < 0 || u > 1) return -1;
        var q = Vec3.Cross(s, e1);
        var v = f * Vec3.Dot(d, q);
        if (v < 0 || u + v > 1) return -1;
        var t = f * Vec3.Dot(e2, q);
        return t > 1e-9 ? t : -1;
    }

    /// <summary>Bounds of every visible model, for framing.</summary>
    public (Vec3 Min, Vec3 Max) Extent()
    {
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (var m in Models.Where(m => m.Visible))
        {
            var (bmin, bmax) = m.Mesh.Bounds();
            var mat = m.Matrix;
            foreach (var corner in new[] { bmin, bmax, new Vec3(bmin.X, bmax.Y, bmin.Z), new Vec3(bmax.X, bmin.Y, bmax.Z) })
            {
                var w = Matrix.Transform(mat, corner);
                min = Vec3.Min(min, w);
                max = Vec3.Max(max, w);
            }
        }
        return Models.Count == 0 ? (Vec3.Zero, Vec3.One) : (min, max);
    }
}

/// <summary>Draws a <see cref="Scene3D"/> with WebGL 2: Lambert plus hemisphere lighting, a rim highlight for selection, and a ground grid.</summary>
public sealed class Renderer3D
{
    private const string Vertex = """
        #version 300 es
        in vec3 aPos; in vec3 aNormal;
        uniform mat4 uModel, uView, uProj;
        out vec3 vN; out vec3 vW;
        void main() { vec4 w = uModel * vec4(aPos, 1.0); vW = w.xyz; vN = mat3(uModel) * aNormal; gl_Position = uProj * uView * w; }
        """;

    private const string Fragment = """
        #version 300 es
        precision highp float;
        in vec3 vN; in vec3 vW;
        uniform vec4 uColor; uniform vec3 uLight; uniform vec3 uEye; uniform float uSelected;
        out vec4 outColor;
        void main() {
          vec3 n = normalize(vN);
          float diffuse = max(dot(n, normalize(uLight)), 0.0);
          float hemi = 0.5 + 0.5 * n.y;
          vec3 v = normalize(uEye - vW);
          float rim = pow(1.0 - max(dot(n, v), 0.0), 3.0);
          vec3 c = uColor.rgb * (0.32 * hemi + 0.78 * diffuse) + rim * 0.12 + uSelected * vec3(0.98, 0.45, 0.09) * (0.25 + rim * 1.6);
          outColor = vec4(c, uColor.a);
        }
        """;

    private const string LineVertex = """
        #version 300 es
        in vec3 aPos; uniform mat4 uView, uProj;
        void main() { gl_Position = uProj * uView * vec4(aPos, 1.0); }
        """;

    private const string LineFragment = """
        #version 300 es
        precision mediump float; uniform vec4 uColor; out vec4 outColor;
        void main() { outColor = uColor; }
        """;

    private readonly GlContext _gl;
    private readonly int _program;
    private readonly int _lines;
    private readonly Dictionary<Mesh, (int Pos, int Nor, int Idx)> _buffers = new();
    private int _grid = -1;
    private int _gridCount;
    private double _gridBuiltFor = -1;

    public Renderer3D(GlContext gl)
    {
        _gl = gl;
        _program = gl.CreateProgram(Vertex, Fragment);
        _lines = gl.CreateProgram(LineVertex, LineFragment);
    }

    public GlContext Gl => _gl;

    /// <summary>Forgets a mesh's GPU buffers (after editing its arrays).</summary>
    public void Invalidate(Mesh mesh) => _buffers.Remove(mesh);

    public void Render(Scene3D scene)
    {
        _gl.Fit();
        var bg = Color.Parse(scene.Background).ToUnit();
        var aspect = _gl.Width / Math.Max(_gl.Height, 1.0);
        var view = scene.Camera.View;
        var proj = scene.Camera.Projection(aspect);
        _gl.Viewport(0, 0, _gl.Width, _gl.Height).ClearColor(bg.R, bg.G, bg.B).Clear().DepthTest(true);

        if (scene.ShowGrid) DrawGrid(scene, view, proj);

        var eye = scene.Camera.Eye;
        var light = scene.Light.Normalized();
        _gl.UseProgram(_program)
            .UniformMatrix(_program, "uView", view)
            .UniformMatrix(_program, "uProj", proj)
            .Uniform(_program, "uLight", light.X, light.Y, light.Z)
            .Uniform(_program, "uEye", eye.X, eye.Y, eye.Z);
        var transparent = false;
        foreach (var m in scene.Models)
        {
            if (!m.Visible) continue;
            if (m.Opacity < 1)
            {
                transparent = true;
                continue;
            }
            DrawModel(m);
        }
        if (transparent)
        {
            _gl.Blend(true);
            foreach (var m in scene.Models.Where(m => m.Visible && m.Opacity < 1)) DrawModel(m);
            _gl.Blend(false);
        }
        _gl.Flush();
    }

    private void DrawModel(Model m)
    {
        var (pos, nor, idx) = Buffers(m.Mesh);
        var c = Color.Parse(m.Color).ToUnit();
        _gl.UseProgram(_program)
            .Attribute(_program, "aPos", pos, 3)
            .Attribute(_program, "aNormal", nor, 3)
            .UniformMatrix(_program, "uModel", m.Matrix)
            .Uniform(_program, "uColor", c.R, c.G, c.B, m.Opacity)
            .Uniform(_program, "uSelected", m.Selected ? 1 : 0)
            .DrawElements(GlContext.Triangles, idx, m.Mesh.Indices.Length);
    }

    private (int, int, int) Buffers(Mesh mesh)
    {
        if (_buffers.TryGetValue(mesh, out var b)) return b;
        b = (_gl.CreateBuffer(mesh.Positions), _gl.CreateBuffer(mesh.Normals), _gl.CreateIndexBuffer(mesh.Indices));
        _buffers[mesh] = b;
        return b;
    }

    private void DrawGrid(Scene3D scene, float[] view, float[] proj)
    {
        if (_gridBuiltFor != scene.GridSize)
        {
            var half = scene.GridSize / 2;
            var lines = new List<float>();
            for (var i = -half; i <= half + 1e-9; i += 1)
            {
                lines.AddRange(new[] { (float)i, 0f, (float)-half, (float)i, 0f, (float)half });
                lines.AddRange(new[] { (float)-half, 0f, (float)i, (float)half, 0f, (float)i });
            }
            _grid = _gl.CreateBuffer(lines.ToArray());
            _gridCount = lines.Count / 3;
            _gridBuiltFor = scene.GridSize;
        }
        _gl.UseProgram(_lines)
            .Attribute(_lines, "aPos", _grid, 3)
            .UniformMatrix(_lines, "uView", view)
            .UniformMatrix(_lines, "uProj", proj)
            .Uniform(_lines, "uColor", 0.5, 0.5, 0.55, 0.35)
            .Blend(true)
            .DrawArrays(GlContext.Lines, 0, _gridCount)
            .Blend(false);
    }
}
