using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace YAPA.Avalonia.Audio;

/// <param name="Executable">Resolved absolute path.</param>
/// <param name="Name">Bare tool name, for logs and the diagnostics UI.</param>
/// <param name="Arguments">One entry per argv token. Never joined into a string: "-volume 50"
/// as one token is exactly the bug that made ffplay fail before.</param>
internal sealed record PlayerCommand(string Executable, string Name, IReadOnlyList<string> Arguments);

/// <summary>Builds command lines for external players, in order of preference.</summary>
internal static class PlayerCommands
{
    private static readonly HashSet<string> SndfileFormats = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".flac", ".ogg", ".oga", ".opus" };
    private static readonly HashSet<string> PaplayFormats = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".flac", ".ogg", ".oga" };
    private static readonly HashSet<string> MpegFormats = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".mp2", ".mp1", ".mpga" };

    /// <summary>
    /// Any media file, volume applied by the player. Used for background music and for
    /// cue files that can't be decoded in-process.
    /// </summary>
    public static IReadOnlyList<PlayerCommand> ForMedia(string path, double sliderVolume, ExecutableResolver resolver)
    {
        var gain = VolumeCurve.ToGain(sliderVolume);
        var ext = Path.GetExtension(path);
        var commands = new List<PlayerCommand>();

        if (OperatingSystem.IsMacOS())
        {
            Add(commands, resolver, "afplay", "-v", F(gain), path);
            return commands;
        }

        // pw-play goes through libsndfile, so it only knows these containers. Its --volume
        // is a linear stream volume (1.0 = unity).
        if (SndfileFormats.Contains(ext))
            Add(commands, resolver, "pw-play", "--volume", F(gain), path);

        // ffplay's -volume is linear 0..100 (an SDL mix scale).
        Add(commands, resolver, "ffplay", "-nodisp", "-autoexit", "-loglevel", "quiet",
            "-volume", I(Math.Round(gain * 100)), path);

        // gst-play's --volume is playbin's linear volume. --no-interactive stops it from
        // putting the controlling terminal into raw mode when YAPA is started from a shell.
        Add(commands, resolver, "gst-play-1.0", "--no-interactive", "-q", "--volume", F(gain), path);

        // mpg123 only decodes MPEG audio; -f is a linear scale where 32768 is unity.
        if (MpegFormats.Contains(ext))
            Add(commands, resolver, "mpg123", "-q", "-f", I(Math.Round(gain * 32768)), path);

        // paplay's --volume is a pa_volume_t, which PulseAudio maps cubically
        // (linear = (v / 65536)^3), so invert that to land on the same gain.
        if (PaplayFormats.Contains(ext))
            Add(commands, resolver, "paplay", "--volume", I(Math.Round(Math.Cbrt(gain) * 65536)), path);

        return commands;
    }

    /// <summary>A 16-bit PCM WAV that already has the volume baked in.</summary>
    public static IReadOnlyList<PlayerCommand> ForPcmWav(string path, ExecutableResolver resolver)
    {
        var commands = new List<PlayerCommand>();
        if (OperatingSystem.IsMacOS())
        {
            Add(commands, resolver, "afplay", path);
            return commands;
        }

        Add(commands, resolver, "pw-play", path);
        Add(commands, resolver, "paplay", path);
        Add(commands, resolver, "aplay", "-q", path);
        return commands;
    }

    private static void Add(List<PlayerCommand> commands, ExecutableResolver resolver, string tool, params string[] args)
    {
        if (resolver.Resolve(tool) is { } executable)
            commands.Add(new PlayerCommand(executable, tool, args));
    }

    // Invariant culture: in a de_DE session "0.5" would otherwise become "0,5", which every
    // one of these tools rejects.
    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    private static string I(double v) => ((long)v).ToString(CultureInfo.InvariantCulture);
}
