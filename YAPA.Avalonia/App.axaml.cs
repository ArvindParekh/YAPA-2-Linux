using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using YAPA.Avalonia.Bootstrap;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.SingleInstance;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia;

public partial class App : Application
{
    private static readonly TimeSpan RemoteCommandTimeout = TimeSpan.FromSeconds(2);

    public static AppBootstrapper? Bootstrapper { get; internal set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var bs = Bootstrapper = new AppBootstrapper();

        // The timer is hidden to the tray routinely, so "last window closed"
        // must not end the app; AppNavigator.Quit shuts down explicitly.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Effects hang off engine transitions; resolve them before anything can start the timer.
        bs.StartEffects();
        bs.Resolve<IPomodoroEngine>().Transitioned += t =>
            Trace.WriteLine($"[timer] {t} · {bs.Resolve<IPomodoroEngine>().Elapsed}s elapsed, pomodoro {bs.Resolve<IPomodoroEngine>().Index}");

        var navigator = bs.Resolve<AppNavigator>();
        var tray = bs.Resolve<SystemTrayService>();
        var commands = bs.Resolve<AppCommandHandler>();
        var snapshots = bs.Resolve<SnapshotService>();

        var timer = navigator.EnsureTimer();
        timer.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty) tray.SetTimerVisible(timer.IsVisible);
        };
        navigator.Quitting += snapshots.Save;
        navigator.Quitting += tray.Freeze;

        var startup = Program.StartupCommandLine;
        var startHidden = bs.Resolve<GeneralSettings>().StartHidden || startup.Commands.Contains(AppCommand.Hide);
        if (!startHidden) navigator.ShowTimer();

        snapshots.Restore(resume: startup.Commands.Contains(AppCommand.Start));
        // Start after a resumed snapshot is a harmless no-op, so the list can run as is.
        var startupCommands = startup.Commands.Where(c => c is not AppCommand.Hide).ToList();
        if (startupCommands.Count > 0) commands.Execute(startupCommands);

        if (Program.Channel is { } channel)
        {
            channel.RequestHandler = args =>
            {
                var parsed = CommandLine.Parse(args);
                try
                {
                    return Dispatcher.UIThread.InvokeAsync(() => commands.Execute(parsed.Commands))
                        .GetTask().WaitAsync(RemoteCommandTimeout).GetAwaiter().GetResult();
                }
                catch (TimeoutException)
                {
                    return "yapa2: the running instance is busy";
                }
            };
        }

        // Session end (logout, shutdown) and SIGTERM land here; Quit already saved otherwise.
        desktop.ShutdownRequested += (_, _) => snapshots.Save();
        desktop.Exit += (_, _) =>
        {
            Trace.WriteLine("[app] exiting");
            bs.Dispose();
        };

        base.OnFrameworkInitializationCompleted();
    }
}
