using System;
using Microsoft.Extensions.DependencyInjection;
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

        // ── Repository (stub; real SQLite impl added in Step 3) ──────────────────
        services.AddSingleton<IPomodoroRepository, NullPomodoroRepository>();

        // ── Commands / ViewModel ─────────────────────────────────────────────────
        // IShowSettingsCommand is a stub until the settings shell lands in Step 5.
        services.AddTransient<IShowSettingsCommand, StubShowSettingsCommand>();
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
