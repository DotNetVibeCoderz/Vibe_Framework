using RustNet.Core;

namespace RustNet.Hal;

/// <summary>GPIO pin modes (values match the runtime).</summary>
public static class PinMode
{
    public const int Input = 0;
    public const int InputPullUp = 1;
    public const int InputPullDown = 2;
    public const int Output = 3;
    public const int OutputOpenDrain = 4;
}

public static class Gpio
{
    [InternalCall]
    public static void SetMode(int pin, int mode) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Write(int pin, bool high) => throw new RuntimeOnlyException();

    [InternalCall]
    public static bool Read(int pin) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Toggle(int pin) => throw new RuntimeOnlyException();
}

public static class Adc
{
    [InternalCall]
    public static int ReadRaw(int channel) => throw new RuntimeOnlyException();

    [InternalCall]
    public static int ReadMillivolts(int channel) => throw new RuntimeOnlyException();
}

public static class Pwm
{
    /// <summary>Configure and enable a PWM channel. Duty is 0..10000 (hundredths of %).</summary>
    [InternalCall]
    public static void Configure(int channel, int frequencyHz, int dutyPermyriad)
        => throw new RuntimeOnlyException();
}

public static class I2c
{
    [InternalCall]
    public static void Write(int bus, int address, byte[] data) => throw new RuntimeOnlyException();

    [InternalCall]
    public static byte[] Read(int bus, int address, int length) => throw new RuntimeOnlyException();
}

/// <summary>Serial ports beyond the console (ESP32: 1 = TX4/RX5, 2 = TX17/RX16).</summary>
public static class Uart
{
    [InternalCall]
    public static void Configure(int port, int baud) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Write(int port, byte[] data) => throw new RuntimeOnlyException();

    /// <summary>Non-blocking-ish read of up to maxBytes (may return empty).</summary>
    [InternalCall]
    public static byte[] Read(int port, int maxBytes) => throw new RuntimeOnlyException();

    [InternalCall]
    public static int Available(int port) => throw new RuntimeOnlyException();
}

/// <summary>
/// TinyCLR-style precise signal control on a GPIO pin: timed edge trains
/// (SignalGenerator), pulse capture (SignalCapture) and echo measurement
/// (PulseFeedback, e.g. HC-SR04). Timings are microseconds.
/// </summary>
public static class Signal
{
    /// <summary>Raw u32-LE timing bytes; prefer <see cref="Generate"/>.</summary>
    [InternalCall]
    public static void GenerateRaw(int pin, bool initialHigh, byte[] timingsUs) => throw new RuntimeOnlyException();

    /// <summary>Raw u32-LE width bytes; prefer <see cref="Capture"/>.</summary>
    [InternalCall]
    public static byte[] CaptureRaw(int pin, int maxEdges, int timeoutUs) => throw new RuntimeOnlyException();

    /// <summary>Send a trigger pulse and measure the echo width in µs (0 = timeout).</summary>
    [InternalCall]
    public static int PulseFeedback(int pin, bool pulseHigh, int pulseUs, int timeoutUs) => throw new RuntimeOnlyException();

    /// <summary>Drive a timed edge train: pin starts at initialHigh, toggling after each duration.</summary>
    public static void Generate(int pin, bool initialHigh, int[] timingsUs)
    {
        byte[] raw = new byte[timingsUs.Length * 4];
        for (int i = 0; i < timingsUs.Length; i++)
        {
            raw[i * 4] = (byte)timingsUs[i];
            raw[i * 4 + 1] = (byte)(timingsUs[i] >> 8);
            raw[i * 4 + 2] = (byte)(timingsUs[i] >> 16);
            raw[i * 4 + 3] = (byte)(timingsUs[i] >> 24);
        }
        GenerateRaw(pin, initialHigh, raw);
    }

    /// <summary>Record pulse widths (µs between successive edges).</summary>
    public static int[] Capture(int pin, int maxEdges, int timeoutUs)
    {
        byte[] raw = CaptureRaw(pin, maxEdges, timeoutUs);
        int[] widths = new int[raw.Length / 4];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = raw[i * 4] | (raw[i * 4 + 1] << 8) | (raw[i * 4 + 2] << 16) | (raw[i * 4 + 3] << 24);
        }
        return widths;
    }
}
