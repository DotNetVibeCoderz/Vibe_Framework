using Rustzor.Browser.Audio;
using Rustzor.Browser.Canvas;

namespace Rustzor.Game;

/// <summary>A 2D camera: follows a target, zooms, shakes, and maps screen to world.</summary>
public sealed class Camera2D
{
    private double _shake;
    private readonly Rng _rng = new(7);

    public Vec2 Position { get; set; }
    public double Zoom { get; set; } = 1;
    public double Rotation { get; set; }
    /// <summary>How quickly Follow catches up; higher is snappier.</summary>
    public double FollowSpeed { get; set; } = 6;
    /// <summary>World area the camera may show; null for unbounded.</summary>
    public Rect2? Bounds { get; set; }
    public Vec2 Viewport { get; set; } = new(800, 600);

    public void Follow(Vec2 target, double dt)
    {
        Position = new Vec2(MathX.Damp(Position.X, target.X, FollowSpeed, dt), MathX.Damp(Position.Y, target.Y, FollowSpeed, dt));
        if (Bounds is { } b)
        {
            var hw = Viewport.X / 2 / Zoom;
            var hh = Viewport.Y / 2 / Zoom;
            Position = new Vec2(MathX.Clamp(Position.X, b.Left + hw, Math.Max(b.Left + hw, b.Right - hw)), MathX.Clamp(Position.Y, b.Top + hh, Math.Max(b.Top + hh, b.Bottom - hh)));
        }
    }

    /// <summary>Adds screen shake that fades out; intensity in pixels.</summary>
    public void Shake(double intensity) => _shake = Math.Max(_shake, intensity);

    public void Update(double dt) => _shake = Math.Max(0, _shake - dt * 30);

    /// <summary>Applies the camera to a canvas; draw the world, then call End.</summary>
    public void Begin(Canvas2D g)
    {
        Viewport = new Vec2(g.Width, g.Height);
        var shake = _shake > 0 ? _rng.InCircle(_shake) : Vec2.Zero;
        g.Save().Translate(g.Width / 2 + shake.X, g.Height / 2 + shake.Y).Rotate(-Rotation).Scale(Zoom, Zoom).Translate(-Position.X, -Position.Y);
    }

    public void End(Canvas2D g) => g.Restore();

    public Vec2 ScreenToWorld(Vec2 screen) => ((screen - Viewport / 2) / Zoom).Rotated(Rotation) + Position;
    public Vec2 WorldToScreen(Vec2 world) => (world - Position).Rotated(-Rotation) * Zoom + Viewport / 2;

    /// <summary>The world rectangle on screen, for culling.</summary>
    public Rect2 VisibleWorld => Rect2.FromCenter(Position, Viewport.X / Zoom, Viewport.Y / Zoom);
}

/// <summary>A pooled particle system for sparks, smoke, explosions and trails.</summary>
public sealed class Particles
{
    private struct P
    {
        public Vec2 Pos, Vel;
        public double Life, MaxLife, Size;
        public string Color;
    }

    private readonly P[] _items;
    private int _count;
    private readonly Rng _rng = new(42);

    public Particles(int capacity = 2000) => _items = new P[capacity];

    public Vec2 Gravity { get; set; } = new(0, 200);
    public double Drag { get; set; } = 1.5;
    public int Count => _count;

    /// <summary>Emits a burst in all directions.</summary>
    public void Burst(Vec2 at, int count, string color, double speed = 200, double life = 0.8, double size = 3)
    {
        for (var i = 0; i < count; i++) Emit(at, Vec2.FromAngle(_rng.Range(0, Math.PI * 2), _rng.Range(speed * 0.3, speed)), color, life * _rng.Range(0.6, 1.2), size);
    }

    public void Emit(Vec2 at, Vec2 velocity, string color, double life = 1, double size = 3)
    {
        if (_count == _items.Length) return;
        _items[_count++] = new P { Pos = at, Vel = velocity, Life = life, MaxLife = life, Size = size, Color = color };
    }

    public void Update(double dt)
    {
        var damp = Math.Max(0, 1 - Drag * dt);
        for (var i = _count - 1; i >= 0; i--)
        {
            ref var p = ref _items[i];
            p.Life -= dt;
            if (p.Life <= 0)
            {
                _items[i] = _items[--_count];
                continue;
            }
            p.Vel = (p.Vel + Gravity * dt) * damp;
            p.Pos += p.Vel * dt;
        }
    }

    public void Draw(Canvas2D g)
    {
        for (var i = 0; i < _count; i++)
        {
            var p = _items[i];
            var t = p.Life / p.MaxLife;
            g.Alpha(t).Fill(p.Color).FillCircle(p.Pos.X, p.Pos.Y, p.Size * (0.5 + t * 0.5));
        }
        g.Alpha(1);
    }
}

/// <summary>Synthesised sound effects that need no audio files.</summary>
public static class Sfx
{
    public static bool Muted { get; set; }

    public static void Jump() => Play(() => AudioEngine.Tone(320, 0.15, "square", 0.15, slideTo: 640));
    public static void Coin() => Play(() => AudioEngine.Notes(new[] { 83, 88 }, 0.07, 0.01, "square", 0.15));
    public static void Hit() => Play(() => AudioEngine.Noise(0.12, 0.25, 1200));
    public static void Explosion() => Play(() => AudioEngine.Noise(0.5, 0.35, 600));
    public static void Laser() => Play(() => AudioEngine.Tone(900, 0.12, "sawtooth", 0.12, slideTo: 200));
    public static void PowerUp() => Play(() => AudioEngine.Notes(new[] { 60, 64, 67, 72 }, 0.08, 0.01, "triangle", 0.2));
    public static void GameOver() => Play(() => AudioEngine.Notes(new[] { 67, 63, 60, 55 }, 0.2, 0.05, "triangle", 0.2));
    public static void Click() => Play(() => AudioEngine.Tone(1200, 0.03, "square", 0.08));

    private static void Play(Action play)
    {
        if (!Muted) play();
    }
}
