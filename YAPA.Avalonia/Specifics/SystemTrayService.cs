using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.ViewModels;

namespace YAPA.Avalonia.Specifics;

/// <summary>
/// Tray icon (StatusNotifierItem on Linux; GNOME needs the AppIndicator
/// extension, which Ubuntu ships enabled). The menu mirrors the timer's
/// context menu and only offers what makes sense in the current phase.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private readonly TimerViewModel _vm;
    private readonly IAppNavigator _navigator;
    private readonly TrayIcon _tray;

    private readonly NativeMenuItem _showHide = new("Hide timer");
    private readonly NativeMenuItem _start = new("Start pomodoro");
    private readonly NativeMenuItem _pause = new("Pause");
    private readonly NativeMenuItem _skip = new("Skip break");
    private readonly NativeMenuItem _stop = new("Stop");
    private readonly NativeMenuItem _reset = new("Reset cycle");
    private bool _disposed;
    private bool _frozen;

    public SystemTrayService(TimerViewModel vm, IAppNavigator navigator)
    {
        _vm = vm;
        _navigator = navigator;

        _tray = new TrayIcon();
        using (var stream = AssetLoader.Open(new Uri("avares://YAPA.Avalonia/Assets/pomoTray.ico")))
            _tray.Icon = new WindowIcon(stream);
        _tray.Menu = BuildMenu();
        _tray.Clicked += (_, _) => _navigator.ToggleTimerVisibility();
        _tray.IsVisible = true;

        _vm.PropertyChanged += OnViewModelChanged;
        Refresh();
    }

    /// <summary>Lets the window's state drive the Show/Hide label.</summary>
    public void SetTimerVisible(bool visible)
    {
        if (_frozen) return;
        _showHide.Header = visible ? "Hide timer" : "Show timer";
    }

    /// <summary>
    /// Stops all menu updates. Called when quitting starts: Avalonia's D-Bus
    /// menu exporter queues a layout update for every change, and one that runs
    /// after the tray is disposed during shutdown throws inside Avalonia.
    /// </summary>
    public void Freeze()
    {
        _frozen = true;
        _vm.PropertyChanged -= OnViewModelChanged;
    }

    private NativeMenu BuildMenu()
    {
        _showHide.Click += (_, _) => _navigator.ToggleTimerVisibility();
        _start.Click += (_, _) => _vm.StartCommand.Execute(null);
        _pause.Click += (_, _) => _vm.PauseCommand.Execute(null);
        _skip.Click += (_, _) => _vm.SkipCommand.Execute(null);
        _stop.Click += (_, _) => _vm.StopCommand.Execute(null);
        _reset.Click += (_, _) => _vm.ResetCommand.Execute(null);

        var stats = new NativeMenuItem("Statistics…");
        stats.Click += (_, _) => _navigator.ShowStatistics();
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => _navigator.ShowSettings();
        var quit = new NativeMenuItem("Quit YAPA");
        quit.Click += (_, _) => _navigator.Quit();

        var menu = new NativeMenu();
        menu.Add(_showHide);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_start);
        menu.Add(_pause);
        menu.Add(_skip);
        menu.Add(_stop);
        menu.Add(_reset);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(stats);
        menu.Add(settings);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);
        return menu;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Summary changes every second; only the tooltip depends on it, and
        // rebuilding the D-Bus menu that often would be wasteful.
        if (e.PropertyName == nameof(TimerViewModel.Summary)) _tray.ToolTipText = _vm.Summary;
        else if (e.PropertyName is nameof(TimerViewModel.CanStart) or nameof(TimerViewModel.CanPause)
                 or nameof(TimerViewModel.CanSkip) or nameof(TimerViewModel.CanStop) or nameof(TimerViewModel.CanReset)
                 or nameof(TimerViewModel.StartMenuText) or nameof(TimerViewModel.StopMenuText))
            Refresh();
    }

    private void Refresh()
    {
        if (_disposed) return;
        _tray.ToolTipText = _vm.Summary;
        _start.Header = _vm.StartMenuText;
        _start.IsVisible = _vm.CanStart;
        _pause.IsVisible = _vm.CanPause;
        _skip.IsVisible = _vm.CanSkip;
        _stop.Header = _vm.StopMenuText;
        _stop.IsVisible = _vm.CanStop;
        _reset.IsEnabled = _vm.CanReset;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.PropertyChanged -= OnViewModelChanged;
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
