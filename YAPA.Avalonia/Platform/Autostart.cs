using System;
using System.Diagnostics;
using System.IO;

namespace YAPA.Avalonia.Platform;

/// <summary>
/// "Launch at login" through the XDG autostart spec: a desktop entry in
/// ~/.config/autostart. The file itself is the setting, so it stays in sync
/// with GNOME Tweaks and similar tools that edit the same directory.
/// </summary>
public static class Autostart
{
    private static string EntryPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "yapa2.desktop");

    public static bool IsSupported => OperatingSystem.IsLinux() && !AppPaths.IsIsolated;

    public static bool IsEnabled => IsSupported && File.Exists(EntryPath);

    public static void SetEnabled(bool enabled, bool startHidden)
    {
        if (!IsSupported) return;
        try
        {
            if (!enabled)
            {
                File.Delete(EntryPath);
                return;
            }

            // Prefer the installed launcher so the entry survives reinstalls to a new location.
            var exec = LauncherPath() ?? Environment.ProcessPath ?? "yapa2";
            Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
            AtomicFile.WriteAllText(EntryPath, $"""
                [Desktop Entry]
                Type=Application
                Name=YAPA 2
                Comment=Pomodoro timer
                Exec="{exec}"{(startHidden ? " --hide" : "")}
                Icon=yapa2
                X-GNOME-Autostart-enabled=true
                X-GNOME-Autostart-Delay=3
                NoDisplay=true
                """.Replace("\r", "") + "\n");
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[autostart] {(enabled ? "enable" : "disable")} failed: {e.Message}");
        }
    }

    private static string? LauncherPath()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "yapa2"),
                     "/usr/local/bin/yapa2",
                     "/usr/bin/yapa2",
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
