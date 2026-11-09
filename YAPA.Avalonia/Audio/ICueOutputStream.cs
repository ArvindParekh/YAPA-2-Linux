using System;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// An open, blocking PCM output stream with a fixed format. The seam between the cue
/// scheduling logic and PulseAudio, so the interrupt/idle behaviour is testable.
/// </summary>
internal interface ICueOutputStream : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>Blocks until the server accepts the samples.</summary>
    bool Write(ReadOnlySpan<float> interleaved, out string error);

    /// <summary>Discards audio queued on the server but not yet played.</summary>
    bool Flush();

    /// <summary>Blocks until everything written has been played.</summary>
    bool Drain();

    /// <summary>Time until audio written now would be heard; null if unknown.</summary>
    TimeSpan? Latency { get; }
}

internal delegate ICueOutputStream? CueStreamFactory(int sampleRate, int channels, out string error);
