using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using YAPA.Avalonia.Bootstrap;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.Windows;

namespace YAPA.Avalonia;

public partial class App : Application
{
    public static AppBootstrapper? Bootstrapper { get; private set; }

    // Raised on the UI thread when a second instance sends command-line arguments.
    public static event Action<string[]>? ExternalCommandLine;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Bootstrapper = new AppBootstrapper();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Select main window based on theme setting
            var themeSettings = Bootstrapper.Resolve<AvaloniaThemeSettings>();
            desktop.MainWindow = themeSettings.ActiveTheme == "Motivational"
                ? (global::Avalonia.Controls.Window) new MotivationalWindow()
                : new MainWindow();

            // Wire the Dashboard plugin so completed pomodoros are written to the DB
            _ = Bootstrapper.Resolve<YAPA.Shared.Common.Dashboard>();

            // Eagerly resolve sound notification plugins to wire engine events
            _ = Bootstrapper.Resolve<AvaloniaSoundNotifications>();
            _ = Bootstrapper.Resolve<AvaloniaBackgroundMusic>();

            // Tray icon
            _ = Bootstrapper.Resolve<SystemTrayService>();

            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Bootstrapper?.Resolve<SnapshotService>().SaveSnapshot();
        Bootstrapper?.Resolve<SystemTrayService>().Dispose();
        Bootstrapper?.Dispose();
    }

    internal static void HandleExternalCommandLine(string[] args)
        => ExternalCommandLine?.Invoke(args);
}
