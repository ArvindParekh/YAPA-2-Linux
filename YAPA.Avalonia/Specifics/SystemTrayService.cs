using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

/// <summary>
/// Manages the system tray icon with a context menu.
/// Lifecycle: created in App.OnFrameworkInitializationCompleted, disposed on app exit.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly IMainViewModel _viewModel;
    private bool _disposed;

    public SystemTrayService(IMainViewModel viewModel)
    {
        _viewModel = viewModel;

        _tray = new TrayIcon();

        using var stream = AssetLoader.Open(new Uri("avares://YAPA.Avalonia/Assets/pomoTray.ico"));
        _tray.Icon = new WindowIcon(stream);

        _tray.ToolTipText = "YAPA 2";
        _tray.IsVisible = true;
        _tray.Menu = BuildMenu();
        _tray.Clicked += OnTrayClicked;
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("Show");
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Add(showItem);

        menu.Add(new NativeMenuItemSeparator());

        var startItem = new NativeMenuItem("Start");
        startItem.Click += (_, _) => _viewModel.StartCommand.Execute(null);
        menu.Add(startItem);

        var pauseItem = new NativeMenuItem("Pause");
        pauseItem.Click += (_, _) => _viewModel.PauseCommand.Execute(null);
        menu.Add(pauseItem);

        var stopItem = new NativeMenuItem("Stop");
        stopItem.Click += (_, _) => _viewModel.StopCommand.Execute(null);
        menu.Add(stopItem);

        var skipItem = new NativeMenuItem("Skip");
        skipItem.Click += (_, _) => _viewModel.SkipCommand.Execute(null);
        menu.Add(skipItem);

        menu.Add(new NativeMenuItemSeparator());

        var settingsItem = new NativeMenuItem("Settings");
        settingsItem.Click += (_, _) => _viewModel.ShowSettingsCommand.Execute(null);
        menu.Add(settingsItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        };
        menu.Add(exitItem);

        return menu;
    }

    private static void ShowMainWindow()
    {
        if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            var win = lifetime.MainWindow;
            if (win != null)
            {
                win.Show();
                win.WindowState = WindowState.Normal;
                win.Activate();
            }
        }
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowMainWindow();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
