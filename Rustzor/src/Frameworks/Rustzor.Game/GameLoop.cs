using Rustzor.Interop;

namespace Rustzor.Game;

/// <summary>
/// A fixed-timestep game loop on requestAnimationFrame. Update runs at a
/// steady rate (60 Hz by default) however fast the display refreshes, so
/// physics behaves the same on every machine; Render runs once per frame with
/// the fraction of a step left over, for interpolation.
/// </summary>
public sealed class GameLoop
{
    private readonly Action<double> _update;
    private readonly Action<double> _render;
    private double _last = -1;
    private double _accumulator;
    private double _fpsTime;
    private int _fpsFrames;
    private bool _running;
    private int _generation;

    /// <param name="update">Called with the step length in seconds.</param>
    /// <param name="render">Called with the interpolation fraction in [0, 1).</param>
    public GameLoop(Action<double> update, Action<double> render)
    {
        _update = update;
        _render = render;
    }

    /// <summary>Input, refreshed before each frame's updates.</summary>
    public Input Input { get; } = new();

    public double StepSeconds { get; set; } = 1.0 / 60;
    /// <summary>Most steps run in one frame, so a stall cannot spiral.</summary>
    public int MaxStepsPerFrame { get; set; } = 5;
    /// <summary>Multiplies elapsed time: 0.5 is slow motion.</summary>
    public double TimeScale { get; set; } = 1;
    public bool Paused { get; set; }
    public bool Running => _running;
    public double Fps { get; private set; }
    /// <summary>Seconds of game time since Start.</summary>
    public double Time { get; private set; }
    public long Frame { get; private set; }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _last = -1;
        var generation = ++_generation;
        Scheduler.RequestAnimationFrame(t => Tick(t, generation));
    }

    public void Stop() => _running = false;

    private void Tick(double nowMs, int generation)
    {
        if (!_running || generation != _generation) return;
        if (_last < 0) _last = nowMs;
        var elapsed = Math.Min((nowMs - _last) / 1000.0, 0.25);
        _last = nowMs;

        _fpsFrames++;
        _fpsTime += elapsed;
        if (_fpsTime >= 0.5)
        {
            Fps = _fpsFrames / _fpsTime;
            _fpsFrames = 0;
            _fpsTime = 0;
        }

        Input.Poll();
        if (!Paused)
        {
            _accumulator += elapsed * TimeScale;
            var steps = 0;
            while (_accumulator >= StepSeconds && steps < MaxStepsPerFrame)
            {
                _update(StepSeconds);
                Time += StepSeconds;
                _accumulator -= StepSeconds;
                steps++;
            }
            if (steps == MaxStepsPerFrame) _accumulator = 0;
        }
        Frame++;
        _render(_accumulator / StepSeconds);
        Scheduler.RequestAnimationFrame(t => Tick(t, generation));
    }
}

/// <summary>Countdown and repeating timers driven by game time.</summary>
public sealed class Timers
{
    private readonly List<(double Due, double Interval, Action Action)> _items = new();
    private double _now;

    public void After(double seconds, Action action) => _items.Add((_now + seconds, 0, action));
    public void Every(double seconds, Action action) => _items.Add((_now + seconds, seconds, action));
    public void Clear() => _items.Clear();

    public void Update(double dt)
    {
        _now += dt;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var (due, interval, action) = _items[i];
            if (_now < due) continue;
            if (interval > 0) _items[i] = (due + interval, interval, action);
            else _items.RemoveAt(i);
            action();
        }
    }
}

public static class Ease
{
    public static double Linear(double t) => t;
    public static double InQuad(double t) => t * t;
    public static double OutQuad(double t) => t * (2 - t);
    public static double InOutQuad(double t) => t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double OutBack(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
    public static double OutElastic(double t) =>
        t is 0 or 1 ? t : Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * (2 * Math.PI / 3)) + 1;
    public static double OutBounce(double t)
    {
        const double n = 7.5625, d = 2.75;
        if (t < 1 / d) return n * t * t;
        if (t < 2 / d) return n * (t -= 1.5 / d) * t + 0.75;
        if (t < 2.5 / d) return n * (t -= 2.25 / d) * t + 0.9375;
        return n * (t -= 2.625 / d) * t + 0.984375;
    }
}

/// <summary>Animates a value from one number to another over time.</summary>
public sealed class Tween
{
    private readonly double _from, _to, _duration;
    private readonly Func<double, double> _ease;
    private readonly Action<double> _apply;
    private double _t;

    public Tween(double from, double to, double seconds, Action<double> apply, Func<double, double>? ease = null)
    {
        _from = from;
        _to = to;
        _duration = Math.Max(seconds, 1e-6);
        _apply = apply;
        _ease = ease ?? Ease.OutQuad;
    }

    public bool Done => _t >= _duration;
    public Action? OnComplete { get; set; }

    public void Update(double dt)
    {
        if (Done) return;
        _t = Math.Min(_t + dt, _duration);
        _apply(MathX.Lerp(_from, _to, _ease(_t / _duration)));
        if (Done) OnComplete?.Invoke();
    }
}

/// <summary>Runs a set of tweens, dropping finished ones.</summary>
public sealed class Tweens
{
    private readonly List<Tween> _items = new();

    public Tween Add(double from, double to, double seconds, Action<double> apply, Func<double, double>? ease = null)
    {
        var t = new Tween(from, to, seconds, apply, ease);
        _items.Add(t);
        return t;
    }

    public void Update(double dt)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            _items[i].Update(dt);
            if (_items[i].Done) _items.RemoveAt(i);
        }
    }

    public int Count => _items.Count;
}
