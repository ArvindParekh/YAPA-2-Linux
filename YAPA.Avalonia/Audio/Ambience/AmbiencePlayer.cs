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
    /// <summary>Fades in (or updates live, if already playing). <paramref name="restartTicks"/> lines the beat up with "now".</summary>
    void Play(AmbienceMix mix, bool restartTicks);

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
/// The user's own file goes through the looping media player instead, because
/// decoding arbitrary formats in-process isn't worth the dependency. The ticking
/// still comes from the stream, so it can layer on top of a file.
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

    private readonly AmbienceMixer _mixer = new(SampleRate);
    private readonly IBackgroundMusicPlayer _filePlayer;
    private readonly Func<ICueOutputStream?> _openStream;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread? _thread;
    private readonly object _gate = new();
    private volatile bool _disposed;
    private bool _streamFailed;
    private string? _playingFile;
    private string? _fallbackLoop;

    internal AmbiencePlayer(IBackgroundMusicPlayer filePlayer, Func<ICueOutputStream?>? openStream)
    {
        _filePlayer = filePlayer;
        _openStream = openStream ?? (() => null);
        _streamFailed = openStream == null;
        if (!_streamFailed)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "YAPA ambience", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    public static IAmbiencePlayer Create(IBackgroundMusicPlayer filePlayer)
    {
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
        return new AmbiencePlayer(filePlayer, open);
    }

    public string BackendDescription => _streamFailed ? "media player (looped recording)" : AudioFactory.InProcessBackendName;

    public void Play(AmbienceMix mix, bool restartTicks)
    {
        if (_disposed) return;
        lock (_gate)
        {
            UpdateFile(mix);
            var generatedBed = mix.Sound is AmbientSound.File ? AmbientSound.None : mix.Sound;

            if (_streamFailed)
            {
                PlayFallbackLoop(mix with { Sound = generatedBed });
                return;
            }

            _mixer.Set(
                active: true,
                sound: generatedBed,
                bedGain: (float)VolumeCurve.ToGain(mix.Volume),
                tickGain: mix.Ticking ? (float)VolumeCurve.ToGain(mix.TickVolume) : 0f,
                restartTicks: restartTicks);
            _wake.Set();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _filePlayer.Stop();
            _playingFile = null;
            _fallbackLoop = null;
            _mixer.Set(active: false, AmbientSound.None, 0, 0);
        }
    }

    private void UpdateFile(AmbienceMix mix)
    {
        var wanted = mix.Sound == AmbientSound.File && !string.IsNullOrEmpty(mix.FilePath) && File.Exists(mix.FilePath)
            ? mix.FilePath
            : null;
        if (wanted == _playingFile) return;
        _filePlayer.Stop();
        _playingFile = wanted;
        if (wanted != null) _filePlayer.Play(wanted, loop: true, mix.Volume);
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

        while (!_disposed)
        {
            if (!_mixer.WantsAudio)
            {
                if (stream != null && DateTime.UtcNow - silentSince > CloseWhenIdleAfter)
                {
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
                    lock (_gate) _streamFailed = true;
                    AudioLog.Info("ambience: no in-process audio, falling back to a looped recording");
                    return;
                }
            }

            _mixer.Render(buffer);
            if (!stream.Write(buffer, out var error))
            {
                AudioLog.Once("ambience-write", $"ambience stream write failed: {error}; reopening");
                stream.Dispose();
                stream = null;
                Thread.Sleep(500);
            }
            silentSince = DateTime.UtcNow;
        }

        stream?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        _filePlayer.Stop();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _filePlayer.Dispose();
        _wake.Dispose();
    }
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
            ticks.MixInto(loop, mix.Sound == AmbientSound.None ? 0.9f : Math.Clamp(relative, 0f, 1.5f) * 0.9f);
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
