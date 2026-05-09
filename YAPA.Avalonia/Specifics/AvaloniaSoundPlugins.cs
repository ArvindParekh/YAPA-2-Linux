using System.ComponentModel;
using System.IO;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

/// <summary>
/// Plays tick/ding sounds when the pomodoro phase changes.
/// Mirrors YAPA.WPF SoundNotifications.
/// </summary>
public sealed class AvaloniaSoundNotifications
{
    private readonly IPomodoroEngine _engine;
    private readonly AvaloniaSoundNotificationsSettings _settings;
    private readonly IMusicPlayer _player;
    private readonly PomodoroEngineSettings _engineSettings;

    public AvaloniaSoundNotifications(
        IPomodoroEngine engine,
        AvaloniaSoundNotificationsSettings settings,
        IMusicPlayer player,
        PomodoroEngineSettings engineSettings)
    {
        _engine = engine;
        _settings = settings;
        _player = player;
        _engineSettings = engineSettings;

        engine.PropertyChanged += OnEnginePropertyChanged;
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_engine.Phase)) return;
        if (_engineSettings.DisableSoundNotifications) return;

        switch (_engine.Phase)
        {
            case PomodoroPhase.Work:
            case PomodoroPhase.Break:
                PlayPath(_settings.PeriodStartSound);
                break;
            case PomodoroPhase.WorkEnded:
            case PomodoroPhase.BreakEnded:
                PlayPath(_settings.PeriodEndSound);
                break;
        }
    }

    private void PlayPath(string path)
    {
        if (!File.Exists(path)) return;
        _player.Stop();
        _player.Load(path);
        _player.Play(volume: _engineSettings.Volume);
    }
}

/// <summary>
/// Plays per-phase background music songs.
/// Mirrors YAPA.WPF MusicPlayer plugin.
/// </summary>
public sealed class AvaloniaBackgroundMusic
{
    private readonly IPomodoroEngine _engine;
    private readonly AvaloniaMusicPlayerSettings _settings;
    private readonly IMusicPlayer _player;
    private readonly PomodoroEngineSettings _engineSettings;

    public AvaloniaBackgroundMusic(
        IPomodoroEngine engine,
        AvaloniaMusicPlayerSettings settings,
        IMusicPlayer player,
        PomodoroEngineSettings engineSettings)
    {
        _engine = engine;
        _settings = settings;
        _player = player;
        _engineSettings = engineSettings;

        engine.PropertyChanged += OnEnginePropertyChanged;
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_engine.Phase)) return;
        if (_engineSettings.DisableSoundNotifications) return;

        _player.Stop();

        string? song = null;
        bool repeat = false;

        switch (_engine.Phase)
        {
            case PomodoroPhase.Work:
                song = _settings.WorkSong;
                repeat = _settings.RepeatWorkSong;
                break;
            case PomodoroPhase.Break:
                if (_engine.Index == _engineSettings.PomodorosBeforeLongBreak)
                {
                    song = _settings.SessionBreakSong;
                    repeat = _settings.RepeatSessionBreakSong;
                }
                else
                {
                    song = _settings.BreakSong;
                    repeat = _settings.RepeatBreakSong;
                }
                break;
        }

        if (!string.IsNullOrEmpty(song) && File.Exists(song))
        {
            _player.Load(song);
            _player.Play(repeat, _engineSettings.Volume);
        }
    }
}
