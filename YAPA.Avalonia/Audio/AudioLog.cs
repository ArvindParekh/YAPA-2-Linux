using System.Collections.Concurrent;
using System.Diagnostics;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Diagnostics for the audio subsystem. There is no logging framework yet, so this goes
/// to <see cref="Trace"/>; every line is tagged so it can be grepped out of a session log.
/// </summary>
internal static class AudioLog
{
    private static readonly ConcurrentDictionary<string, byte> Reported = new();

    public static void Info(string message) => Trace.WriteLine("[audio] " + message);

    /// <summary>
    /// For conditions that would otherwise repeat on every cue (missing player, broken
    /// server): the first occurrence is useful, the hundredth is noise.
    /// </summary>
    public static void Once(string key, string message)
    {
        if (Reported.TryAdd(key, 0))
            Info(message);
    }
}
