using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

/// <summary>
/// The timer's words, each replaceable ("Work" → "Deep work", "START" → "GO"), and a note line
/// of the user's own that can carry live values ("{left} to go · ends {ends}"). Empty means
/// the built-in wording.
/// </summary>
public sealed class TimerTexts : IPluginSettings, INotifyPropertyChanged
{
    /// <summary>Key, what it is, the built-in wording.</summary>
    public static readonly IReadOnlyList<(string Key, string What, string Default)> Labels =
    [
        ("status_ready", "status when idle", "Ready"),
        ("status_work", "status during a pomodoro", "Work"),
        ("status_work_done", "status when a pomodoro has ended", "Work done"),
        ("status_break", "status during a short break", "Break"),
        ("status_long_break", "status during the long break", "Long break"),
        ("status_break_over", "status when a break has ended", "Break over"),
        ("status_paused", "status while a pomodoro is paused", "Paused"),
        ("status_break_paused", "status while a break is paused", "Break paused"),
        ("button_start", "the start button", "START"),
        ("button_break", "the button that starts a break", "BREAK"),
        ("button_resume", "the resume button", "RESUME"),
        ("button_pause", "the pause button", "PAUSE"),
        ("button_stop", "the stop button", "STOP"),
        ("button_skip", "the skip-break button", "SKIP"),
    ];

    /// <summary>What the note can show, filled in live.</summary>
    public static readonly IReadOnlyList<(string Token, string Meaning)> Placeholders =
    [
        ("{done}", "pomodoros done today"),
        ("{goal}", "the daily goal"),
        ("{left}", "pomodoros left to the goal"),
        ("{pomodoro}", "position in the cycle"),
        ("{cycle_length}", "pomodoros per cycle"),
        ("{cycle}", "cycle of the day"),
        ("{ends}", "when the running interval ends (HH:MM)"),
        ("{goal_eta}", "when the goal will be reached (HH:MM)"),
        ("{now}", "the time now (HH:MM)"),
        ("{profile}", "the active profile"),
    ];

    public const int MaxLabel = 24;
    public const int MaxNote = 80;

    private readonly ISettingsForComponent _s;

    public TimerTexts(ISettings settings) => _s = settings.GetSettingsForComponent("Texts");

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The user's wording for <paramref name="key"/>, or "" for the built-in one.</summary>
    public string Get(string key) => _s.Get(key, "") ?? "";

    public void Set(string key, string value)
    {
        if (Labels.All(l => l.Key != key) && key is not (nameof(Note) or nameof(Quote))) throw new ArgumentException($"Unknown text {key}.");
        _s.Update(key, value.Trim());
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key));
    }

    /// <summary>The label to show: the user's, or <paramref name="fallback"/>.</summary>
    public string Or(string key, string fallback) => Get(key) is { Length: > 0 } custom ? custom : fallback;

    /// <summary>A line of the user's own on the timer, with placeholders; empty for none.</summary>
    public string Note
    {
        get => _s.Get(nameof(Note), "") ?? "";
        set => Set(nameof(Note), value);
    }

    /// <summary>For the Motivational theme: one quote of the user's own instead of the rotating ones.</summary>
    public string Quote
    {
        get => _s.Get(nameof(Quote), "") ?? "";
        set => Set(nameof(Quote), value);
    }

    public void ResetToDefaults()
    {
        foreach (var (key, _, _) in Labels) Set(key, "");
        Note = "";
        Quote = "";
    }

    public void DeferChanges() => _s.DeferChanges();
}
