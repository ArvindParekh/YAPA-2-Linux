using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using YAPA.Avalonia.Platform.Linux.DBus;
using YAPA.Avalonia.Platform.Linux.X11;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// Notes which window is in front, every few seconds, as spans of time per window: all day
/// while the user is at the computer (or only during pomodoros, with <see cref="JournalSettings.WholeDay"/>
/// off). Away is two minutes without input, unless something holds the screen awake (a video
/// playing), and never on a locked screen. YAPA's own windows and skipped apps aren't recorded.
/// While an app that pauses the journal runs (Tor Browser), nothing is recorded but a blank
/// "paused" span, which tells the journal to drop whatever other sources found in that time.
/// Sampling runs off the UI thread: a few property reads on the X server, and a look at the
/// process list for the pausing apps.
/// </summary>
public sealed class ActivityRecorder : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(2);

    private readonly IPomodoroEngine _engine;
    private readonly JournalStore _store;
    private readonly JournalSettings _settings;
    private readonly IdleInhibition _inhibition;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private X11ActiveWindow? _x11;
    private bool _x11Tried;
    private bool _running;
    private bool _working;
    private bool _disposed;

    private long _spanId;
    private string? _spanKey;

    public ActivityRecorder(IPomodoroEngine engine, JournalStore store, JournalSettings settings, IdleInhibition? inhibition = null)
    {
        _engine = engine;
        _store = store;
        _settings = settings;
        _inhibition = inhibition ?? new IdleInhibition();
        _timer = new Timer(_ => Sample(), null, Timeout.Infinite, Timeout.Infinite);
        _engine.PropertyChanged += OnEngineChanged;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(JournalSettings.RecordWindows) or nameof(JournalSettings.WholeDay) or nameof(JournalSettings.PauseFor)) Update();
        };
        Update();
    }

    private void OnEngineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPomodoroEngine.Phase) or nameof(IPomodoroEngine.IsRunning)) Update();
    }

    /// <summary>Starts or stops sampling. Reads the engine here, on its own thread, not from the timer's.</summary>
    private void Update()
    {
        var working = _engine.Phase == PomodoroPhase.Work && _engine.IsRunning;
        // The pausing apps are watched even when windows aren't recorded: they stop the other sources too.
        var wanted = (_settings.WholeDay || working) && (_settings.RecordWindows || _settings.PauseApps.Count > 0);
        lock (_gate)
        {
            _working = working;
            if (_disposed || wanted == _running) return;
            _running = wanted;
            if (wanted) _timer.Change(TimeSpan.Zero, Interval);
            else
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                Close();
            }
        }
    }

    private void Sample()
    {
        // Timer callbacks can overlap when one is slow; the second just skips.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (!_running || _disposed) return;
            SampleLocked();
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[journal] recording failed: {e.Message}");
            Close();
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void SampleLocked()
    {
        var now = DateTime.UtcNow;
        if (PausingApps.AnyRunning(_settings.PauseApps))
        {
            Record(now, JournalStore.Paused, "");
            return;
        }
        if (!_settings.RecordWindows || (!_settings.WholeDay && !_working))
        {
            Close();
            return;
        }

        if (!_x11Tried)
        {
            _x11Tried = true;
            _x11 = X11ActiveWindow.TryCreate();
            if (_x11 == null) Trace.WriteLine("[journal] no X11: windows aren't recorded");
        }
        if (_x11 == null)
        {
            Close();
            return;
        }

        ActiveWindow? window;
        try
        {
            // Not touching anything is still being here while a video plays.
            if (_x11.Idle() is { } idle && idle >= Away && !Watching())
            {
                Close();
                return;
            }
            window = _x11.Current();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _x11 = null;
            return;
        }
        if (window == null || window.ProcessId == Environment.ProcessId || IsDesktop(window))
        {
            Close();
            return;
        }

        var app = AppName(window);
        // Skipped apps and private windows: as if they were never open.
        if (_settings.IsIgnored(app, window.Class, window.Instance, window.Title))
        {
            Close();
            return;
        }
        Record(now, app, window.Title);
    }

    private bool Watching()
    {
        try
        {
            // On the timer's thread, which has nothing else to do: waiting is fine.
            var asking = _inhibition.IsWatchingAsync();
            return asking.Wait(TimeSpan.FromSeconds(2)) && asking.Result;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private void Record(DateTime now, string app, string title)
    {
        var key = app + "\n" + title;
        if (key == _spanKey && _spanId != 0)
        {
            _store.ExtendSpan(_spanId, now);
            return;
        }
        // The new window has been in front since about the last sample.
        var start = _spanId != 0 ? now - Interval / 2 : now;
        if (_spanId != 0) _store.ExtendSpan(_spanId, start);
        _spanId = _store.BeginSpan(start, app, title);
        _spanKey = key;
    }

    private void Close()
    {
        if (_spanId != 0) _store.ExtendSpan(_spanId, DateTime.UtcNow);
        _spanId = 0;
        _spanKey = null;
    }

    /// <summary>
    /// The bare desktop: GNOME's desktop icons are a window ("Desktop Icons 1", from Gjs) that
    /// takes focus when the desktop is clicked. Nothing is being done in it.
    /// </summary>
    public static bool IsDesktop(ActiveWindow window)
        => window.Class is "Gjs" or "gjs" && window.Title.StartsWith("Desktop Icons", StringComparison.Ordinal)
           || window.Class is "gnome-shell" or "Gnome-shell" or "desktop_window" or "Xfdesktop" or "plasmashell";

    /// <summary>A name people use for the app, from its window class.</summary>
    public static string AppName(ActiveWindow window)
    {
        if (window.Instance.Contains("ms-playwright", StringComparison.Ordinal) || window.Instance.Contains("--remote-debugging", StringComparison.Ordinal))
            return "Browser (driven by an agent)";
        var c = window.Class;
        return c.ToLowerInvariant() switch
        {
            "com.anthropic.claude" or "claude" => "Claude",
            "zen" or "zen-browser" => "Zen",
            "firefox" or "firefox_firefox" => "Firefox",
            "google-chrome" => "Chrome",
            "chromium" or "chromium-browser" => "Chromium",
            "brave-browser" => "Brave",
            "code" or "code-oss" => "VS Code",
            "cursor" => "Cursor",
            "gnome-terminal-server" or "gnome-terminal" or "org.gnome.terminal" or "kgx" or "org.gnome.console" or "ptyxis" or "org.gnome.ptyxis" => "Terminal",
            "kitty" => "kitty",
            "alacritty" => "Alacritty",
            "wezterm" or "org.wezfurlong.wezterm" => "WezTerm",
            "slack" => "Slack",
            "discord" => "Discord",
            "obsidian" => "Obsidian",
            "thunderbird" or "thunderbird-esr" => "Thunderbird",
            "org.gnome.nautilus" or "nautilus" => "Files",
            "zoom" => "Zoom",
            "spotify" => "Spotify",
            "telegramdesktop" or "telegram-desktop" or "org.telegram.desktop" => "Telegram",
            "" => "Unknown",
            var lower when lower.StartsWith("jetbrains-", StringComparison.Ordinal) => "JetBrains " + c["jetbrains-".Length..],
            _ => Prettify(c),
        };
    }

    // "org.gnome.TextEditor" → "TextEditor"; "libreoffice-writer" → "Libreoffice writer".
    private static string Prettify(string c)
    {
        var last = c.Contains('.') ? c[(c.LastIndexOf('.') + 1)..] : c;
        last = last.Replace('-', ' ').Replace('_', ' ');
        return last.Length > 0 ? char.ToUpperInvariant(last[0]) + last[1..] : c;
    }

    public void Dispose()
    {
        _engine.PropertyChanged -= OnEngineChanged;
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            Close();
            _x11?.Dispose();
        }
        _inhibition.Dispose();
    }
}
