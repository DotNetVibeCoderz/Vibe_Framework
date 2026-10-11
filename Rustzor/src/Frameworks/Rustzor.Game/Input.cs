using Microsoft.AspNetCore.Components;
using Rustzor.Browser.Gamepad;
using Rustzor.Interop;

namespace Rustzor.Game;

/// <summary>
/// Keyboard, pointer and gamepad state for the current frame. The game loop
/// refreshes it once per frame; read it from anywhere in update code.
/// Keys use <c>KeyboardEvent.code</c> names: "KeyW", "ArrowLeft", "Space", "Digit1".
/// </summary>
public sealed class Input
{
    private readonly HashSet<string> _down = new();
    private readonly HashSet<string> _pressed = new();
    private readonly HashSet<string> _released = new();
    private string? _elementId;

    /// <summary>Pointer coordinates are relative to this element (usually the game canvas).</summary>
    public void Attach(ElementReference element) => _elementId = element.Id;

    public Vec2 Pointer { get; private set; }
    public bool PointerDown { get; private set; }
    public bool PointerInside { get; private set; }
    /// <summary>Presses since the last frame.</summary>
    public int Clicks { get; private set; }
    public double Wheel { get; private set; }
    public GamepadState? Gamepad { get; private set; }

    /// <summary>Held now.</summary>
    public bool IsDown(string code) => _down.Contains(code);
    /// <summary>Went down this frame.</summary>
    public bool WasPressed(string code) => _pressed.Contains(code);
    /// <summary>Went up this frame.</summary>
    public bool WasReleased(string code) => _released.Contains(code);

    public bool AnyDown(params string[] codes)
    {
        foreach (var c in codes)
            if (_down.Contains(c)) return true;
        return false;
    }

    /// <summary>WASD, arrow keys and the gamepad's left stick and d-pad as one direction, length at most 1.</summary>
    public Vec2 Axis()
    {
        double x = 0, y = 0;
        if (AnyDown("KeyA", "ArrowLeft")) x -= 1;
        if (AnyDown("KeyD", "ArrowRight")) x += 1;
        if (AnyDown("KeyW", "ArrowUp")) y -= 1;
        if (AnyDown("KeyS", "ArrowDown")) y += 1;
        if (Gamepad is { } g)
        {
            if (Math.Abs(g.LeftX) > 0.15) x += g.LeftX;
            if (Math.Abs(g.LeftY) > 0.15) y += g.LeftY;
            if (g.Left) x -= 1;
            if (g.Right) x += 1;
            if (g.Up) y -= 1;
            if (g.Down) y += 1;
        }
        return new Vec2(x, y).Clamped(1);
    }

    /// <summary>Space, Enter, a click or the gamepad's A button this frame.</summary>
    public bool Action => WasPressed("Space") || WasPressed("Enter") || Clicks > 0;

    public void Poll(bool gamepads = true)
    {
        var s = Js.Call("game.input", Js.Args("el", _elementId));
        Fill(_down, s["down"]);
        Fill(_pressed, s["pressed"]);
        Fill(_released, s["released"]);
        Pointer = new Vec2(s["x"].AsDouble(), s["y"].AsDouble());
        PointerDown = s["buttons"].AsInt() != 0;
        PointerInside = s["inside"].AsBool();
        Clicks = s["clicks"].AsInt();
        Wheel = s["wheel"].AsDouble();
        Gamepad = gamepads ? Gamepads.First() : null;
    }

    private static void Fill(HashSet<string> set, JsonValue values)
    {
        set.Clear();
        foreach (var v in values.Items) set.Add(v.AsString());
    }
}
