using System;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using YAPA.Avalonia.Platform.Linux.X11;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// Notes which window is in front while a pomodoro runs, every few seconds, as spans of
/// time per window. Only during work: breaks, pauses and time away from the keyboard aren't
/// recorded. YAPA's own windows don't count. Reading it is two property reads on the X
/// server; nothing is sampled when the timer isn't running a pomodoro.
/// </summary>
public sealed class ActivityRecorder : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(2);

    private readonly IPomodoroEngine _engine;
    private readonly JournalStore _store;
    private readonly JournalSettings _settings;
    private readonly DispatcherTimer _timer;
    private X11ActiveWindow? _x11;
    private bool _x11Tried;

    private long _spanId;
    private string? _spanKey;

    public ActivityRecorder(IPomodoroEngine engine, JournalStore store, JournalSettings settings)
    {
        _engine = engine;
        _store = store;
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += (_, _) => Sample();
        _engine.PropertyChanged += OnEngineChanged;
        _settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(JournalSettings.RecordWindows)) Update(); };
        Update();
    }

    private bool Working => _engine.Phase == PomodoroPhase.Work && _engine.IsRunning;

    private void OnEngineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPomodoroEngine.Phase) or nameof(IPomodoroEngine.IsRunning)) Update();
    }

    private void Update()
    {
        if (Working && _settings.RecordWindows)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
                Sample();
            }
        }
        else
        {
            _timer.Stop();
            Close();
        }
    }

    private void Sample()
    {
        if (!_x11Tried)
        {
            _x11Tried = true;
            _x11 = X11ActiveWindow.TryCreate();
            if (_x11 == null) Trace.WriteLine("[journal] no X11: windows aren't recorded");
        }
        if (_x11 == null)
        {
            _timer.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        ActiveWindow? window;
        try
        {
            if (_x11.Idle() is { } idle && idle >= Away)
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
        if (window == null || window.ProcessId == Environment.ProcessId)
        {
            Close();
            return;
        }

        var app = AppName(window);
        // A password manager or a private window still counts as time, but nothing more about it.
        var title = _settings.IsIgnored(app, window.Class, window.Title) ? "" : window.Title;
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
        _timer.Stop();
        Close();
        _engine.PropertyChanged -= OnEngineChanged;
        _x11?.Dispose();
    }
}
