using System;
using System.Diagnostics;
using System.Threading;

namespace YAPA.Avalonia.Audio;

/// <summary>Timing of one cue, for diagnostics and the silent end-to-end test.</summary>
/// <param name="Backend">"stream" or "process".</param>
/// <param name="StreamOpen">Time spent connecting, if this cue had to open the stream.</param>
/// <param name="FirstWrite">From Play() until the first chunk was accepted by the server.</param>
/// <param name="QueueFilled">From Play() until a full server buffer was queued, i.e. playback has certainly started.</param>
/// <param name="OutputLatency">Server-reported delay between writing and hearing, sampled after the last write.</param>
/// <param name="Total">From Play() until the cue finished, was interrupted, or was handed off.</param>
internal sealed record CueTelemetry(
    string Backend,
    TimeSpan? StreamOpen,
    TimeSpan? FirstWrite,
    TimeSpan? QueueFilled,
    TimeSpan? OutputLatency,
    TimeSpan Total,
    bool Interrupted,
    bool Drained);

/// <summary>
/// Plays cues on one dedicated thread.
///
/// Requests go through a single "latest wins" slot rather than a queue: a cue is only
/// meaningful right now, so if two arrive while one is playing, the middle one is dropped
/// rather than played late. Writing happens in ~10 ms chunks with a check for a newer
/// request between chunks, so an interrupt costs at most one chunk plus a flush.
///
/// The stream is opened on first use and kept open for <see cref="DefaultIdleClose"/>
/// after the last cue: pause/resume clicks come in bursts and reconnecting costs tens of
/// milliseconds, but a stream held open forever would sit in GNOME's sound settings and
/// keep the audio device awake.
/// </summary>
internal sealed class CuePlayer : ISoundCuePlayer
{
    public static readonly TimeSpan DefaultIdleClose = TimeSpan.FromSeconds(4);

    // After a failed connect, cues use the process fallback for a while instead of paying
    // the connection timeout on every cue. Retried later because the server may simply not
    // have been up yet (autostart at login).
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(30);
    private const int ChunkMilliseconds = 10;

    private readonly SoundLibrary _library;
    private readonly CueStreamFactory? _openStream;
    private readonly string _streamBackendName;
    private readonly ProcessCueBackend _fallback;
    private readonly TimeSpan _idleClose;
    private readonly Thread _worker;
    private readonly AutoResetEvent _wake = new(false);

    private Request? _pending;
    private volatile bool _disposed;
    private volatile bool _streamUsable = true;

    // Worker-thread state.
    private ICueOutputStream? _stream;
    private long _reconnectAfter;
    private float[] _chunk = Array.Empty<float>();

    private abstract record Request(long PostedAt);
    private sealed record PlayRequest(SoundRef Sound, PcmBuffer? Pcm, double Volume, long PostedAt) : Request(PostedAt);
    private sealed record StopRequest(long PostedAt) : Request(PostedAt);

    /// <param name="openStream">Null when in-process output is unavailable; every cue then uses the fallback.</param>
    public CuePlayer(SoundLibrary library, CueStreamFactory? openStream, string streamBackendName,
                     ProcessCueBackend fallback, TimeSpan? idleClose = null)
    {
        _library = library;
        _openStream = openStream;
        _streamBackendName = streamBackendName;
        _fallback = fallback;
        _idleClose = idleClose ?? DefaultIdleClose;
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "YAPA audio cues",
            // A late cue is a wrong cue; this thread sleeps almost all the time anyway.
            Priority = ThreadPriority.AboveNormal,
        };
        _worker.Start();
    }

    /// <summary>Raised on the worker thread after each cue. For diagnostics and tests.</summary>
    internal event Action<CueTelemetry>? CueFinished;

    internal bool IsStreamOpen => Volatile.Read(ref _stream) is not null;

    /// <summary>
    /// Reflects the most recent connection attempt. Before the first cue it reports the
    /// in-process backend whenever libpulse loaded, because the stream is only opened lazily.
    /// </summary>
    public string BackendDescription
        => _openStream is not null && _streamUsable ? _streamBackendName : _fallback.Description;

    public void Play(SoundRef sound, double volume) => Post(new PlayRequest(sound, null, volume, Stopwatch.GetTimestamp()));

    /// <summary>Plays raw PCM; lets tests drive the real backend with a silent buffer.</summary>
    internal void PlayPcm(PcmBuffer pcm, double volume) => Post(new PlayRequest(SoundRef.None, pcm, volume, Stopwatch.GetTimestamp()));

    public void Stop() => Post(new StopRequest(Stopwatch.GetTimestamp()));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _wake.Set();
        // Writes block for at most a chunk, so the worker notices quickly.
        if (Thread.CurrentThread != _worker && _worker.Join(TimeSpan.FromSeconds(2)))
            _wake.Dispose();
        _fallback.Dispose();
    }

    private void Post(Request request)
    {
        if (_disposed)
            return;
        Interlocked.Exchange(ref _pending, request);
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private bool Superseded => _disposed || Volatile.Read(ref _pending) is not null;

    private void Run()
    {
        try
        {
            while (!_disposed)
            {
                var request = Interlocked.Exchange(ref _pending, null);
                if (request is null)
                {
                    var signalled = _wake.WaitOne(_stream is null ? Timeout.InfiniteTimeSpan : _idleClose);
                    if (!signalled && _stream is not null)
                        CloseStream();
                    continue;
                }

                try
                {
                    Handle(request);
                }
                catch (Exception ex)
                {
                    AudioLog.Info($"cue failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AudioLog.Info($"cue worker stopped: {ex.Message}");
        }
        finally
        {
            CloseStream();
        }
    }

    private void Handle(Request request)
    {
        // Whatever comes next, the previous cue is over.
        _fallback.Stop();

        if (request is not PlayRequest play)
        {
            _stream?.Flush();
            return;
        }

        var pcm = play.Pcm;
        var cacheKey = "pcm-adhoc";
        if (pcm is null)
        {
            var decoded = _library.Load(play.Sound, out var passthroughPath);
            if (decoded is null)
            {
                if (passthroughPath is not null)
                {
                    _fallback.PlayFile(passthroughPath, play.Volume);
                    Report(new CueTelemetry("process", null, null, null, null, Since(play.PostedAt), false, false));
                }
                return;
            }
            pcm = decoded.Pcm;
            cacheKey = decoded.CacheKey;
        }

        if (TryPlayThroughStream(pcm, play) || Superseded)
            return;

        _fallback.PlayPcm(pcm, cacheKey, play.Volume);
        Report(new CueTelemetry("process", null, null, null, null, Since(play.PostedAt), false, false));
    }

    /// <summary>False if the stream can't be used, so the caller falls back to a process.</summary>
    private bool TryPlayThroughStream(PcmBuffer pcm, PlayRequest play)
    {
        if (_openStream is null)
            return false;

        if (_stream is not null && (_stream.SampleRate != pcm.SampleRate || _stream.Channels != pcm.Channels))
            CloseStream();

        TimeSpan? openTime = null;
        if (_stream is null)
        {
            if (Stopwatch.GetTimestamp() < _reconnectAfter)
                return false;

            var started = Stopwatch.GetTimestamp();
            var stream = _openStream(pcm.SampleRate, pcm.Channels, out var error);
            if (stream is null)
            {
                _streamUsable = false;
                _reconnectAfter = Stopwatch.GetTimestamp() + (long)(ReconnectBackoff.TotalSeconds * Stopwatch.Frequency);
                AudioLog.Info($"in-process audio unavailable ({error}); using {_fallback.Description}, retrying in {ReconnectBackoff.TotalSeconds:0} s");
                return false;
            }

            openTime = Since(started);
            Volatile.Write(ref _stream, stream);
            _streamUsable = true;
        }

        var gain = (float)VolumeCurve.ToGain(play.Volume);
        var chunkLength = Math.Max(1, pcm.SampleRate * ChunkMilliseconds / 1000) * pcm.Channels;
        if (_chunk.Length < chunkLength)
            _chunk = new float[chunkLength];

        var queueLength = (int)(pcm.SampleRate * 0.040) * pcm.Channels;
        TimeSpan? firstWrite = null, queueFilled = null;
        var samples = pcm.Samples;

        for (var offset = 0; offset < samples.Length; offset += chunkLength)
        {
            if (Superseded)
            {
                _stream!.Flush();
                Report(new CueTelemetry("stream", openTime, firstWrite, queueFilled, null, Since(play.PostedAt), true, false));
                return true;
            }

            var count = Math.Min(chunkLength, samples.Length - offset);
            var chunk = _chunk.AsSpan(0, count);
            for (var i = 0; i < count; i++)
                chunk[i] = Math.Clamp(samples[offset + i] * gain, -1f, 1f);

            if (!_stream!.Write(chunk, out var error))
            {
                AudioLog.Info($"in-process audio write failed ({error}); reconnecting on the next cue");
                CloseStream();
                // If nothing was heard yet, let the fallback play the whole cue; otherwise a
                // replay from the start would be worse than a cut-off cue.
                return offset > 0;
            }

            firstWrite ??= Since(play.PostedAt);
            if (queueFilled is null && offset + count >= Math.Min(queueLength, samples.Length))
                queueFilled = Since(play.PostedAt);
        }

        // Everything is queued; wait for it to be heard, but stay interruptible. Drain alone
        // would block through a newer cue.
        var latency = _stream!.Latency;
        var deadline = Stopwatch.GetTimestamp() + (long)(((latency ?? TimeSpan.FromMilliseconds(100)).TotalSeconds + 0.015) * Stopwatch.Frequency);
        while (true)
        {
            var left = deadline - Stopwatch.GetTimestamp();
            if (left <= 0)
                break;
            if (_wake.WaitOne(TimeSpan.FromSeconds((double)left / Stopwatch.Frequency)) && Superseded)
            {
                _stream.Flush();
                Report(new CueTelemetry("stream", openTime, firstWrite, queueFilled, latency, Since(play.PostedAt), true, false));
                return true;
            }
        }

        var drained = _stream.Drain();
        Report(new CueTelemetry("stream", openTime, firstWrite, queueFilled, latency, Since(play.PostedAt), false, drained));
        return true;
    }

    private void CloseStream()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
            return;
        try
        {
            stream.Dispose();
        }
        catch (Exception ex)
        {
            AudioLog.Info($"closing audio stream failed: {ex.Message}");
        }
    }

    private void Report(CueTelemetry telemetry)
    {
        try { CueFinished?.Invoke(telemetry); }
        catch { /* diagnostics must never break playback */ }
    }

    private static TimeSpan Since(long timestamp) => Stopwatch.GetElapsedTime(timestamp);
}
