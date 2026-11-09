using System;
using System.IO;
using System.Linq;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Plays cues through external players when the in-process PulseAudio path is unavailable.
///
/// Decoded cues are written to the cache directory as 16-bit WAV with the volume baked
/// in, because aplay (the last resort) has no volume option and this way every player
/// sounds the same. Files nobody can decode in-process (MP3, OGG...) go straight to a
/// media player with a volume flag instead.
/// </summary>
internal sealed class ProcessCueBackend : IDisposable
{
    private const string CueDirectoryName = "audio-cues";

    private readonly ExecutableResolver _resolver;
    private readonly string _cueDirectory;
    private readonly ProcessAudioPlayer _wavPlayer;
    private readonly ProcessAudioPlayer _mediaPlayer;
    private bool _cueDirectoryPrepared;

    public ProcessCueBackend(ExecutableResolver resolver, IProcessLauncher launcher, string cacheDirectory)
    {
        _resolver = resolver;
        _cueDirectory = Path.Combine(cacheDirectory, CueDirectoryName);
        _wavPlayer = new ProcessAudioPlayer("cue", (path, _) => PlayerCommands.ForPcmWav(path, resolver), launcher, TimeProvider.System);
        _mediaPlayer = new ProcessAudioPlayer("cue", (path, volume) => PlayerCommands.ForMedia(path, volume, resolver), launcher, TimeProvider.System);
    }

    /// <summary>The tool that would play the next cue, e.g. "pw-play".</summary>
    public string Description
    {
        get
        {
            // Probed with a dummy path: only the tool lookup matters here, not the file.
            var first = PlayerCommands.ForPcmWav("/dev/null", _resolver).FirstOrDefault();
            return first?.Name ?? "none (no audio player found)";
        }
    }

    public void PlayPcm(PcmBuffer pcm, string cacheKey, double volume)
    {
        Stop();
        if (EnsureWav(pcm, cacheKey, volume) is { } path)
            _wavPlayer.Play(path, loop: false, volume: 1.0);
    }

    public void PlayFile(string path, double volume)
    {
        Stop();
        _mediaPlayer.Play(path, loop: false, volume);
    }

    public void Stop()
    {
        _wavPlayer.Stop();
        _mediaPlayer.Stop();
    }

    public void Dispose()
    {
        _wavPlayer.Dispose();
        _mediaPlayer.Dispose();
    }

    internal string? EnsureWav(PcmBuffer pcm, string cacheKey, double volume)
    {
        try
        {
            PrepareCueDirectory();

            // One file per (sound, volume step). 1% steps are below what anyone can hear and
            // keep the number of distinct files small.
            var percent = (int)Math.Round(Math.Clamp(double.IsNaN(volume) ? 0 : volume, 0, 1) * 100);
            var path = Path.Combine(_cueDirectory, $"{cacheKey}-v{percent:D3}.wav");
            if (File.Exists(path))
                return path;

            // Write-then-rename so a player started concurrently never reads half a file.
            var temp = path + ".tmp";
            WavWriter.WritePcm16(temp, pcm, VolumeCurve.ToGain(percent / 100.0));
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            AudioLog.Once("cue-cache", $"cannot write cue files to {_cueDirectory}: {ex.Message}");
            return null;
        }
    }

    // Files from earlier runs may come from an older synthesizer, so they're cleared once per
    // process rather than trusted. Within a run, the cache key pins the content.
    private void PrepareCueDirectory()
    {
        if (_cueDirectoryPrepared)
            return;
        Directory.CreateDirectory(_cueDirectory);
        foreach (var file in Directory.EnumerateFiles(_cueDirectory))
        {
            try { File.Delete(file); }
            catch { /* in use or vanished; harmless */ }
        }
        _cueDirectoryPrepared = true;
    }
}
