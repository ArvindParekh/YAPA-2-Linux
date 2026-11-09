using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

/// <summary>
/// Settings files on disk. Writes are coalesced: dragging a slider changes a
/// setting dozens of times a second, and each change used to rewrite and fsync
/// the file. The latest content is written shortly after changes stop, and on
/// <see cref="Flush"/> (called at exit).
/// </summary>
public sealed class CrossPlatformEnvironment : IEnvironment, IDisposable
{
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(400);

    private readonly PendingFile _settings = new(Path.Combine(AppPaths.ConfigDirectory, "settings.json"));
    private readonly PendingFile _localSettings = new(Path.Combine(AppPaths.ConfigDirectory, "localSettings.json"));

    public CrossPlatformEnvironment() => AppPaths.EnsureCreated();

    public string GetSettings() => _settings.Read();
    public void SaveSettings(string settings) => _settings.Write(settings);

    public string GetLocalSettings() => _localSettings.Read();
    public void SaveLocalSettings(string settings) => _localSettings.Write(settings);

    public string GetPluginDirectory() => Path.Combine(AppPaths.DataDirectory, "Plugins");
    public string GetThemeDirectory() => Path.Combine(AppPaths.DataDirectory, "Themes");

    public bool PreRelease() => false;

    public void Flush()
    {
        _settings.Flush();
        _localSettings.Flush();
    }

    public void Dispose()
    {
        Flush();
        _settings.Dispose();
        _localSettings.Dispose();
    }

    private sealed class PendingFile(string path) : IDisposable
    {
        private readonly object _lock = new();
        private Timer? _timer;
        private string? _pending;

        public string Read()
        {
            lock (_lock)
            {
                // Pending content is newer than the file.
                if (_pending != null) return _pending;
            }
            return File.Exists(path) ? File.ReadAllText(path) : "{}";
        }

        public void Write(string contents)
        {
            lock (_lock)
            {
                _pending = contents;
                _timer ??= new Timer(_ => Flush());
                _timer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Flush()
        {
            string? contents;
            lock (_lock)
            {
                contents = _pending;
                _pending = null;
                if (contents == null) return;
                try
                {
                    AtomicFile.WriteAllText(path, contents);
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"[settings] write {path} failed: {e.Message}");
                }
            }
        }

        public void Dispose()
        {
            lock (_lock) _timer?.Dispose();
        }
    }
}
