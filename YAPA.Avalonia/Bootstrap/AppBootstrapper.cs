using System;
using Microsoft.Extensions.DependencyInjection;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Settings;
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
        services.AddSingleton<AvaloniaMusicPlayerSettings>();

        // ── Timer ────────────────────────────────────────────────────────────
        services.AddSingleton<IPomodoroEngine, PomodoroEngine>();
        services.AddSingleton<IPomodoroRepository, SqlitePomodoroRepository>();
        services.AddSingleton<Dashboard>();
        services.AddSingleton<SnapshotService>();

        // ── Effects of timer transitions ─────────────────────────────────────
        services.AddSingleton<SoundSettings>();
        services.AddSingleton<SoundEffects>();
        services.AddSingleton<YAPA.Avalonia.Audio.Ambience.AmbienceSettings>();
        services.AddSingleton(_ => YAPA.Avalonia.Audio.Ambience.AmbiencePlayer.Create(AudioFactory.CreateMusicPlayer()));
        services.AddSingleton<AmbienceEffects>();
        services.AddSingleton<SoundsPageFactory>();

        // ── UI and shell integration ─────────────────────────────────────────
        services.AddSingleton<AppNavigator>();
        services.AddSingleton<IAppNavigator>(sp => sp.GetRequiredService<AppNavigator>());
        // Its own slow timer: the goal forecast only needs refreshing every few seconds when idle.
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<TimerViewModel>(sp,
            (ITimer)new AvaloniaTimer(longest: TimeSpan.FromSeconds(15))));
        services.AddSingleton<AppCommandHandler>();
        services.AddSingleton<SystemTrayService>();
        services.AddSingleton(_ => YAPA.Avalonia.Platform.Linux.X11.X11PlacementGuard.Create());
        services.AddSingleton(_ => DesktopIntegration.CreateNotifier());
        services.AddSingleton(_ => DesktopIntegration.CreateSessionMonitor());
        services.AddSingleton<NotificationEffects>();
        services.AddSingleton<SessionEffects>();

        // ── Assistant ────────────────────────────────────────────────────────
        services.AddSingleton<YAPA.Avalonia.Assistant.AssistantSettings>();
        services.AddSingleton<YAPA.Avalonia.Assistant.SettingsCatalog>();
        services.AddSingleton<YAPA.Avalonia.Assistant.ChangeJournal>();
        services.AddSingleton<YAPA.Avalonia.Assistant.AppTools>();
        services.AddSingleton<YAPA.Avalonia.Assistant.AssistantService>();
        services.AddSingleton<YAPA.Avalonia.Assistant.AssistantViewModel>();

        _provider = services.BuildServiceProvider();
    }

    /// <summary>Instantiates the services that only listen to the engine, so they're wired before the first transition.</summary>
    public void StartEffects()
    {
        Resolve<Dashboard>();
        Resolve<SoundEffects>();
        Resolve<AmbienceEffects>();
        Resolve<NotificationEffects>();
        Resolve<SessionEffects>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _provider.Dispose();
    }
}
