using System;
using Microsoft.Extensions.DependencyInjection;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Bootstrap;

/// <summary>
/// Composition root. Every service is registered up front; nothing registers
/// after the provider is built.
/// </summary>
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
        services.AddTransient<ITimer, AvaloniaTimer>();
        services.AddTransient<IDate, DateTimeWrapper>();

        // ── Pomodoro engine ──────────────────────────────────────────────────
        services.AddTransient<PomodoroEngineSettings>();
        services.AddSingleton<IPomodoroEngine, PomodoroEngine>();
        services.AddSingleton<IPomodoroRepository, SqlitePomodoroRepository>();
        services.AddSingleton<Dashboard>();
        services.AddSingleton<SnapshotService>();

        // ── Theme / sound settings ───────────────────────────────────────────
        services.AddSingleton<AvaloniaYapaThemeSettings>();
        services.AddSingleton<AvaloniaThemeSettings>();
        services.AddSingleton<AvaloniaSoundNotificationsSettings>();
        services.AddSingleton<AvaloniaMusicPlayerSettings>();

        // ── Audio ────────────────────────────────────────────────────────────
        // Notifications and background music must not share a player: music
        // stops its player on every phase change, which would cut off the cue.
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

        // ── Shell integration ────────────────────────────────────────────────
        services.AddSingleton<SystemTrayService>();
        services.AddSingleton<IShowSettingsCommand, ShowSettingsCommand>();
        services.AddSingleton<IMainViewModel, MainViewModel>();

        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _provider.Dispose();
    }
}
