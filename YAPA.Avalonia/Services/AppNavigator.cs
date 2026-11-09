using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using YAPA.Avalonia.Settings;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.Windows;

namespace YAPA.Avalonia.Services;

public sealed class AppNavigator : IAppNavigator
{
    public const string ClassicTheme = "YAPA 1.0";
    public const string MotivationalTheme = "Motivational";

    private readonly AvaloniaThemeSettings _themeSettings;
    private TimerWindowBase? _timer;
    private SettingsWindow? _settings;
    private StatisticsWindow? _statistics;
    private bool _quitting;

    public AppNavigator(AvaloniaThemeSettings themeSettings)
    {
        _themeSettings = themeSettings;
        _themeSettings.PropertyChanged += OnThemeChanged;
    }

    /// <summary>Raised just before the app shuts down, while windows still exist.</summary>
    public event Action? Quitting;

    public TimerWindowBase? TimerWindow => _timer;

    public void ShowTimer()
    {
        var timer = EnsureTimer();
        timer.Show();
        if (timer.WindowState == WindowState.Minimized) timer.WindowState = WindowState.Normal;
        timer.Activate();
    }

    public void HideTimer() => _timer?.Hide();

    public void ToggleTimerVisibility()
    {
        if (_timer is { IsVisible: true }) HideTimer();
        else ShowTimer();
    }

    public void ShowSettings(SettingsPage page = SettingsPage.Timer)
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Navigate(page);
        _settings.Show();
        _settings.Activate();
    }

    public void ShowStatistics()
    {
        if (_statistics == null)
        {
            _statistics = new StatisticsWindow();
            _statistics.Closed += (_, _) => _statistics = null;
        }
        _statistics.Show();
        _statistics.Activate();
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        Quitting?.Invoke();
        _settings?.Close();
        _statistics?.Close();
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
        TimerWindowBase window = _themeSettings.ActiveTheme == MotivationalTheme
            ? new MotivationalWindow()
            : new MainWindow();

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
