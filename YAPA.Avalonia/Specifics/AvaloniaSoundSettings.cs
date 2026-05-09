using System;
using System.IO;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public sealed class AvaloniaSoundNotificationsSettings : IPluginSettings
{
    private static readonly string DefaultTickPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "tick.wav");

    private static readonly string DefaultDingPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "ding.wav");

    private readonly ISettingsForComponent _s;

    public AvaloniaSoundNotificationsSettings(ISettings settings)
        => _s = settings.GetSettingsForComponent("SoundNotifications");

    public string PeriodStartSound
    {
        get => _s.Get(nameof(PeriodStartSound), DefaultTickPath);
        set => _s.Update(nameof(PeriodStartSound), value);
    }

    public string PeriodEndSound
    {
        get => _s.Get(nameof(PeriodEndSound), DefaultDingPath);
        set => _s.Update(nameof(PeriodEndSound), value);
    }

    public void DeferChanges() => _s.DeferChanges();

    public void ResetToDefaults()
    {
        PeriodStartSound = DefaultTickPath;
        PeriodEndSound   = DefaultDingPath;
    }
}

public sealed class AvaloniaMusicPlayerSettings : IPluginSettings
{
    private readonly ISettingsForComponent _s;

    public AvaloniaMusicPlayerSettings(ISettings settings)
        => _s = settings.GetSettingsForComponent("MusicPlayer");

    public string? WorkSong
    {
        get => _s.Get<string?>(nameof(WorkSong), null, local: true);
        set => _s.Update(nameof(WorkSong), value, local: true);
    }

    public bool RepeatWorkSong
    {
        get => _s.Get(nameof(RepeatWorkSong), false, local: true);
        set => _s.Update(nameof(RepeatWorkSong), value, local: true);
    }

    public string? BreakSong
    {
        get => _s.Get<string?>(nameof(BreakSong), null, local: true);
        set => _s.Update(nameof(BreakSong), value, local: true);
    }

    public bool RepeatBreakSong
    {
        get => _s.Get(nameof(RepeatBreakSong), false, local: true);
        set => _s.Update(nameof(RepeatBreakSong), value, local: true);
    }

    public string? SessionBreakSong
    {
        get => _s.Get<string?>(nameof(SessionBreakSong), null, local: true);
        set => _s.Update(nameof(SessionBreakSong), value, local: true);
    }

    public bool RepeatSessionBreakSong
    {
        get => _s.Get(nameof(RepeatSessionBreakSong), false, local: true);
        set => _s.Update(nameof(RepeatSessionBreakSong), value, local: true);
    }

    public void DeferChanges() => _s.DeferChanges();

    public void ResetToDefaults()
    {
        WorkSong               = null;
        RepeatWorkSong         = false;
        BreakSong              = null;
        RepeatBreakSong        = false;
        SessionBreakSong       = null;
        RepeatSessionBreakSong = false;
    }
}
