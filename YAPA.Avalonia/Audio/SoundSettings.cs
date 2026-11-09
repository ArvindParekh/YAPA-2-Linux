using System.Collections.Generic;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Audio;

public enum SoundCue
{
    Start,
    Pause,
    Resume,
    Stop,
    WorkCompleted,
    BreakCompleted,
}

/// <summary>Which sound plays for each timer event. Volume and mute live in the engine settings, where they always were.</summary>
public sealed class SoundSettings
{
    private readonly ISettingsForComponent _s;
    private readonly ISettingsForComponent _legacy;

    public SoundSettings(ISettings settings)
    {
        _s = settings.GetSettingsForComponent("Sounds");
        // The original app only had "period start" and "period end" sounds.
        _legacy = settings.GetSettingsForComponent("SoundNotifications");
    }

    public static IReadOnlyList<SoundCue> All { get; } =
        [SoundCue.Start, SoundCue.Pause, SoundCue.Resume, SoundCue.Stop, SoundCue.WorkCompleted, SoundCue.BreakCompleted];

    public SoundRef Get(SoundCue cue)
    {
        var stored = _s.Get<string?>(cue.ToString(), null);
        if (stored != null) return SoundRef.Parse(stored);

        // A custom file chosen in an older version carries over; the old default
        // paths don't, because the new built-ins replace those defaults on purpose
        // (the original tick peaks at -27 dBFS and was barely audible).
        var legacyKey = cue switch
        {
            SoundCue.Start or SoundCue.Resume => "PeriodStartSound",
            SoundCue.WorkCompleted or SoundCue.BreakCompleted => "PeriodEndSound",
            _ => null,
        };
        if (legacyKey != null)
        {
            var legacy = SoundRef.Parse(_legacy.Get<string?>(legacyKey, null));
            if (legacy.Kind == SoundRefKind.File) return legacy;
        }

        return Default(cue);
    }

    public void Set(SoundCue cue, SoundRef sound) => _s.Update(cue.ToString(), sound.Serialize());

    public void ResetToDefaults()
    {
        foreach (var cue in All) Set(cue, Default(cue));
    }

    public static SoundRef Default(SoundCue cue) => cue switch
    {
        SoundCue.Start => DefaultCueSounds.Start,
        SoundCue.Pause => DefaultCueSounds.Pause,
        SoundCue.Resume => DefaultCueSounds.Resume,
        SoundCue.Stop => DefaultCueSounds.Stop,
        SoundCue.WorkCompleted => DefaultCueSounds.WorkCompleted,
        _ => DefaultCueSounds.BreakCompleted,
    };

    public static string Label(SoundCue cue) => cue switch
    {
        SoundCue.Start => "Pomodoro or break starts",
        SoundCue.Pause => "Paused",
        SoundCue.Resume => "Resumed",
        SoundCue.Stop => "Stopped",
        SoundCue.WorkCompleted => "Pomodoro complete",
        _ => "Break over",
    };
}
