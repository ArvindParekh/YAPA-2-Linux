using System;
using YAPA.Avalonia.Audio.PulseAudio;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Entry point for the app. Cues and music get separate players with separate output
/// paths, so stopping the music on a phase change can never cut off the phase's cue.
/// </summary>
public static class AudioFactory
{
    public const string InProcessBackendName = "PulseAudio (in-process)";

    /// <summary>
    /// Linux: in-process PulseAudio, falling back to pw-play / paplay / aplay per cue if the
    /// library is missing or the server refuses the connection. macOS: afplay.
    /// Windows: silent (the WPF build is the Windows app).
    /// </summary>
    public static ISoundCuePlayer CreateCuePlayer()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return new NullCuePlayer("not supported on Windows");

            var fallback = new ProcessCueBackend(ExecutableResolver.Default, SystemProcessLauncher.Instance, AppPaths.CacheDirectory);

            CueStreamFactory? openStream = null;
            if (PulseSimpleNative.TryLoad(out var reason))
                openStream = (int rate, int channels, out string error) => PulseSimpleStream.TryOpen(rate, channels, out error);
            else if (OperatingSystem.IsLinux())
                AudioLog.Info($"in-process audio disabled: {reason}; cues will use {fallback.Description}");

            return new CuePlayer(SoundLibrary.Shared, openStream, InProcessBackendName, fallback);
        }
        catch (Exception ex)
        {
            AudioLog.Info($"cue player could not be created: {ex.Message}");
            return new NullCuePlayer("audio initialisation failed");
        }
    }

    public static IBackgroundMusicPlayer CreateMusicPlayer()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return new NullMusicPlayer();

            var resolver = ExecutableResolver.Default;
            return new ProcessAudioPlayer(
                "music",
                (path, volume) => PlayerCommands.ForMedia(path, volume, resolver),
                SystemProcessLauncher.Instance,
                TimeProvider.System);
        }
        catch (Exception ex)
        {
            AudioLog.Info($"music player could not be created: {ex.Message}");
            return new NullMusicPlayer();
        }
    }
}
