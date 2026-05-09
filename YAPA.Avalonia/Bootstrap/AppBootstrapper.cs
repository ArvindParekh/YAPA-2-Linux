using System;
using Microsoft.Extensions.DependencyInjection;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Bootstrap;

/// <summary>
/// Replaces the WPF DependencyContainer. All registrations happen before the
/// ServiceProvider is built, so ContainerBuilder.Update() is never needed.
///
/// ThemeManager and PluginManager (Steps 4-5) will call Register() / RegisterInstance()
/// via IDependencyInjector after the initial build; MediDependencyInjector handles
/// those calls by snapshotting existing singletons and rebuilding.
/// </summary>
public class AppBootstrapper : IDisposable
{
    private readonly MediDependencyInjector _di;
    private bool _disposed;

    public IDependencyInjector DependencyInjector => _di;

    public T Resolve<T>() where T : class
        => (T)_di.Resolve(typeof(T));

    public AppBootstrapper()
    {
        var services = new ServiceCollection();

        // ── Infrastructure ───────────────────────────────────────────────────────
        services.AddSingleton<IJson, AvaloniaJson>();
        services.AddSingleton<IEnvironment, CrossPlatformEnvironment>();
        services.AddSingleton<ISettings, JsonYapaSettings>();
        services.AddSingleton<IThreading, AvaloniaThreading>();
        services.AddSingleton<ISettingManager, AvaloniaSettingManager>();

        // ── Timer / Date ─────────────────────────────────────────────────────────
        // Transient mirrors WPF registration; singletons that hold them keep the
        // same instance for their lifetime (effectively singleton in practice).
        services.AddTransient<ITimer, AvaloniaTimer>();
        services.AddTransient<IDate, DateTimeWrapper>();

        // ── Pomodoro engine ──────────────────────────────────────────────────────
        // PomodoroEngineSettings is transient, matching the WPF registration.
        services.AddTransient<PomodoroEngineSettings>();
        services.AddSingleton<IPomodoroEngine, PomodoroEngine>();

        // ── Repository ───────────────────────────────────────────────────────────
        services.AddSingleton<IPomodoroRepository, SqlitePomodoroRepository>();

        // ── Dashboard plugin ─────────────────────────────────────────────────────
        // Subscribes to engine.OnPomodoroCompleted and writes each completed
        // pomodoro to the repository so CompletedToday / dashboard data is correct.
        services.AddSingleton<Dashboard>();

        // ── Snapshot ─────────────────────────────────────────────────────────────
        services.AddSingleton<SnapshotService>();

        // ── Theme / sound settings ───────────────────────────────────────────────
        services.AddSingleton<AvaloniaYapaThemeSettings>();
        services.AddSingleton<AvaloniaThemeSettings>();
        services.AddSingleton<AvaloniaSoundNotificationsSettings>();
        services.AddSingleton<AvaloniaMusicPlayerSettings>();

        // ── Audio player + sound plugins ─────────────────────────────────────────
        services.AddSingleton<IMusicPlayer, ProcessBasedMusicPlayer>();
        services.AddSingleton<AvaloniaSoundNotifications>();
        services.AddSingleton<AvaloniaBackgroundMusic>();

        // ── Tray ─────────────────────────────────────────────────────────────────
        services.AddSingleton<SystemTrayService>();

        // ── Commands / ViewModel ─────────────────────────────────────────────────
        services.AddSingleton<IShowSettingsCommand, ShowSettingsCommand>();
        services.AddTransient<IMainViewModel, MainViewModel>();

        // ── DI self-reference ────────────────────────────────────────────────────
        // Must be added to the collection before Build() so it can be resolved.
        _di = new MediDependencyInjector(services);
        services.AddSingleton<IDependencyInjector>(_di);

        _di.Build();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _di.Dispose();
    }
}
