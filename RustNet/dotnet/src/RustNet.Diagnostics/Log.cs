using RustNet.Core;

namespace RustNet.Diagnostics;

/// <summary>
/// Structured log channel. Records land in the device ring buffer and every
/// attached sink (serial console, RNDP log stream, remote MQTT/HTTP).
/// </summary>
public static class Log
{
    [InternalCall]
    public static void Info(string message) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Warn(string message) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Error(string message) => throw new RuntimeOnlyException();
}
