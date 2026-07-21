using RustNet.Core;

namespace RustNet.Sys;

/// <summary>Monotonic time since device boot.</summary>
public static class Uptime
{
    [InternalCall]
    public static long Ms() => throw new RuntimeOnlyException();
}

/// <summary>Identity and health of the device the app is running on.</summary>
public static class DeviceInfo
{
    [InternalCall]
    public static string Chip() => throw new RuntimeOnlyException();

    [InternalCall]
    public static string Board() => throw new RuntimeOnlyException();

    [InternalCall]
    public static string Version() => throw new RuntimeOnlyException();

    [InternalCall]
    public static long UptimeMs() => throw new RuntimeOnlyException();

    /// <summary>Full device summary as JSON (chip, board, version, uptime).</summary>
    [InternalCall]
    public static string Json() => throw new RuntimeOnlyException();
}

/// <summary>
/// Power management: sleep modes, wake sources (GPIO edge / RTC alarm),
/// shutdown and reset. Arm wake sources before sleeping or shutting down.
/// </summary>
public static class Power
{
    public const int Light = 0;
    public const int Deep = 1;
    public const int Hibernate = 2;

    [InternalCall]
    public static int BatteryMillivolts() => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Sleep(int mode, int durationMs) => throw new RuntimeOnlyException();

    /// <summary>Wake on a GPIO edge (true = rising) during the next sleep/shutdown.</summary>
    [InternalCall]
    public static void ArmWakeGpio(int pin, bool rising) => throw new RuntimeOnlyException();

    /// <summary>Wake via RTC alarm after the given number of seconds.</summary>
    [InternalCall]
    public static void ArmWakeRtc(int seconds) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void ClearWakeSources() => throw new RuntimeOnlyException();

    /// <summary>Why the chip is running: power-on, rtc-alarm, gpio, watchdog-reset, ...</summary>
    [InternalCall]
    public static string WakeReason() => throw new RuntimeOnlyException();

    /// <summary>Reboot the device. On the virtual device this halts the app.</summary>
    [InternalCall]
    public static void Reset() => throw new RuntimeOnlyException();

    /// <summary>Power off entirely; only armed wake sources bring it back.</summary>
    [InternalCall]
    public static void Shutdown() => throw new RuntimeOnlyException();
}

/// <summary>Battery-backed real-time clock (epoch = seconds since 1970 UTC).</summary>
public static class Rtc
{
    [InternalCall]
    public static long Epoch() => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Set(long epoch) => throw new RuntimeOnlyException();

    /// <summary>Current time as "YYYY-MM-DD HH:MM:SS".</summary>
    [InternalCall]
    public static string NowString() => throw new RuntimeOnlyException();

    /// <summary>Arm the RTC alarm at an absolute epoch second (deep-sleep wake source).</summary>
    [InternalCall]
    public static void SetAlarm(long epoch) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void ClearAlarm() => throw new RuntimeOnlyException();
}

/// <summary>Hardware watchdog: once started, feed it within the timeout or the chip resets.</summary>
public static class Watchdog
{
    [InternalCall]
    public static void Start(int timeoutMs) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Feed() => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Stop() => throw new RuntimeOnlyException();

    [InternalCall]
    public static bool IsRunning() => throw new RuntimeOnlyException();
}

/// <summary>
/// External memories hanging off the MCU. Index 0 is QSPI flash (erase
/// before rewrite), index 1 is SDRAM on boards that have it.
/// </summary>
public static class ExtMemory
{
    [InternalCall]
    public static int Size(int index) => throw new RuntimeOnlyException();

    /// <summary>"qspi-flash" or "sdram".</summary>
    [InternalCall]
    public static string Kind(int index) => throw new RuntimeOnlyException();

    [InternalCall]
    public static byte[] Read(int index, int address, int length) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Write(int index, int address, byte[] data) => throw new RuntimeOnlyException();

    /// <summary>Erase to 0xFF (QSPI flash only); rounds to sector boundaries.</summary>
    [InternalCall]
    public static void Erase(int index, int address, int length) => throw new RuntimeOnlyException();

    [InternalCall]
    public static int SectorSize(int index) => throw new RuntimeOnlyException();
}
