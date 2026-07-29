using RustNet.Core;
using RustNet.Hal;
using RustNet.Threading;

namespace Blink;

/// <summary>
/// Board facts the firmware knows and the app does not. Keeping the LED pin
/// on this side of the boundary means one compiled module runs on every
/// board, instead of one per LED position.
/// </summary>
internal static class Board
{
    /// <summary>The user LED, as a HAL pin index.</summary>
    [InternalCall]
    public static int UserLed() => throw new RuntimeOnlyException();
}

/// <summary>
/// The first C# program to run on a RustNet ARM target: it drives the board's
/// user LED through the HAL, interpreted on-chip.
/// </summary>
internal static class Program
{
    private static void Main()
    {
        int led = Board.UserLed();
        Gpio.SetMode(led, PinMode.Output);

        Console.WriteLine("[C#] blinking the user LED");

        // Deliberately not the bring-up firmware's steady 100/400 ms: two
        // quick blips then a long pause. The pattern alone tells you the C#
        // app is driving the pin, rather than a leftover native loop.
        while (true)
        {
            Blip(led);
            Blip(led);
            Sleep.Ms(1200);
        }
    }

    private static void Blip(int led)
    {
        Gpio.Write(led, true);
        Sleep.Ms(60);
        Gpio.Write(led, false);
        Sleep.Ms(180);
    }
}
