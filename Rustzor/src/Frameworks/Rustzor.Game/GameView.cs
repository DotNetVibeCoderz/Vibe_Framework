using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Rustzor.Browser.Canvas;

namespace Rustzor.Game;

/// <summary>Everything a game needs each frame, handed to the <see cref="GameView"/> callbacks.</summary>
public sealed class GameContext
{
    internal GameContext(Canvas2D canvas, GameLoop loop)
    {
        Canvas = canvas;
        Loop = loop;
    }

    public Canvas2D Canvas { get; }
    public GameLoop Loop { get; }
    public Input Input => Loop.Input;
    public World World { get; } = new();
    public PhysicsWorld Physics { get; } = new();
    public Camera2D Camera { get; } = new();
    public Particles Particles { get; } = new();
    public Tweens Tweens { get; } = new();
    public Timers Timers { get; } = new();
    public Rng Random { get; } = new((ulong)Environment.TickCount64 | 1);
    public double Width => Canvas.Width;
    public double Height => Canvas.Height;
    /// <summary>Anything the game wants to keep between frames.</summary>
    public Dictionary<string, object?> State { get; } = new();
}

/// <summary>
/// A full-size game canvas with a running loop.
/// <code>
/// &lt;GameView Height="480" OnStart="Start" OnUpdate="Update" OnDraw="Draw" /&gt;
/// </code>
/// OnUpdate runs at a fixed 60 Hz after the built-in physics, particles, tweens
/// and timers have stepped; OnDraw runs once per frame.
/// </summary>
public sealed class GameView : ComponentBase, IDisposable
{
    private ElementReference _canvas;
    private GameContext? _context;

    [Parameter] public int Height { get; set; } = 480;
    /// <summary>CSS background behind the canvas.</summary>
    [Parameter] public string Background { get; set; } = "#0b0f14";
    [Parameter] public string? Class { get; set; }
    [Parameter] public Action<GameContext>? OnStart { get; set; }
    [Parameter] public Action<GameContext, double>? OnUpdate { get; set; }
    [Parameter] public Action<GameContext>? OnDraw { get; set; }
    /// <summary>Steps the physics world before OnUpdate.</summary>
    [Parameter] public bool UsePhysics { get; set; } = true;
    /// <summary>Clears the canvas before OnDraw.</summary>
    [Parameter] public bool AutoClear { get; set; } = true;

    public GameContext? Context => _context;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "canvas");
        builder.AddAttribute(1, "class", Class);
        builder.AddAttribute(2, "tabindex", "0");
        builder.AddAttribute(3, "style", $"display:block;width:100%;height:{Height}px;background:{Background};border-radius:12px;outline:none;touch-action:none");
        builder.AddElementReferenceCapture(4, r => _canvas = r);
        builder.CloseElement();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender) return;
        var g = Canvas2D.FromElement(_canvas);
        GameContext? ctx = null;
        var loop = new GameLoop(dt => Step(ctx!, dt), _ => Draw(ctx!));
        ctx = new GameContext(g, loop);
        _context = ctx;
        loop.Input.Attach(_canvas);
        ctx.Camera.Position = new Vec2(g.Width / 2, g.Height / 2);
        OnStart?.Invoke(ctx);
        loop.Start();
    }

    private void Step(GameContext ctx, double dt)
    {
        if (UsePhysics) ctx.Physics.Step(dt);
        ctx.Particles.Update(dt);
        ctx.Tweens.Update(dt);
        ctx.Timers.Update(dt);
        ctx.Camera.Update(dt);
        OnUpdate?.Invoke(ctx, dt);
        ctx.World.Step(dt);
    }

    private void Draw(GameContext ctx)
    {
        var g = ctx.Canvas;
        // The page around the canvas can reflow after the first render (a side
        // panel fills in, the window resizes). Re-read the element's size twice
        // a second, or the backing store stays at its first size and stretches.
        if (ctx.Loop.Frame % 30 == 0) g.Fit();
        if (AutoClear) g.Clear();
        OnDraw?.Invoke(ctx);
        g.Flush();
    }

    public void Dispose()
    {
        _context?.Loop.Stop();
        _context?.Canvas.Dispose();
    }
}
