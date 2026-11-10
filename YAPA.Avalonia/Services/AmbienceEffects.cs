using System;
using System.ComponentModel;
using YAPA.Avalonia.Audio.Ambience;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Services;

/// <summary>
/// Plays the ambience while a session runs: it fades in when a pomodoro starts
/// or resumes, and out when it pauses, stops or completes. During breaks it
/// plays only if asked to (the ticking never does). Settings changes apply
/// live to whatever is playing.
///
/// <see cref="Preview"/> lets the settings page audition a sound without
/// starting the timer.
/// </summary>
public sealed class AmbienceEffects : IDisposable
{
    private readonly IPomodoroEngine _engine;
    private readonly AmbienceSettings _settings;
    private readonly PomodoroEngineSettings _engineSettings;
    private readonly IAmbiencePlayer _player;
    private readonly ISettings _globalSettings;
    private readonly IClock _clock;
    private bool _previewing;

    public AmbienceEffects(IPomodoroEngine engine, AmbienceSettings settings, PomodoroEngineSettings engineSettings,
        IAmbiencePlayer player, ISettings globalSettings, IClock clock)
    {
        _clock = clock;
        _engine = engine;
        _settings = settings;
        _engineSettings = engineSettings;
        _player = player;
        _globalSettings = globalSettings;
        _engine.Transitioned += OnTransition;
        _settings.PropertyChanged += OnSettingsChanged;
        _globalSettings.PropertyChanged += OnGlobalSettingsChanged;
    }

    /// <summary>The master "Play sounds" switch lives in the engine settings.</summary>
    private void OnGlobalSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "PomodoroEngine.DisableSoundNotifications") Apply(retime: false);
    }

    /// <summary>True while ambience should be heard for the current timer phase.</summary>
    public bool ShouldPlay
    {
        get
        {
            if (!_settings.Enabled || !_settings.HasAnything || _engineSettings.DisableSoundNotifications) return false;
            return _engine.Phase switch
            {
                PomodoroPhase.Work => true,
                PomodoroPhase.Break => _settings.DuringBreaks && _settings.Sound != AmbientSound.None,
                _ => false,
            };
        }
    }

    public bool IsPreviewing => _previewing;

    /// <summary>Plays the current settings now, timer or not, until <see cref="StopPreview"/>.</summary>
    public void Preview()
    {
        _previewing = true;
        // No timer to follow: tick on a beat that starts now.
        _player.Play(_settings.Mix(inBreak: false), new TickAnchor(_clock.Monotonic + TimeSpan.FromMilliseconds(400), 0));
    }

    public void StopPreview()
    {
        _previewing = false;
        Apply(retime: true);
    }

    private void OnTransition(PomodoroTransition t)
    {
        // Every transition can move where the seconds fall (a resume continues from a
        // fractional second), so the tick timing is refreshed from the engine each time.
        Apply(retime: true);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Apply(retime: false);

    private void Apply(bool retime)
    {
        if (_previewing && !_engineSettings.DisableSoundNotifications)
        {
            _player.Play(_settings.Mix(inBreak: false), null);
            return;
        }

        if (ShouldPlay) _player.Play(_settings.Mix(inBreak: _engine.Phase == PomodoroPhase.Break), retime ? Anchor() : null);
        else _player.Stop();
    }

    /// <summary>Where the timer's next second falls; ticks are placed on it and every second after.</summary>
    private TickAnchor? Anchor()
        => _engine.NextSecondAt is { } next ? new TickAnchor(next, _engine.Elapsed + 1) : null;

    public void Dispose()
    {
        _engine.Transitioned -= OnTransition;
        _settings.PropertyChanged -= OnSettingsChanged;
        _globalSettings.PropertyChanged -= OnGlobalSettingsChanged;
        _player.Stop();
        _player.Dispose();
    }
}
