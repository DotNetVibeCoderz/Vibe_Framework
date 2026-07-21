using RustNet.Core;

namespace RustNet.Threading;

/// <summary>
/// Cooperative delays. On device this yields to the RustNet scheduler so
/// other tasks (network, timers) keep running while the app waits.
/// </summary>
public static class Sleep
{
    [InternalCall]
    public static void Ms(int milliseconds) => throw new RuntimeOnlyException();

    public static void Seconds(int seconds) => Ms(seconds * 1000);
}
