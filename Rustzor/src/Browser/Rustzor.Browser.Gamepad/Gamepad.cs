using Rustzor.Interop;

namespace Rustzor.Browser.Gamepad;

/// <summary>One controller state at the moment it was read.</summary>
public sealed class GamepadState
{
    public int Index { get; init; }
    public string Id { get; init; } = "";
    public bool Connected { get; init; }
    public double[] Axes { get; init; } = Array.Empty<double>();
    public bool[] Pressed { get; init; } = Array.Empty<bool>();
    public double[] Values { get; init; } = Array.Empty<double>();

    public double LeftX => Axis(0);
    public double LeftY => Axis(1);
    public double RightX => Axis(2);
    public double RightY => Axis(3);

    public double Axis(int i) => i < Axes.Length ? Axes[i] : 0;

    public bool Button(int i) => i < Pressed.Length && Pressed[i];

    // Button numbers of the "standard" mapping.
    public bool A => Button(0);
    public bool B => Button(1);
    public bool X => Button(2);
    public bool Y => Button(3);
    public bool Start => Button(9);
    public bool Up => Button(12);
    public bool Down => Button(13);
    public bool Left => Button(14);
    public bool Right => Button(15);
}

/// <summary>Game controllers. Poll once per frame.</summary>
public static class Gamepads
{
    public static List<GamepadState> Poll()
    {
        var list = new List<GamepadState>();
        foreach (var g in Js.Call("pad.poll").Items)
        {
            var axes = new List<double>();
            foreach (var a in g["axes"].Items) axes.Add(a.AsDouble());
            var pressed = new List<bool>();
            var values = new List<double>();
            foreach (var b in g["buttons"].Items)
            {
                pressed.Add(b[0].AsBool());
                values.Add(b[1].AsDouble());
            }
            list.Add(new GamepadState
            {
                Index = g["index"].AsInt(),
                Id = g["id"].AsString(),
                Connected = g["connected"].AsBool(),
                Axes = axes.ToArray(),
                Pressed = pressed.ToArray(),
                Values = values.ToArray(),
            });
        }
        return list;
    }

    /// <summary>The first connected controller, or null.</summary>
    public static GamepadState? First()
    {
        var all = Poll();
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>Rumbles the controller where the browser supports it.</summary>
    public static Task RumbleAsync(int index, int durationMs, double strong = 1, double weak = 1) =>
        Js.CallAsync("pad.rumble", Js.Args("index", index, "duration", durationMs, "strong", strong, "weak", weak));
}
