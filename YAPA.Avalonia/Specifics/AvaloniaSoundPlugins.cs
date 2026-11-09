using System.IO;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

// Interim adapters over the old process player; replaced by Audio/ integration.
public sealed class AvaloniaSoundNotifications
{
    private readonly AvaloniaSoundNotificationsSettings _settings;
    private readonly IMusicPlayer _player;
    private readonly PomodoroEngineSettings _engineSettings;

    public AvaloniaSoundNotifications(IPomodoroEngine engine, AvaloniaSoundNotificationsSettings settings, IMusicPlayer player, PomodoroEngineSettings engineSettings)
    {
        _settings = settings;
        _player = player;
        _engineSettings = engineSettings;
        engine.Transitioned += OnTransition;
    }

    private void OnTransition(PomodoroTransition t)
    {
        if (_engineSettings.DisableSoundNotifications) return;
        switch (t.Kind)
        {
            case PomodoroTransitionKind.WorkStarted or PomodoroTransitionKind.BreakStarted or PomodoroTransitionKind.Resumed when !t.Automatic:
                Play(_settings.PeriodStartSound);
                break;
            case PomodoroTransitionKind.WorkCompleted or PomodoroTransitionKind.BreakCompleted:
                Play(_settings.PeriodEndSound);
                break;
        }
    }

    private void Play(string path)
    {
        if (!File.Exists(path)) return;
        _player.Stop();
        _player.Load(path);
        _player.Play(volume: _engineSettings.Volume);
    }
}

public sealed class AvaloniaBackgroundMusic
{
    private readonly IPomodoroEngine _engine;
    private readonly AvaloniaMusicPlayerSettings _settings;
    private readonly IMusicPlayer _player;
    private readonly PomodoroEngineSettings _engineSettings;

    public AvaloniaBackgroundMusic(IPomodoroEngine engine, AvaloniaMusicPlayerSettings settings, IMusicPlayer player, PomodoroEngineSettings engineSettings)
    {
        _engine = engine;
        _settings = settings;
        _player = player;
        _engineSettings = engineSettings;
        engine.Transitioned += OnTransition;
    }

    private void OnTransition(PomodoroTransition t)
    {
        _player.Stop();
        if (_engineSettings.DisableSoundNotifications) return;

        (string? song, bool repeat) = _engine.Phase switch
        {
            PomodoroPhase.Work => (_settings.WorkSong, _settings.RepeatWorkSong),
            PomodoroPhase.Break when _engine.IsLongBreak => (_settings.SessionBreakSong, _settings.RepeatSessionBreakSong),
            PomodoroPhase.Break => (_settings.BreakSong, _settings.RepeatBreakSong),
            _ => (null, false),
        };

        if (!string.IsNullOrEmpty(song) && File.Exists(song))
        {
            _player.Load(song);
            _player.Play(repeat, _engineSettings.Volume);
        }
    }
}
