using System;
using Microsoft.Extensions.DependencyInjection;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.ViewModels;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Bootstrap;

/// <summary>Composition root. Everything is registered before the provider is built.</summary>
public sealed class AppBootstrapper : IDisposable
{
    private readonly ServiceProvider _provider;
    private bool _disposed;

    public T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();

    public AppBootstrapper()
    {
        var services = new ServiceCollection();

        // ── Infrastructure ───────────────────────────────────────────────────
        services.AddSingleton<IJson, NewtonsoftJson>();
        services.AddSingleton<IEnvironment, CrossPlatformEnvironment>();
        services.AddSingleton<ISettings, JsonYapaSettings>();
        services.AddSingleton<IThreading, AvaloniaThreading>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddTransient<ITimer, AvaloniaTimer>();

        // ── Settings ─────────────────────────────────────────────────────────
        services.AddSingleton<PomodoroEngineSettings>();
        services.AddSingleton<AvaloniaYapaThemeSettings>();
        services.AddSingleton<AvaloniaThemeSettings>();
        services.AddSingleton<GeneralSettings>();
        services.AddSingleton<AvaloniaSoundNotificationsSettings>();
        services.AddSingleton<AvaloniaMusicPlayerSettings>();

        // ── Timer ────────────────────────────────────────────────────────────
        services.AddSingleton<IPomodoroEngine, PomodoroEngine>();
        services.AddSingleton<IPomodoroRepository, SqlitePomodoroRepository>();
        services.AddSingleton<Dashboard>();
        services.AddSingleton<SnapshotService>();

        // ── Effects of timer transitions ─────────────────────────────────────
        // Notifications and background music must not share a player: music
        // stops its player on every transition, which would cut off the cue.
        services.AddSingleton(sp => new AvaloniaSoundNotifications(
            sp.GetRequiredService<IPomodoroEngine>(),
            sp.GetRequiredService<AvaloniaSoundNotificationsSettings>(),
            new ProcessBasedMusicPlayer(),
            sp.GetRequiredService<PomodoroEngineSettings>()));
        services.AddSingleton(sp => new AvaloniaBackgroundMusic(
            sp.GetRequiredService<IPomodoroEngine>(),
            sp.GetRequiredService<AvaloniaMusicPlayerSettings>(),
            new ProcessBasedMusicPlayer(),
            sp.GetRequiredService<PomodoroEngineSettings>()));

        // ── UI and shell integration ─────────────────────────────────────────
        services.AddSingleton<AppNavigator>();
        services.AddSingleton<IAppNavigator>(sp => sp.GetRequiredService<AppNavigator>());
        services.AddSingleton<TimerViewModel>();
        services.AddSingleton<AppCommandHandler>();
        services.AddSingleton<SystemTrayService>();
        services.AddSingleton<IWindowPlacementGuard, NullPlacementGuard>();

        _provider = services.BuildServiceProvider();
    }

    /// <summary>Instantiates the services that only listen to the engine, so they're wired before the first transition.</summary>
    public void StartEffects()
    {
        Resolve<Dashboard>();
        Resolve<AvaloniaSoundNotifications>();
        Resolve<AvaloniaBackgroundMusic>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _provider.Dispose();
    }
}
