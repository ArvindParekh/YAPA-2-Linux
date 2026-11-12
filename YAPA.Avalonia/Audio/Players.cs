using System;

namespace YAPA.Avalonia.Audio;

/// <summary>Short notification sounds (period start/end, pause, resume, stop).</summary>
public interface ISoundCuePlayer : IDisposable
{
    /// <summary>
    /// Non-blocking, never throws. A new cue interrupts any cue still playing.
    /// <paramref name="volume"/> is 0..1 (the UI slider value); it is mapped through
    /// <see cref="VolumeCurve"/>.
    /// </summary>
    void Play(SoundRef sound, double volume);

    void Stop();

    /// <summary>Human-readable backend name for the About/diagnostics UI, e.g. "PulseAudio (in-process)" or "pw-play".</summary>
    string BackendDescription { get; }
}

/// <summary>Per-phase background music. Independent of cues, so a cue never stops the music and vice versa.</summary>
public interface IBackgroundMusicPlayer : IDisposable
{
    /// <summary>Non-blocking, never throws. Replaces whatever is playing.</summary>
    void Play(string path, bool loop, double volume);

    void Stop();

    bool IsPlaying { get; }
}

/// <summary>
/// Suggested cue for each timer event. These are defaults for the settings UI, nothing more:
/// the user can pick any built-in or file for each event.
///
/// The pairs are chosen so the meaning is audible without looking: rising for "go"
/// (start/resume), falling for "done" (stop), a single short tap for the low-stakes pause,
/// and the two bells for the moments worth noticing from across the room.
/// </summary>
public static class DefaultCueSounds
{
    public static SoundRef Start { get; } = SoundRef.BuiltIn(BuiltInSound.ChimeUp);
    public static SoundRef Pause { get; } = SoundRef.BuiltIn(BuiltInSound.SoftTap);

    // Same as Start on purpose: resuming is "going again", and a separate resume sound
    // would be one more sound for the user to learn.
    public static SoundRef Resume { get; } = SoundRef.BuiltIn(BuiltInSound.ChimeUp);
    public static SoundRef Stop { get; } = SoundRef.BuiltIn(BuiltInSound.ChimeDown);
    public static SoundRef WorkCompleted { get; } = SoundRef.BuiltIn(BuiltInSound.Bell);
    public static SoundRef BreakCompleted { get; } = SoundRef.BuiltIn(BuiltInSound.BrightBell);

    // The original YAPA ding: unlike the interval cues, so a reminder never reads as "time's up".
    public static SoundRef Reminder { get; } = SoundRef.BuiltIn(BuiltInSound.ClassicDing);
}

internal sealed class NullCuePlayer : ISoundCuePlayer
{
    public NullCuePlayer(string reason) => BackendDescription = $"none ({reason})";

    public string BackendDescription { get; }
    public void Play(SoundRef sound, double volume) { }
    public void Stop() { }
    public void Dispose() { }
}

internal sealed class NullMusicPlayer : IBackgroundMusicPlayer
{
    public bool IsPlaying => false;
    public void Play(string path, bool loop, double volume) { }
    public void Stop() { }
    public void Dispose() { }
}
