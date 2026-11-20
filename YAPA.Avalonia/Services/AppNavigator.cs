using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using YAPA.Avalonia.Assistant;
using YAPA.Avalonia.Settings;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.Windows;

namespace YAPA.Avalonia.Services;

public sealed class AppNavigator : IAppNavigator
{
    public const string ClassicTheme = "YAPA 1.0";
    public const string MotivationalTheme = "Motivational";
    public const string StudioTheme = "Studio";

    private readonly AvaloniaThemeSettings _themeSettings;
    private TimerWindowBase? _timer;
    private YAPA.Avalonia.Hub.HubWindow? _hub;
    private AssistantWindow? _assistant;
    private bool _quitting;

    public AppNavigator(AvaloniaThemeSettings themeSettings)
    {
        _themeSettings = themeSettings;
        _themeSettings.PropertyChanged += OnThemeChanged;
    }

    /// <summary>Raised just before the app shuts down, while windows still exist.</summary>
    public event Action? Quitting;

    /// <summary>The timer was shown, hidden, minimized or restored (by us or the window manager).</summary>
    public event Action<bool>? TimerShowingChanged;

    public TimerWindowBase? TimerWindow => _timer;

    internal YAPA.Avalonia.Hub.HubWindow? HubForTests => _hub;

    public bool IsSettingsOpen => _hub?.ShowsSettings == true;

    public bool IsStatisticsOpen => _hub?.ShowsInsights == true;

    /// <summary>
    /// Settings changed elsewhere (the assistant): an open Hub shows the new values, since its
    /// pages only notice changes made through them.
    /// </summary>
    public void RefreshSettings() => _hub?.RefreshPages();

    /// <summary>The Hub, made once and kept until it's closed.</summary>
    private YAPA.Avalonia.Hub.HubWindow Hub()
    {
        if (_hub == null)
        {
            _hub = new YAPA.Avalonia.Hub.HubWindow();
            _hub.Closed += (_, _) => _hub = null;
        }
        return _hub;
    }

    private void Present(YAPA.Avalonia.Hub.HubWindow hub)
    {
        hub.Show();
        if (hub.WindowState == WindowState.Minimized) hub.WindowState = WindowState.Normal;
        hub.Activate();
    }

    public void ShowTimer()
    {
        var timer = EnsureTimer();
        // States set while withdrawn survive the map; Mutter dropped them at the last hide.
        if (!timer.IsVisible) timer.ReapplyShellState();
        timer.Show();
        if (timer.WindowState == WindowState.Minimized) timer.WindowState = WindowState.Normal;
        timer.Activate();
    }

    public void HideTimer() => _timer?.Hide();

    public void ToggleTimerVisibility()
    {
        if (IsTimerShowing) HideTimer();
        else ShowTimer();
    }

    /// <summary>
    /// On screen right now. A minimized window still reports IsVisible, which
    /// made the tray's first click "hide" an already-minimized timer and only
    /// the second click bring it back.
    /// </summary>
    public bool IsTimerShowing => _timer is { IsVisible: true } t && t.WindowState != WindowState.Minimized;

    public void ShowSettings(SettingsPage page = SettingsPage.Timer)
    {
        var hub = Hub();
        hub.ShowSettings(page);
        Present(hub);
    }

    public void ShowStatistics()
    {
        var hub = Hub();
        hub.ShowInsights();
        Present(hub);
    }

    /// <summary>Your day, on <paramref name="date"/> (today by default).</summary>
    public void ShowDay(DateOnly? date = null)
    {
        var hub = Hub();
        hub.ShowDay(date);
        Present(hub);
    }

    /// <summary>The journal's week view, this week.</summary>
    public void ShowWeek()
    {
        var hub = Hub();
        hub.ShowWeek();
        Present(hub);
    }

    /// <summary>Created once and kept: it holds the conversation's on-screen state between openings.</summary>
    public void ShowAssistant() => ShowAssistant(null);

    /// <param name="over">A window to open over, palette-style (the hub); beside the timer otherwise.</param>
    public void ShowAssistant(Window? over)
    {
        _assistant ??= new AssistantWindow();
        if (over is { IsVisible: true }) _assistant.ShowOver(over);
        else _assistant.ShowNear(IsTimerShowing ? _timer : null);
    }

    public bool IsAssistantShowing => _assistant?.IsShowing == true;

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        Quitting?.Invoke();
        _hub?.Close();
        _assistant?.Close();
        _timer?.Close();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.Shutdown();
    }

    public TimerWindowBase EnsureTimer()
    {
        if (_timer != null) return _timer;
        _timer = CreateTimer();
        return _timer;
    }

    private TimerWindowBase CreateTimer()
    {
        TimerWindowBase window = _themeSettings.ActiveTheme switch
        {
            MotivationalTheme => new MotivationalWindow(),
            ClassicTheme => new MainWindow(),
            _ => new StudioWindow(),
        };

        window.PropertyChanged += (_, e) =>
        {
            if (ReferenceEquals(window, _timer) && (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty))
                TimerShowingChanged?.Invoke(IsTimerShowing);
        };

        // Closing the timer from the window manager (Alt+F4, the dock's "Quit")
        // means quit, since the app has no other main window.
        window.Closing += (_, e) =>
        {
            if (!_quitting && !_swapping && !e.IsProgrammatic) Quit();
        };
        return window;
    }

    private bool _swapping;

    /// <summary>Switching theme swaps the timer window in place, no restart needed.</summary>
    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AvaloniaThemeSettings.ActiveTheme) || _timer == null) return;

        var old = _timer;
        var wasVisible = old.IsVisible;
        _swapping = true;
        try
        {
            _timer = CreateTimer();
            if (wasVisible) _timer.Show();
            old.Close();
        }
        finally
        {
            _swapping = false;
        }
    }
}
