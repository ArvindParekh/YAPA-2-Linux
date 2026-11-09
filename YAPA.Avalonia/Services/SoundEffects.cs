using System;
using System.IO;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Services;

/// <summary>
/// Sound for timer transitions: a short cue per event, and optional background
/// music per phase. Cues and music use separate players. When they shared one,
/// the music side's Stop on every phase change silenced every cue.
/// </summary>
public sealed class SoundEffects : IDisposable
{
    private readonly IPomodoroEngine _engine;
    private readonly PomodoroEngineSettings _engineSettings;
    private readonly SoundSettings _sounds;
    private readonly AvaloniaMusicPlayerSettings _music;

    public SoundEffects(IPomodoroEngine engine, PomodoroEngineSettings engineSettings, SoundSettings sounds, AvaloniaMusicPlayerSettings music)
    {
        _engine = engine;
        _engineSettings = engineSettings;
        _sounds = sounds;
        _music = music;
        Cues = AudioFactory.CreateCuePlayer();
        Music = AudioFactory.CreateMusicPlayer();
        _engine.Transitioned += OnTransition;
    }

    public ISoundCuePlayer Cues { get; }
    public IBackgroundMusicPlayer Music { get; }

    public void Preview(SoundRef sound) => Cues.Play(sound, _engineSettings.Volume);

    private void OnTransition(PomodoroTransition t)
    {
        if (_engineSettings.DisableSoundNotifications)
        {
            Music.Stop();
            return;
        }

        SoundCue? cue = t.Kind switch
        {
            // Automatic starts follow a completion cue that just played; don't talk over it.
            PomodoroTransitionKind.WorkStarted or PomodoroTransitionKind.BreakStarted when !t.Automatic => SoundCue.Start,
            PomodoroTransitionKind.Paused => SoundCue.Pause,
            PomodoroTransitionKind.Resumed => SoundCue.Resume,
            PomodoroTransitionKind.Stopped => SoundCue.Stop,
            PomodoroTransitionKind.WorkCompleted => SoundCue.WorkCompleted,
            PomodoroTransitionKind.BreakCompleted => SoundCue.BreakCompleted,
            _ => null,
        };
        if (cue is { } c) Cues.Play(_sounds.Get(c), _engineSettings.Volume);

        UpdateMusic();
    }

    private void UpdateMusic()
    {
        var (song, loop) = _engine.Phase switch
        {
            PomodoroPhase.Work => (_music.WorkSong, _music.RepeatWorkSong),
            PomodoroPhase.Break when _engine.IsLongBreak => (_music.SessionBreakSong, _music.RepeatSessionBreakSong),
            PomodoroPhase.Break => (_music.BreakSong, _music.RepeatBreakSong),
            _ => ((string?)null, false),
        };

        if (string.IsNullOrEmpty(song) || !File.Exists(song)) Music.Stop();
        else Music.Play(song, loop, _engineSettings.Volume);
    }

    public void Dispose()
    {
        _engine.Transitioned -= OnTransition;
        Music.Dispose();
        Cues.Dispose();
    }
}
