using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Avalonia;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.SingleInstance;

namespace YAPA.Avalonia;

internal static class Program
{
    public static InstanceChannel? Channel { get; private set; }
    public static ParsedCommandLine StartupCommandLine { get; private set; } = CommandLine.Parse([]);

    // No Avalonia or SynchronizationContext-dependent code before AppMain.
    [STAThread]
    public static int Main(string[] args)
    {
        var parsed = CommandLine.Parse(args);
        if (parsed.Help)
        {
            Console.WriteLine(CommandLine.Usage);
            return 0;
        }
        if (parsed.Version)
        {
            Console.WriteLine($"YAPA 2 {Version}");
            return 0;
        }
        foreach (var unknown in parsed.Unknown)
            Console.Error.WriteLine($"yapa2: ignoring unknown argument '{unknown}' (see --help)");

        var channel = InstanceChannel.TryBecomePrimary();
        if (channel == null)
        {
            // Another instance owns the timer; hand it our request. With no
            // arguments, a second launch (e.g. from the app grid) means "show me".
            var reply = InstanceChannel.Send(args.Length == 0 ? ["--show"] : args);
            if (reply == null)
            {
                Console.Error.WriteLine("yapa2: an instance is running but did not respond.");
                return 1;
            }
            if (reply.Length > 0) Console.WriteLine(reply);
            return 0;
        }

        if (parsed.IsQueryOnly)
        {
            channel.Dispose();
            Console.WriteLine(parsed.Commands.Contains(AppCommand.StatusJson) ? "{\"running\":false}" : "YAPA 2 is not running");
            return parsed.Commands.Contains(AppCommand.Quit) ? 0 : 3;
        }

        // Only the primary instance logs: CLI clients like `--status` are short-lived
        // and would otherwise interleave writes into the same file.
        Diagnostics.Logging.Initialize();
        Channel = channel;
        StartupCommandLine = parsed;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, global::Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            channel.Dispose();
        }
    }

    public static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "dev";

    // Avalonia configuration; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                // The app name GNOME shows for the tray icon and notifications.
                WmClass = "yapa2",
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Geist for everything the app writes; timer windows pin Inter for their own look.
            .With(new global::Avalonia.Media.FontManagerOptions { DefaultFamilyName = App.UiFontUri })
            .LogToTrace();
}
