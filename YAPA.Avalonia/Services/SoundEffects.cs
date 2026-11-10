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

    private readonly ISettings _settings;

    public SoundEffects(IPomodoroEngine engine, PomodoroEngineSettings engineSettings, SoundSettings sounds, AvaloniaMusicPlayerSettings music,
        ISettings settings)
        : this(engine, engineSettings, sounds, music, settings, AudioFactory.CreateCuePlayer(), AudioFactory.CreateMusicPlayer())
    {
    }

    internal SoundEffects(IPomodoroEngine engine, PomodoroEngineSettings engineSettings, SoundSettings sounds, AvaloniaMusicPlayerSettings music,
        ISettings settings, ISoundCuePlayer cues, IBackgroundMusicPlayer musicPlayer)
    {
        _engine = engine;
        _engineSettings = engineSettings;
        _sounds = sounds;
        _music = music;
        _settings = settings;
        Cues = cues;
        Music = musicPlayer;
        _engine.Transitioned += OnTransition;
        _settings.PropertyChanged += OnSettingChanged;
    }

    /// <summary>Settings apply immediately: choosing new music mid-pomodoro plays it now.</summary>
    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && (name.StartsWith("MusicPlayer.", StringComparison.Ordinal)
                || name == "PomodoroEngine.DisableSoundNotifications"))
            UpdateMusic();
    }

    /// <summary>Raised when what the music player is doing may have changed.</summary>
    public event Action? MusicChanged;

    /// <summary>Which phase's music is playing for the timer right now, if any.</summary>
    public MusicSlot? LiveSlot { get; private set; }

    public ISoundCuePlayer Cues { get; }
    public IBackgroundMusicPlayer Music { get; }

    public void Preview(SoundRef sound) => Cues.Play(sound, _engineSettings.Volume);

    private bool _musicPreview;

    /// <summary>Auditions a station or file from the settings page, whatever the timer is doing.</summary>
    public void PreviewMusic(YAPA.Avalonia.Audio.Library.MediaItem item)
    {
        _musicPreview = true;
        _playing = null;
        Music.Play(item.Location, loop: true, _engineSettings.Volume);
        MusicChanged?.Invoke();
    }

    /// <summary>Ends an audition and goes back to what the timer's phase calls for.</summary>
    public void StopMusicPreview()
    {
        if (!_musicPreview) return;
        _musicPreview = false;
        Music.Stop();
        _playing = null;
        UpdateMusic();
    }

    public bool IsPreviewingMusic => _musicPreview;

    private void OnTransition(PomodoroTransition t)
    {
        if (_engineSettings.DisableSoundNotifications)
        {
            UpdateMusic(); // stops it
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

    /// <summary>What the timer last asked the player for, so unrelated changes don't restart it.</summary>
    private (string Location, bool Loop)? _playing;

    private void UpdateMusic()
    {
        var (slot, song, loop) = _engine.Phase switch
        {
            PomodoroPhase.Work => (MusicSlot.Work, _music.WorkSong, _music.RepeatWorkSong),
            PomodoroPhase.Break when _engine.IsLongBreak => (MusicSlot.LongBreak, _music.SessionBreakSong, _music.RepeatSessionBreakSong),
            PomodoroPhase.Break => (MusicSlot.Break, _music.BreakSong, _music.RepeatBreakSong),
            _ => ((MusicSlot?)null, (string?)null, false),
        };
        if (_engineSettings.DisableSoundNotifications) song = null;

        var item = YAPA.Avalonia.Audio.Library.MediaLibrary.Resolve(song);
        LiveSlot = item == null ? null : slot;
        if (_musicPreview) // the settings page is auditioning something
        {
            MusicChanged?.Invoke();
            return;
        }

        // "Loop" for a stream means reconnect if it drops; the player's respawn guard
        // stops it if the station is truly unreachable.
        (string Location, bool Loop)? wanted = item == null ? null
            : (item.Location, item.Kind == YAPA.Avalonia.Audio.Library.MediaKind.Radio || loop);

        // Same thing still playing (say, breaks and the long break share the rain, or
        // another phase's choice changed): let it carry on rather than restart it.
        if (wanted != null && wanted == _playing && Music.IsPlaying)
        {
            MusicChanged?.Invoke();
            return;
        }

        _playing = wanted;
        if (wanted is { } w) Music.Play(w.Location, w.Loop, _engineSettings.Volume);
        else Music.Stop();
        MusicChanged?.Invoke();
    }

    public void Dispose()
    {
        _engine.Transitioned -= OnTransition;
        _settings.PropertyChanged -= OnSettingChanged;
        Music.Dispose();
        Cues.Dispose();
    }
}

/// <summary>The three phases that can have their own music.</summary>
public enum MusicSlot
{
    Work,
    Break,
    LongBreak,
}
