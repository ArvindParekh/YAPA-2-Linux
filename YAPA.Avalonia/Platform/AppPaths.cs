using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YAPA.Avalonia.Platform;

/// <summary>
/// Single source of truth for where YAPA keeps its files.
///
/// Setting <c>YAPA_HOME</c> puts config, data and the single-instance lock under
/// one directory. That gives a fully isolated instance (development, tests,
/// portable installs) that can run next to the user's real one.
/// </summary>
public static class AppPaths
{
    public static string ConfigDirectory { get; }
    public static string DataDirectory { get; }
    public static string CacheDirectory { get; }

    /// <summary>Stable per-profile key for the single-instance mutex and IPC socket.</summary>
    public static string InstanceKey { get; }

    public static bool IsIsolated { get; }

    static AppPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var isolated = Environment.GetEnvironmentVariable("YAPA_HOME");

        if (!string.IsNullOrWhiteSpace(isolated))
        {
            IsIsolated = true;
            var root = Path.GetFullPath(isolated);
            ConfigDirectory = Path.Combine(root, "config");
            DataDirectory = Path.Combine(root, "data");
            CacheDirectory = Path.Combine(root, "cache");
        }
        else if (OperatingSystem.IsWindows())
        {
            // Matches the original WPF app so existing settings and history are reused.
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            ConfigDirectory = DataDirectory = Path.Combine(docs, "YAPA2");
            CacheDirectory = Path.Combine(Path.GetTempPath(), "YAPA2");
        }
        else
        {
            ConfigDirectory = Path.Combine(Xdg("XDG_CONFIG_HOME", Path.Combine(home, ".config")), "YAPA2");
            DataDirectory = Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), "YAPA2");
            CacheDirectory = Path.Combine(Xdg("XDG_CACHE_HOME", Path.Combine(home, ".cache")), "YAPA2");
        }

        InstanceKey = IsIsolated
            ? $"{Environment.UserName}-{ShortHash(ConfigDirectory)}"
            : Environment.UserName;
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    private static string Xdg(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        // The XDG spec says relative paths are invalid and must be ignored.
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) ? value : fallback;
    }

    private static string ShortHash(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..10].ToLowerInvariant();
}
