using System;
using System.Runtime.InteropServices;

namespace YAPA.Avalonia.Audio.PulseAudio;

/// <summary>
/// One float32 playback stream. Not thread-safe: pa_simple isn't, and only the cue worker
/// thread touches it.
/// </summary>
internal sealed class PulseSimpleStream : ICueOutputStream
{
    public const string ApplicationName = "YAPA 2";
    public const string StreamName = "Timer sounds";

    /// <summary>
    /// Server-side buffer target. It bounds both start-up latency and how much already-queued
    /// audio a flush has to discard, so it is kept small. Cues are written faster than real
    /// time, so a small buffer can't underrun mid-cue.
    /// </summary>
    public static readonly TimeSpan TargetLatency = TimeSpan.FromMilliseconds(40);

    private readonly PulseSimpleNative.SimpleHandle _handle;

    private PulseSimpleStream(PulseSimpleNative.SimpleHandle handle, int sampleRate, int channels)
    {
        _handle = handle;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }
    public int Channels { get; }

    public static PulseSimpleStream? TryOpen(int sampleRate, int channels, out string error)
    {
        try
        {
            var spec = new PulseSimpleNative.SampleSpec
            {
                Format = BitConverter.IsLittleEndian ? PulseSimpleNative.SampleFloat32Le : PulseSimpleNative.SampleFloat32Be,
                Rate = (uint)sampleRate,
                Channels = (byte)channels,
            };

            var frameBytes = channels * sizeof(float);
            var tlength = (uint)(sampleRate * TargetLatency.TotalSeconds) * (uint)frameBytes;
            var attributes = new PulseSimpleNative.BufferAttr
            {
                MaxLength = PulseSimpleNative.Default,
                TLength = tlength,
                // Default prebuf equals tlength; playback starts as soon as that much is queued,
                // which for a cue is microseconds after the first writes.
                PreBuf = PulseSimpleNative.Default,
                MinReq = PulseSimpleNative.Default,
                FragSize = PulseSimpleNative.Default,
            };

            // A null channel map makes the server pick its default layout for the channel count
            // (mono, stereo, 5.1 ...), falling back to AUX channels for exotic counts.
            var handle = PulseSimpleNative.pa_simple_new(
                null, ApplicationName, PulseSimpleNative.StreamPlayback, null, StreamName,
                ref spec, IntPtr.Zero, ref attributes, out var code);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                error = PulseSimpleNative.Describe(code);
                return null;
            }

            error = "";
            return new PulseSimpleStream(handle, sampleRate, channels);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = ex.Message;
            return null;
        }
    }

    public bool Write(ReadOnlySpan<float> interleaved, out string error)
    {
        var bytes = MemoryMarshal.AsBytes(interleaved);
        if (bytes.IsEmpty)
        {
            error = "";
            return true;
        }

        // Blocks until the server has room, i.e. for at most about one chunk of audio.
        if (PulseSimpleNative.pa_simple_write(_handle, ref MemoryMarshal.GetReference(bytes), (nuint)bytes.Length, out var code) < 0)
        {
            error = PulseSimpleNative.Describe(code);
            return false;
        }
        error = "";
        return true;
    }

    public bool Flush() => PulseSimpleNative.pa_simple_flush(_handle, out _) >= 0;

    public bool Drain() => PulseSimpleNative.pa_simple_drain(_handle, out _) >= 0;

    public TimeSpan? Latency
    {
        get
        {
            var usec = PulseSimpleNative.pa_simple_get_latency(_handle, out _);
            return usec == ulong.MaxValue ? null : TimeSpan.FromTicks((long)usec * 10);
        }
    }

    public void Dispose() => _handle.Dispose();
}
