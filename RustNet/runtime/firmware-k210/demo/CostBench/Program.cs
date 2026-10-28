using RustNet.Core;
using RustNet.Sys;

namespace CostBench;

internal static class Board
{
    // Answered by the firmware's own dispatch (`ends_with("Board::UserLed()")`)
    // with a constant: the cheapest possible host call, so its cost is the
    // dispatch itself.
    [InternalCall]
    public static int UserLed() => throw new RuntimeOnlyException();
}

/// <summary>
/// The interpreter's cost model, measured on the chip: an empty loop
/// iteration, a static managed call, and a host call. The per-call figures
/// are net of the loop. Printed in nanoseconds so small costs still show.
/// </summary>
internal static class Program
{
    private const int N = 20000;

    private static int Add(int a, int b) => a + b;

    private static void Main()
    {
        int sink = 0;

        long t0 = Uptime.Ms();
        for (int i = 0; i < N; i++)
        {
            sink += i;
        }
        long loop = Uptime.Ms() - t0;

        t0 = Uptime.Ms();
        for (int i = 0; i < N; i++)
        {
            sink = Add(sink, i);
        }
        long call = Uptime.Ms() - t0;

        t0 = Uptime.Ms();
        for (int i = 0; i < N; i++)
        {
            sink += Board.UserLed();
        }
        long host = Uptime.Ms() - t0;

        Console.WriteLine($"costbench N={N}: loop {loop * 1000000 / N} ns/iter, "
            + $"static call {(call - loop) * 1000000 / N} ns, host call {(host - loop) * 1000000 / N} ns (sink {sink})");
    }
}
