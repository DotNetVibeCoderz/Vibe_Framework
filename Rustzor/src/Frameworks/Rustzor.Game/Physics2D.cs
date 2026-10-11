namespace Rustzor.Game;

public enum ShapeKind { Circle, Box }

/// <summary>A rigid body. Mass 0 makes it static (walls, floors).</summary>
public sealed class Body
{
    public Vec2 Position;
    public Vec2 Velocity;
    public Vec2 Force;
    public double Mass = 1;
    /// <summary>Bounciness, 0 (none) to 1 (perfect).</summary>
    public double Restitution = 0.2;
    public double Friction = 0.2;
    /// <summary>Velocity lost per second to air.</summary>
    public double LinearDamping = 0.01;
    public ShapeKind Shape = ShapeKind.Circle;
    public double Radius = 10;
    public double Width = 20, Height = 20;
    /// <summary>Overlaps are reported but not resolved.</summary>
    public bool IsTrigger;
    /// <summary>Bodies collide when (a.Layer &amp; b.Mask) != 0 and the other way round.</summary>
    public int Layer = 1, Mask = -1;
    public double GravityScale = 1;
    /// <summary>Whatever the game attaches: an entity, a name.</summary>
    public object? Tag;
    public bool OnGround { get; internal set; }

    public bool IsStatic => Mass <= 0;
    public double InverseMass => Mass <= 0 ? 0 : 1 / Mass;

    public Rect2 Bounds => Shape == ShapeKind.Circle
        ? new Rect2(Position.X - Radius, Position.Y - Radius, Radius * 2, Radius * 2)
        : new Rect2(Position.X - Width / 2, Position.Y - Height / 2, Width, Height);

    public void ApplyImpulse(Vec2 impulse) => Velocity += impulse * InverseMass;

    public static Body Circle(Vec2 at, double radius, double mass = 1) => new() { Position = at, Radius = radius, Mass = mass, Shape = ShapeKind.Circle };
    public static Body Box(Vec2 center, double width, double height, double mass = 1) => new() { Position = center, Width = width, Height = height, Mass = mass, Shape = ShapeKind.Box };
}

public readonly record struct Contact(Body A, Body B, Vec2 Normal, double Depth);

/// <summary>
/// Impulse-based 2D physics: gravity, circles and boxes, restitution and
/// friction, a spatial hash broad phase, and collision events.
/// </summary>
public sealed class PhysicsWorld
{
    private readonly List<Body> _bodies = new();
    private readonly Dictionary<long, List<Body>> _grid = new();
    private readonly Dictionary<Body, int> _index = new();
    private readonly List<(Body A, Body B)> _pairs = new();
    private readonly HashSet<long> _seen = new();

    public Vec2 Gravity { get; set; } = new(0, 900);
    /// <summary>Broad-phase cell size; about twice the typical body size.</summary>
    public double CellSize { get; set; } = 64;
    /// <summary>Position correction iterations per step.</summary>
    public int Iterations { get; set; } = 3;

    public IReadOnlyList<Body> Bodies => _bodies;
    public event Action<Contact>? OnCollision;
    public event Action<Contact>? OnTrigger;

    public Body Add(Body body)
    {
        _bodies.Add(body);
        return body;
    }

    public void Remove(Body body) => _bodies.Remove(body);
    public void Clear() => _bodies.Clear();

    public void Step(double dt)
    {
        foreach (var b in _bodies)
        {
            b.OnGround = false;
            if (b.IsStatic) continue;
            var accel = Gravity * b.GravityScale + b.Force * b.InverseMass;
            b.Velocity += accel * dt;
            b.Velocity *= Math.Max(0, 1 - b.LinearDamping * dt);
            b.Position += b.Velocity * dt;
            b.Force = Vec2.Zero;
        }

        // Broad phase once per step: candidate pairs from the spatial hash.
        BuildGrid();
        _pairs.Clear();
        _seen.Clear();
        foreach (var cell in _grid.Values)
        {
            for (var i = 0; i < cell.Count; i++)
            for (var j = i + 1; j < cell.Count; j++)
            {
                var a = cell[i];
                var b = cell[j];
                if (a.IsStatic && b.IsStatic) continue;
                if ((a.Layer & b.Mask) == 0 || (b.Layer & a.Mask) == 0) continue;
                var ia = _index[a];
                var ib = _index[b];
                var key = ia < ib ? (long)ia << 32 | (uint)ib : (long)ib << 32 | (uint)ia;
                if (_seen.Add(key)) _pairs.Add((a, b));
            }
        }

        // Narrow phase and resolution, iterated so stacks settle.
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            foreach (var (a, b) in _pairs)
            {
                if (!Collide(a, b, out var contact)) continue;
                if (a.IsTrigger || b.IsTrigger)
                {
                    if (iteration == 0) OnTrigger?.Invoke(contact);
                    continue;
                }
                Resolve(contact, iteration == 0);
                if (iteration == 0) OnCollision?.Invoke(contact);
            }
        }
    }

    /// <summary>Bodies whose shape contains the point.</summary>
    public List<Body> QueryPoint(Vec2 p)
    {
        var hits = new List<Body>();
        foreach (var b in _bodies)
        {
            var inside = b.Shape == ShapeKind.Circle ? (p - b.Position).LengthSquared <= b.Radius * b.Radius : b.Bounds.Contains(p);
            if (inside) hits.Add(b);
        }
        return hits;
    }

    /// <summary>The nearest body a ray hits within a distance, or null.</summary>
    public Body? Raycast(Vec2 origin, Vec2 direction, double maxDistance, out Vec2 point)
    {
        var dir = direction.Normalized();
        Body? best = null;
        var bestT = maxDistance;
        foreach (var b in _bodies)
        {
            double t;
            if (b.Shape == ShapeKind.Circle)
            {
                var oc = origin - b.Position;
                var bb = Vec2.Dot(oc, dir);
                var c = oc.LengthSquared - b.Radius * b.Radius;
                var disc = bb * bb - c;
                if (disc < 0) continue;
                t = -bb - Math.Sqrt(disc);
            }
            else
            {
                var r = b.Bounds;
                var tx1 = (r.Left - origin.X) / dir.X;
                var tx2 = (r.Right - origin.X) / dir.X;
                var ty1 = (r.Top - origin.Y) / dir.Y;
                var ty2 = (r.Bottom - origin.Y) / dir.Y;
                var tmin = Math.Max(Math.Min(tx1, tx2), Math.Min(ty1, ty2));
                var tmax = Math.Min(Math.Max(tx1, tx2), Math.Max(ty1, ty2));
                if (tmax < 0 || tmin > tmax) continue;
                t = tmin;
            }
            if (t >= 0 && t < bestT)
            {
                bestT = t;
                best = b;
            }
        }
        point = origin + dir * bestT;
        return best;
    }

    private void BuildGrid()
    {
        foreach (var list in _grid.Values) list.Clear();
        _index.Clear();
        foreach (var b in _bodies)
        {
            _index[b] = _index.Count;
            var r = b.Bounds;
            var x0 = (int)Math.Floor(r.Left / CellSize);
            var x1 = (int)Math.Floor(r.Right / CellSize);
            var y0 = (int)Math.Floor(r.Top / CellSize);
            var y1 = (int)Math.Floor(r.Bottom / CellSize);
            // A huge static body (the floor) would fill thousands of cells; cap it.
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096)
            {
                x1 = x0 + 63;
                y1 = y0 + 63;
            }
            for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
            {
                var key = (long)x << 32 | (uint)y;
                if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<Body>();
                list.Add(b);
            }
        }
    }

    public static bool Collide(Body a, Body b, out Contact contact)
    {
        contact = default;
        if (a.Shape == ShapeKind.Circle && b.Shape == ShapeKind.Circle)
        {
            var d = b.Position - a.Position;
            var r = a.Radius + b.Radius;
            var dist2 = d.LengthSquared;
            if (dist2 >= r * r) return false;
            var dist = Math.Sqrt(dist2);
            contact = new Contact(a, b, dist > 1e-9 ? d / dist : new Vec2(0, 1), r - dist);
            return true;
        }
        if (a.Shape == ShapeKind.Box && b.Shape == ShapeKind.Box)
        {
            var ra = a.Bounds;
            var rb = b.Bounds;
            var ox = Math.Min(ra.Right, rb.Right) - Math.Max(ra.Left, rb.Left);
            var oy = Math.Min(ra.Bottom, rb.Bottom) - Math.Max(ra.Top, rb.Top);
            if (ox <= 0 || oy <= 0) return false;
            var d = b.Position - a.Position;
            contact = ox < oy
                ? new Contact(a, b, new Vec2(d.X < 0 ? -1 : 1, 0), ox)
                : new Contact(a, b, new Vec2(0, d.Y < 0 ? -1 : 1), oy);
            return true;
        }
        // Circle against box: the closest point of the box to the circle's centre.
        var (circle, box, flip) = a.Shape == ShapeKind.Circle ? (a, b, false) : (b, a, true);
        var bounds = box.Bounds;
        var closest = new Vec2(MathX.Clamp(circle.Position.X, bounds.Left, bounds.Right), MathX.Clamp(circle.Position.Y, bounds.Top, bounds.Bottom));
        var delta = circle.Position - closest;
        var len2 = delta.LengthSquared;
        if (len2 >= circle.Radius * circle.Radius) return false;
        Vec2 normal;
        double depth;
        if (len2 > 1e-12)
        {
            var len = Math.Sqrt(len2);
            normal = -(delta / len); // from circle toward box
            depth = circle.Radius - len;
        }
        else
        {
            // The centre is inside the box: push out along the shallowest axis.
            var dl = circle.Position.X - bounds.Left;
            var dr = bounds.Right - circle.Position.X;
            var dt = circle.Position.Y - bounds.Top;
            var db = bounds.Bottom - circle.Position.Y;
            var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            normal = min == dl ? Vec2.Right : min == dr ? Vec2.Left : min == dt ? Vec2.Down : Vec2.Up;
            depth = circle.Radius + min;
        }
        contact = flip ? new Contact(a, b, -normal, depth) : new Contact(a, b, normal, depth);
        return true;
    }

    private static void Resolve(Contact c, bool applyImpulse)
    {
        var (a, b, n) = (c.A, c.B, c.Normal);
        var invSum = a.InverseMass + b.InverseMass;
        if (invSum <= 0) return;

        // Positional correction, split by inverse mass.
        var correction = n * (Math.Max(c.Depth - 0.01, 0) / invSum * 0.8);
        a.Position -= correction * a.InverseMass;
        b.Position += correction * b.InverseMass;

        // The normal points from a to b, and y grows downward: b below a means
        // a is standing on b.
        if (n.Y > 0.5) a.OnGround = true;
        if (n.Y < -0.5) b.OnGround = true;

        if (!applyImpulse) return;
        var rv = b.Velocity - a.Velocity;
        var along = Vec2.Dot(rv, n);
        if (along > 0) return;
        var e = Math.Min(a.Restitution, b.Restitution);
        var j = -(1 + e) * along / invSum;
        var impulse = n * j;
        a.Velocity -= impulse * a.InverseMass;
        b.Velocity += impulse * b.InverseMass;

        // Coulomb friction along the tangent.
        rv = b.Velocity - a.Velocity;
        var tangent = (rv - n * Vec2.Dot(rv, n)).Normalized();
        var jt = -Vec2.Dot(rv, tangent) / invSum;
        var mu = Math.Sqrt(a.Friction * b.Friction);
        var friction = Math.Abs(jt) < j * mu ? tangent * jt : tangent * (-j * mu);
        a.Velocity -= friction * a.InverseMass;
        b.Velocity += friction * b.InverseMass;
    }
}
