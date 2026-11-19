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

    /// <summary>Record the window in front while a pomodoro runs (X11 only). Nothing during breaks.</summary>
    public bool RecordWindows
    {
        get => _s.Get(nameof(RecordWindows), true);
        set => Set(value);
    }

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
    /// Apps, sites and words never recorded or sent, one per line. A window or page that
    /// matches is kept as time with no title, and a matching prompt is left out.
    /// </summary>
    public string Ignored
    {
        get => _s.Get(nameof(Ignored), DefaultIgnored);
        set => Set(value ?? "");
    }

    public const string DefaultIgnored = "KeePassXC\nBitwarden\n1Password\nPrivate Browsing\nIncognito";

    public IReadOnlyList<string> IgnoredTerms =>
        Ignored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(t => t.Length > 1).ToList();

    public bool IsIgnored(params string[] texts)
    {
        var terms = IgnoredTerms;
        return texts.Any(text => text.Length > 0 && terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    public void ResetToDefaults()
    {
        RecordWindows = true;
        ClaudeCode = true;
        Git = true;
        Browsers = true;
        Summaries = true;
        Ignored = DefaultIgnored;
    }

    public void DeferChanges() => _s.DeferChanges();
}
