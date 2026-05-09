using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using YAPA.Avalonia.Bootstrap;
using YAPA.Avalonia.Persistence;

namespace YAPA.Avalonia;

public partial class App : Application
{
    public static AppBootstrapper? Bootstrapper { get; private set; }

    // Raised on the UI thread when a second instance sends command-line arguments.
    // The main window subscribes to this in Step 4.
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
            desktop.MainWindow = new MainWindow();

            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Bootstrapper?.Resolve<SnapshotService>().SaveSnapshot();
        Bootstrapper?.Dispose();
    }

    internal static void HandleExternalCommandLine(string[] args)
        => ExternalCommandLine?.Invoke(args);
}
