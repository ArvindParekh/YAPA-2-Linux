using System;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Decoded audio: interleaved float samples, nominally in [-1, 1].
/// Float is the one format every source (any WAV flavour, the synthesizer) converts to
/// losslessly enough, and PulseAudio accepts it natively.
/// </summary>
internal sealed class PcmBuffer
{
    public PcmBuffer(float[] samples, int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (samples.Length % channels != 0) throw new ArgumentException("Sample count is not a whole number of frames.", nameof(samples));

        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public float[] Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int FrameCount => Samples.Length / Channels;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);

    public float Peak()
    {
        var peak = 0f;
        foreach (var s in Samples)
            peak = Math.Max(peak, Math.Abs(s));
        return peak;
    }

    public double Rms()
    {
        if (Samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in Samples)
            sum += (double)s * s;
        return Math.Sqrt(sum / Samples.Length);
    }
}
