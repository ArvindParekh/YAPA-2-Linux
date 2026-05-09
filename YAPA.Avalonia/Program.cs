using System;
using Avalonia;
using Avalonia.Threading;
using YAPA.Avalonia.SingleInstance;

namespace YAPA.Avalonia;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        using var sim = new SingleInstanceManager();

        if (!sim.Acquire())
        {
            // Forward args to the already-running instance and exit cleanly.
            SingleInstanceManager.SendCommandLineToFirst(args);
            return 0;
        }

        // Marshal incoming args from subsequent instances onto the UI thread.
        sim.ArgsReceived += receivedArgs =>
        {
            Dispatcher.UIThread.Post(() => App.HandleExternalCommandLine(receivedArgs));
        };

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
