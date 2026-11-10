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
    private bool _previewing;

    public AmbienceEffects(IPomodoroEngine engine, AmbienceSettings settings, PomodoroEngineSettings engineSettings,
        IAmbiencePlayer player, ISettings globalSettings)
    {
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
        if (e.PropertyName == "PomodoroEngine.DisableSoundNotifications") Apply(restartTicks: false);
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
        _player.Play(_settings.Mix(inBreak: false), restartTicks: true);
    }

    public void StopPreview()
    {
        _previewing = false;
        Apply(restartTicks: false);
    }

    private void OnTransition(PomodoroTransition t)
    {
        // A new or resumed work interval restarts the beat, so ticks fall in step with the display.
        var restart = t.Kind is PomodoroTransitionKind.WorkStarted or PomodoroTransitionKind.Resumed;
        Apply(restart);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Apply(restartTicks: false);

    private void Apply(bool restartTicks)
    {
        if (_previewing && !_engineSettings.DisableSoundNotifications)
        {
            _player.Play(_settings.Mix(inBreak: false), restartTicks);
            return;
        }

        if (ShouldPlay) _player.Play(_settings.Mix(inBreak: _engine.Phase == PomodoroPhase.Break), restartTicks);
        else _player.Stop();
    }

    public void Dispose()
    {
        _engine.Transitioned -= OnTransition;
        _settings.PropertyChanged -= OnSettingsChanged;
        _globalSettings.PropertyChanged -= OnGlobalSettingsChanged;
        _player.Stop();
        _player.Dispose();
    }
}
