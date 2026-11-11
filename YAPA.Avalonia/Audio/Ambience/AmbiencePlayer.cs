using System;
using System.IO;
using System.Threading;
using YAPA.Avalonia.Audio.PulseAudio;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Audio.Ambience;

/// <param name="Sound">The bed. <see cref="AmbientSound.File"/> plays <paramref name="FilePath"/> on loop.</param>
/// <param name="Volume">0..1 slider value for the bed.</param>
/// <param name="Ticking">Layer a ticking clock on top.</param>
/// <param name="TickVolume">0..1 slider value for the ticking.</param>
public sealed record AmbienceMix(AmbientSound Sound, string? FilePath, double Volume, bool Ticking, double TickVolume)
{
    public static readonly AmbienceMix Silent = new(AmbientSound.None, null, 0, false, 0);

    public bool IsAudible => (Sound != AmbientSound.None && Volume > 0) || (Ticking && TickVolume > 0);
}

/// <summary>Background sound while a session runs: rain or noise, a ticking clock, or the user's own file.</summary>
public interface IAmbiencePlayer : IDisposable
{
    /// <summary>Fades in (or updates live, if already playing). <paramref name="ticks"/> says where the
    /// timer's seconds fall, so ticks land on them; null keeps the previous timing.
    /// <paramref name="windDown"/> thins it out towards the interval's end; null doesn't.</summary>
    void Play(AmbienceMix mix, TickAnchor? ticks, WindDown? windDown = null);

    /// <summary>Fades out.</summary>
    void Stop();

    string BackendDescription { get; }
}

/// <summary>
/// Generated sounds stream continuously through their own PulseAudio stream
/// (named "Ambience" in the sound settings, so its volume can be set
/// separately). The stream is opened on demand and closed when silent, so YAPA
/// doesn't hold an audio stream open all day.
///
/// A recording plays in the same stream, decoded by ffmpeg or GStreamer (see
/// <see cref="RecordingGenerator"/>), so its volume follows the slider live and it
/// fades like the generated sounds. Only without a decoder does it go to the looping
/// media player, whose volume is fixed when it starts.
///
/// Without PulseAudio, a seamless loop is rendered to the cache and played by
/// the media player: no live fades, but the same sound.
/// </summary>
public sealed class AmbiencePlayer : IAmbiencePlayer
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private static readonly TimeSpan Block = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan StreamBuffer = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CloseWhenIdleAfter = TimeSpan.FromSeconds(3);

    private readonly AmbienceMixer _mixer;
    private readonly Func<string, IAmbienceGenerator?>? _openRecording;
    private readonly IBackgroundMusicPlayer _filePlayer;
    private readonly Func<ICueOutputStream?> _openStream;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread? _thread;
    private readonly object _gate = new();
    private volatile bool _disposed;
    /// <summary>No in-process stream is possible at all (no libpulse).</summary>
    private readonly bool _noStream;

    /// <summary>
    /// After a failed connection, use the looped-recording fallback until this time, then
    /// try the stream again. The server may simply not be up yet (YAPA launched at login
    /// a moment before PipeWire), so one failure must not demote the whole run.
    /// </summary>
    private DateTime _streamRetryAt = DateTime.MinValue;
    private readonly TimeSpan RetryAfter;
    private AmbienceMix _lastMix = AmbienceMix.Silent;
    private bool _active;
    private string? _playingFile;
    private string? _fallbackLoop;

    /// <param name="openRecording">Decodes a recording into the stream; null when no decoder is installed.</param>
    internal AmbiencePlayer(IBackgroundMusicPlayer filePlayer, Func<ICueOutputStream?>? openStream, TimeSpan? retryAfter = null,
        Func<string, IAmbienceGenerator?>? openRecording = null)
    {
        RetryAfter = retryAfter ?? TimeSpan.FromSeconds(30);
        _openRecording = openRecording;
        _mixer = new AmbienceMixer(SampleRate, bed => bed is { Sound: AmbientSound.File, FilePath: { } path }
            ? _openRecording?.Invoke(path)
            : AmbienceGenerators.Create(bed.Sound, SampleRate, (ulong)Environment.TickCount64));
        _filePlayer = filePlayer;
        _openStream = openStream ?? (() => null);
        _noStream = openStream == null;
        if (!_noStream)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "YAPA ambience", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    public static IAmbiencePlayer Create(IBackgroundMusicPlayer filePlayer)
    {
        if (AudioFactory.Silenced) return new NullAmbiencePlayer();
        Func<ICueOutputStream?>? open = null;
        if (PulseSimpleNative.TryLoad(out _))
        {
            open = () =>
            {
                var stream = PulseSimpleStream.TryOpen(SampleRate, Channels, "Ambience", StreamBuffer, out var error);
                if (stream == null) AudioLog.Once("ambience-open:" + error, $"ambience stream failed to open: {error}");
                return stream;
            };
        }
        Func<string, IAmbienceGenerator?>? recording = RecordingGenerator.CanDecode(ExecutableResolver.Default)
            ? path => RecordingGenerator.TryCreate(path, ExecutableResolver.Default)
            : null;
        return new AmbiencePlayer(filePlayer, open, openRecording: recording);
    }

    private bool UsingFallback => _noStream || DateTime.UtcNow < _streamRetryAt;

    public string BackendDescription => UsingFallback ? "media player (looped recording)" : AudioFactory.InProcessBackendName;

    public void Play(AmbienceMix mix, TickAnchor? ticks, WindDown? windDown = null)
    {
        if (_disposed) return;
        lock (_gate)
        {
            _lastMix = mix;
            _active = true;
            var generatedBed = mix.Sound is AmbientSound.File ? AmbientSound.None : mix.Sound;
            var recording = RecordingPath(mix);

            if (UsingFallback)
            {
                UpdateFile(recording, mix.Volume);
                PlayFallbackLoop(mix with { Sound = generatedBed });
                return;
            }

            // Back on the stream after a fallback period: the loop must stop or both would play.
            if (_fallbackLoop != null)
            {
                _filePlayer.Stop();
                _fallbackLoop = null;
                _playingFile = null;
            }

            var inStream = recording != null && _openRecording != null;
            UpdateFile(inStream ? null : recording, mix.Volume);
            _mixer.Set(
                active: true,
                sound: inStream ? new AmbientBed(AmbientSound.File, recording) : generatedBed,
                bedGain: (float)VolumeCurve.ToGain(mix.Volume),
                tickGain: mix.Ticking ? (float)VolumeCurve.ToGain(mix.TickVolume) : 0f,
                anchor: ticks,
                windDown: windDown); // the looped-file fallback has no live gain, so it plays on unchanged
            _wake.Set();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            _filePlayer.Stop();
            _playingFile = null;
            _fallbackLoop = null;
            _mixer.Deactivate();
        }
    }

    private static string? RecordingPath(AmbienceMix mix)
        => mix.Sound == AmbientSound.File && !string.IsNullOrEmpty(mix.FilePath) && File.Exists(mix.FilePath) ? mix.FilePath : null;

    /// <summary>The external-player route for a recording (null: none playing that way).</summary>
    private void UpdateFile(string? wanted, double volume)
    {
        if (wanted == _playingFile) return;
        _filePlayer.Stop();
        _playingFile = wanted;
        if (wanted != null) _filePlayer.Play(wanted, loop: true, volume);
    }

    private void PlayFallbackLoop(AmbienceMix mix)
    {
        if (!mix.IsAudible || mix.Sound == AmbientSound.None && !mix.Ticking)
        {
            if (_playingFile == null) _filePlayer.Stop();
            return;
        }
        // The media player can't mix two files, so the user's own file wins over generated sound.
        if (_playingFile != null) return;

        var path = AmbienceLoop.EnsureRendered(mix, AppPaths.CacheDirectory);
        if (path == null || path == _fallbackLoop) return;
        _fallbackLoop = path;
        // Volume is baked into the loop at full scale; the player's own volume does the rest.
        _filePlayer.Play(path, loop: true, Math.Max(mix.Volume, mix.Ticking ? mix.TickVolume : 0));
    }

    private void Run()
    {
        var buffer = new float[(int)(SampleRate * Block.TotalSeconds) * Channels];
        ICueOutputStream? stream = null;
        var silentSince = DateTime.MinValue;
        TimeSpan? nextHeardAt = null;

        while (!_disposed)
        {
            if (!_mixer.WantsAudio)
            {
                if (stream != null && DateTime.UtcNow - silentSince > CloseWhenIdleAfter)
                {
                    nextHeardAt = null;
                    stream.Drain();
                    stream.Dispose();
                    stream = null;
                }
                if (stream == null)
                {
                    _wake.WaitOne();
                    continue;
                }
                // Keep feeding silence briefly before closing: a quick resume then needs no reconnect.
                Array.Clear(buffer);
                stream.Write(buffer, out _);
                continue;
            }

            if (stream == null)
            {
                stream = _openStream();
                if (stream == null)
                {
                    lock (_gate)
                    {
                        _streamRetryAt = DateTime.UtcNow + RetryAfter;
                        AudioLog.Info($"ambience: sound server unavailable, using a looped recording; retrying in {RetryAfter.TotalSeconds:0} s");
                        // Silence the mixer (it has nowhere to play) and hand over to the fallback.
                        _mixer.Set(active: false, AmbientSound.None, 0, 0);
                        if (_active)
                        {
                            var generatedBed = _lastMix.Sound is AmbientSound.File ? AmbientSound.None : _lastMix.Sound;
                            UpdateFile(RecordingPath(_lastMix), _lastMix.Volume);
                            PlayFallbackLoop(_lastMix with { Sound = generatedBed });
                        }
                    }
                    _retryTimer ??= new Timer(_ => RetryStream(), null, Timeout.Infinite, Timeout.Infinite);
                    _retryTimer.Change(RetryAfter, Timeout.InfiniteTimeSpan);
                    continue;
                }
            }

            _mixer.Render(buffer, NextHeardAt(stream, ref nextHeardAt));
            if (!stream.Write(buffer, out var error))
            {
                AudioLog.Once("ambience-write", $"ambience stream write failed: {error}; reopening");
                stream.Dispose();
                stream = null;
                nextHeardAt = null;
                Thread.Sleep(500);
            }
            silentSince = DateTime.UtcNow;
        }

        stream?.Dispose();
    }

    private static readonly TimeSpan Resync = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// When the block about to be written will be heard. Blocks follow each other
    /// exactly, so the timeline is advanced by one block each time; it is only snapped
    /// back to "now + measured latency" when the two disagree by more than
    /// <see cref="Resync"/> (start, underrun, device clock drift). A contiguous
    /// timeline is what guarantees each tick is rendered exactly once.
    /// </summary>
    private static TimeSpan NextHeardAt(ICueOutputStream stream, ref TimeSpan? next)
    {
        var measured = System.Diagnostics.Stopwatch.GetElapsedTime(0) + (stream.Latency ?? StreamBuffer);
        var heardAt = next is { } n && (n - measured).Duration() <= Resync ? n : measured;
        next = heardAt + Block;
        return heardAt;
    }

    private Timer? _retryTimer;

    /// <summary>Retry period over: if something is playing, move it back onto the stream.</summary>
    private void RetryStream()
    {
        AmbienceMix mix;
        lock (_gate)
        {
            if (!_active || _disposed) return;
            mix = _lastMix;
        }
        Play(mix, ticks: null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer?.Dispose();
        _wake.Set();
        _filePlayer.Stop();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _filePlayer.Dispose();
        _wake.Dispose();
    }
}

public sealed class NullAmbiencePlayer : IAmbiencePlayer
{
    public void Play(AmbienceMix mix, TickAnchor? ticks, WindDown? windDown = null) { }
    public void Stop() { }
    public string BackendDescription => "none";
    public void Dispose() { }
}

/// <summary>A seamless recording of a generated mix, for players that can only loop a file.</summary>
internal static class AmbienceLoop
{
    private const int SampleRate = 48_000;
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Seam = TimeSpan.FromSeconds(2);

    /// <summary>Renders the mix to a WAV whose end crossfades into its start, so looping it has no seam.</summary>
    public static float[] Render(AmbienceMix mix, ulong seed = 0x5EED)
    {
        var bodyFrames = (int)(SampleRate * Length.TotalSeconds);
        var seamFrames = (int)(SampleRate * Seam.TotalSeconds);
        var total = new float[(bodyFrames + seamFrames) * 2];

        var bed = AmbienceGenerators.Create(mix.Sound, SampleRate, seed);
        // Full scale: the media player applies the user's volume on playback.
        bed?.MixInto(total, 0.9f);

        // Equal-power crossfade of the overhang onto the start.
        var loop = new float[bodyFrames * 2];
        Array.Copy(total, loop, loop.Length);
        for (var f = 0; f < seamFrames; f++)
        {
            var t = f / (float)seamFrames;
            var fadeIn = MathF.Sin(t * MathF.PI / 2);
            var fadeOut = MathF.Cos(t * MathF.PI / 2);
            for (var c = 0; c < 2; c++)
                loop[f * 2 + c] = loop[f * 2 + c] * fadeIn + total[(bodyFrames + f) * 2 + c] * fadeOut;
        }

        // Ticks are periodic (1 s divides 60 s), so they need no crossfade and are added after.
        if (mix.Ticking)
        {
            var ticks = new TickGenerator(SampleRate);
            var relative = mix.Volume > 0 ? (float)(VolumeCurve.ToGain(mix.TickVolume) / Math.Max(VolumeCurve.ToGain(mix.Volume), 1e-6)) : 1f;
            // A file loop can't follow the timer, so its ticks simply start on the loop's first frame.
            ticks.MixInto(loop, mix.Sound == AmbientSound.None ? 0.9f : Math.Clamp(relative, 0f, 1.5f) * 0.9f,
                TimeSpan.Zero, new TickAnchor(TimeSpan.Zero, 0));
        }

        for (var i = 0; i < loop.Length; i++) loop[i] = MathF.Tanh(loop[i]);
        return loop;
    }

    public static string? EnsureRendered(AmbienceMix mix, string cacheDirectory)
    {
        try
        {
            var dir = Path.Combine(cacheDirectory, "ambience");
            Directory.CreateDirectory(dir);
            var key = $"{mix.Sound}-{(mix.Ticking ? $"tick{Math.Round(mix.TickVolume / Math.Max(mix.Volume, 0.01), 1)}" : "notick")}.wav";
            var path = Path.Combine(dir, key);
            if (!File.Exists(path))
            {
                WavWriter.WritePcm16(path, new PcmBuffer(Render(mix), SampleRate, 2));
            }
            return path;
        }
        catch (Exception e)
        {
            AudioLog.Info($"ambience loop render failed: {e.Message}");
            return null;
        }
    }
}
