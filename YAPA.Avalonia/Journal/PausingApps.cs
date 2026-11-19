using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// Whether an app that stops the journal (Tor Browser) is running, from the processes on the
/// machine rather than the window in front: it pauses everything while it's open at all,
/// focused or not, minimised or on another workspace.
/// </summary>
public static class PausingApps
{
    /// <summary>
    /// True when a running program's path has a part that names one of <paramref name="apps"/>,
    /// spelled loosely: "Tor Browser" matches …/tor-browser/Browser/firefox.real,
    /// …/torbrowser/…, and torbrowser-launcher, but "tor" doesn't match "editor".
    /// </summary>
    public static bool AnyRunning(IReadOnlyList<string> apps, string proc = "/proc")
    {
        var names = apps.Select(Squash).Where(n => n.Length > 1).ToList();
        if (names.Count == 0 || !Directory.Exists(proc)) return false;
        foreach (var dir in Enumerate(proc))
        {
            var pid = Path.GetFileName(dir);
            if (pid.Length == 0 || !pid.All(char.IsAsciiDigit)) continue;
            if (Program(dir) is { Length: > 0 } program && Names(program, names)) return true;
        }
        return false;
    }

    /// <summary>A program path names an app when one of its parts starts with the app's name.</summary>
    public static bool Names(string programPath, IReadOnlyList<string> squashedNames)
    {
        foreach (var part in programPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Squash(part);
            if (squashedNames.Any(n => p.StartsWith(n, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    /// <summary>"Tor Browser", "tor-browser" and "TorBrowser" are one name.</summary>
    public static string Squash(string name)
    {
        var s = new StringBuilder(name.Length);
        foreach (var c in name)
            if (char.IsLetterOrDigit(c)) s.Append(char.ToLowerInvariant(c));
        return s.ToString();
    }

    // argv[0]: the program as it was started, path included (a flatpak's too, as its sandbox sees it).
    private static string? Program(string dir)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(dir, "cmdline"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
            Span<byte> buffer = stackalloc byte[512];
            var read = stream.Read(buffer);
            var end = buffer[..read].IndexOf((byte)0);
            return Encoding.UTF8.GetString(buffer[..(end >= 0 ? end : read)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // gone since the listing, or not ours to read
        }
    }

    private static IEnumerable<string> Enumerate(string proc)
    {
        try { return Directory.GetDirectories(proc); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
