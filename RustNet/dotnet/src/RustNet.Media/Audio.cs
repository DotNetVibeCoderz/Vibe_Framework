using RustNet.Core;

namespace RustNet.Media;

/// <summary>
/// PCM audio playback over the I2S HAL (v0.8, chip-gated). Configure the
/// sink, then push little-endian 16-bit PCM buffers. On the virtual device
/// the samples flow into the I2S simulator so pipelines are testable without
/// a DAC/amp; on real silicon they reach the I2S peripheral.
/// </summary>
public static class Audio
{
    /// <summary>Configure the audio sink before playing.</summary>
    [InternalCall]
    public static void Configure(int sampleRate, int bitsPerSample, int channels)
        => throw new RuntimeOnlyException();

    /// <summary>Queue a little-endian 16-bit PCM buffer for playback.
    /// Returns the number of samples the sink accepted.</summary>
    [InternalCall]
    public static int Play(byte[] pcm) => throw new RuntimeOnlyException();

    /// <summary>Cumulative PCM samples the sink has accepted since boot.</summary>
    [InternalCall]
    public static int SamplesPlayed() => throw new RuntimeOnlyException();
}
