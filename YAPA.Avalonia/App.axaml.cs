using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using YAPA.Avalonia.Bootstrap;

namespace YAPA.Avalonia;

public partial class App : Application
{
    public static AppBootstrapper? Bootstrapper { get; private set; }

    // Raised on the UI thread when a second instance sends command-line arguments.
    // The active main window subscribes to this during Step 4.
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
            desktop.Exit += (_, _) => Bootstrapper?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static void HandleExternalCommandLine(string[] args)
        => ExternalCommandLine?.Invoke(args);
}
