using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Finds command-line players without spawning <c>which</c>.
///
/// PATH alone is not enough: an app launched from the GNOME app grid gets the session's
/// minimal PATH, not the one from the user's shell rc files, so tools installed through
/// Homebrew or into ~/.local/bin are invisible to it. Those locations are appended.
/// </summary>
internal sealed class ExecutableResolver
{
    private static readonly Lazy<ExecutableResolver> DefaultInstance = new(() => new ExecutableResolver(
        BuildSearchPath(Environment.GetEnvironmentVariable("PATH"),
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        IsExecutableFile));

    private readonly Func<string, bool> _isExecutable;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);

    public ExecutableResolver(IEnumerable<string> searchDirectories, Func<string, bool> isExecutable)
    {
        SearchDirectories = searchDirectories
            .Where(d => !string.IsNullOrWhiteSpace(d) && Path.IsPathRooted(d))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _isExecutable = isExecutable;
    }

    public static ExecutableResolver Default => DefaultInstance.Value;

    public IReadOnlyList<string> SearchDirectories { get; }

    /// <summary>
    /// Absolute path of the first match, or null. Results (including misses) are cached for
    /// the life of the process: these lookups run on every cue, and players don't come and go.
    /// </summary>
    public string? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (name.Contains('/') || name.Contains(Path.DirectorySeparatorChar))
            return _isExecutable(name) ? name : null;
        return _cache.GetOrAdd(name, Find);
    }

    private string? Find(string name)
    {
        foreach (var dir in SearchDirectories)
        {
            var candidate = Path.Combine(dir, name);
            if (_isExecutable(candidate))
                return candidate;
            if (OperatingSystem.IsWindows() && _isExecutable(candidate + ".exe"))
                return candidate + ".exe";
        }
        return null;
    }

    public static IReadOnlyList<string> BuildSearchPath(string? pathVariable, string homeDirectory)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(pathVariable))
            dirs.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        dirs.Add("/usr/bin");
        dirs.Add("/usr/local/bin");
        dirs.Add("/bin");
        dirs.Add("/home/linuxbrew/.linuxbrew/bin");
        if (!string.IsNullOrEmpty(homeDirectory))
        {
            dirs.Add(Path.Combine(homeDirectory, ".local", "bin"));
            dirs.Add(Path.Combine(homeDirectory, ".linuxbrew", "bin"));
        }
        return dirs.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            if (OperatingSystem.IsWindows())
                return true;
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch
        {
            return false;
        }
    }
}
