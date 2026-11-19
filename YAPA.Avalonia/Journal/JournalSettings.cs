using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Journal;

/// <summary>What the day journal may look at, and whether Claude writes it up.</summary>
public sealed class JournalSettings : IPluginSettings, INotifyPropertyChanged
{
    private readonly ISettingsForComponent _s;

    public JournalSettings(ISettings settings) => _s = settings.GetSettingsForComponent("Journal");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(object value, [CallerMemberName] string name = "")
    {
        _s.Update(name, value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Record the window in front while the user is at the computer (X11 only).</summary>
    public bool RecordWindows
    {
        get => _s.Get(nameof(RecordWindows), true);
        set => Set(value);
    }

    /// <summary>
    /// The whole day, not just pomodoros: windows are recorded whenever the user is at the
    /// computer, and the Day window shows breaks and the time between sessions.
    /// </summary>
    public bool WholeDay
    {
        get => _s.Get(nameof(WholeDay), true);
        set => Set(value);
    }

    /// <summary>
    /// Apps that stop the journal entirely while they run, one per line ("Tor Browser"):
    /// nothing is recorded, and nothing any other source found in that time is used.
    /// </summary>
    public string PauseFor
    {
        get => _s.Get(nameof(PauseFor), DefaultPauseFor);
        set => Set(value ?? "");
    }

    public const string DefaultPauseFor = "Tor Browser";

    public IReadOnlyList<string> PauseApps => Lines(PauseFor);

    /// <summary>Read Claude Code conversations: titles, folders and what the user typed.</summary>
    public bool ClaudeCode
    {
        get => _s.Get(nameof(ClaudeCode), true);
        set => Set(value);
    }

    public bool Git
    {
        get => _s.Get(nameof(Git), true);
        set => Set(value);
    }

    public bool Browsers
    {
        get => _s.Get(nameof(Browsers), true);
        set => Set(value);
    }

    /// <summary>Have Claude write each pomodoro, session and day up; otherwise titles come from the evidence alone.</summary>
    public bool Summaries
    {
        get => _s.Get(nameof(Summaries), true);
        set => Set(value);
    }

    /// <summary>
    /// Apps, sites and words never recorded or sent, one per line. Anything that matches (a
    /// window, a page, a conversation, a prompt, a commit) is skipped as if it never happened.
    /// </summary>
    public string Ignored
    {
        get => _s.Get(nameof(Ignored), DefaultIgnored);
        set => Set(value ?? "");
    }

    /// <summary>
    /// Adult sites and titles (<see cref="AdultSites"/>) are never recorded or sent: the time an
    /// adult window is in front is off the record, and adult pages are left out of the history.
    /// </summary>
    public bool SkipAdult
    {
        get => _s.Get(nameof(SkipAdult), true);
        set => Set(value);
    }

    public const string DefaultIgnored = "Telegram\nKeePassXC\nBitwarden\n1Password\nPrivate Browsing\nIncognito";

    public IReadOnlyList<string> IgnoredTerms => Lines(Ignored);

    private static IReadOnlyList<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(t => t.Length > 1).ToList();

    public bool IsIgnored(params string[] texts)
    {
        var terms = IgnoredTerms;
        return texts.Any(text => text.Length > 0 && terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    public void ResetToDefaults()
    {
        RecordWindows = true;
        WholeDay = true;
        PauseFor = DefaultPauseFor;
        ClaudeCode = true;
        Git = true;
        Browsers = true;
        Summaries = true;
        Ignored = DefaultIgnored;
        SkipAdult = true;
    }

    public void DeferChanges() => _s.DeferChanges();
}
