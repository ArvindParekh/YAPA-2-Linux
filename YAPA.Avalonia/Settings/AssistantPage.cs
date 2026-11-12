using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using YAPA.Avalonia.Assistant;
using YAPA.Avalonia.Services;

namespace YAPA.Avalonia.Settings;

/// <summary>The sidebar icons, one per page: 24-unit stroked paths.</summary>
public static class SettingsIcons
{
    private static readonly Geometry Timer = Geometry.Parse("M 21 12 A 9 9 0 1 1 3 12 A 9 9 0 1 1 21 12 Z M 12 7.5 L 12 12 L 15 14");
    private static readonly Geometry Appearance = Geometry.Parse("M 21 12 A 9 9 0 1 1 3 12 A 9 9 0 1 1 21 12 Z M 12 3 L 12 21 M 12 7 L 17.5 7 M 12 11 L 20.5 11 M 12 15 L 19.5 15");
    private static readonly Geometry Sounds = Geometry.Parse("M 4 9.5 L 7.5 9.5 L 12 5.5 L 12 18.5 L 7.5 14.5 L 4 14.5 Z M 15.5 9 A 4 4 0 0 1 15.5 15 M 18.5 6.5 A 7.5 7.5 0 0 1 18.5 17.5");
    private static readonly Geometry Assistant = Geometry.Parse("M 12 3.5 L 13.7 10.3 L 20.5 12 L 13.7 13.7 L 12 20.5 L 10.3 13.7 L 3.5 12 L 10.3 10.3 Z M 18.5 3.5 L 19.1 5 L 20.5 5.5 L 19.1 6 L 18.5 7.5 L 17.9 6 L 16.5 5.5 L 17.9 5 Z");
    private static readonly Geometry General = Geometry.Parse("M 4 7 L 11 7 M 15 7 L 20 7 M 13 4.5 L 13 9.5 M 4 17 L 7 17 M 11 17 L 20 17 M 9 14.5 L 9 19.5");
    private static readonly Geometry About = Geometry.Parse("M 21 12 A 9 9 0 1 1 3 12 A 9 9 0 1 1 21 12 Z M 12 11 L 12 16.5 M 12 7.6 L 12 7.7");

    public static Geometry For(SettingsPage page) => page switch
    {
        SettingsPage.Timer => Timer,
        SettingsPage.Appearance => Appearance,
        SettingsPage.Sounds => Sounds,
        SettingsPage.Assistant => Assistant,
        SettingsPage.About => About,
        _ => General,
    };
}

public sealed record MemoryRow(string Id, string Text);

public sealed record ScheduledRow(string Id, string Text);

/// <summary>
/// The assistant, in the open: whether it answers to a double-click, which model it uses,
/// and what it keeps (notes, scheduled actions, past conversations), each removable.
/// </summary>
public sealed class AssistantPageViewModel : SettingsPageViewModel
{
    private readonly AssistantSettings _settings;
    private readonly AssistantMemory _memory;
    private readonly Scheduler _scheduler;

    public AssistantPageViewModel(AssistantSettings settings, AssistantMemory memory, Scheduler scheduler)
    {
        _settings = settings;
        _memory = memory;
        _scheduler = scheduler;
        _scheduler.Changed += RefreshScheduled;
        RemoveNoteCommand = new RelayCommand<string>(id => { if (id != null && _memory.Forget(id)) RefreshNotes(); });
        CancelScheduledCommand = new RelayCommand<string>(id => { if (id != null) _scheduler.Remove(id); });
        ClearConversationsCommand = new RelayCommand(() => { _memory.ClearConversations(); Changed(nameof(ConversationsText)); Changed(nameof(HasConversations)); });
        RefreshNotes();
        RefreshScheduled();
    }

    public override string Title => "Assistant";
    public override string Subtitle => "Ask or tell it anything about your timer. Double-click the timer, or press /.";
    public override SettingsPage Page => SettingsPage.Assistant;

    public bool Enabled { get => _settings.Enabled; set { _settings.Enabled = value; Changed(); } }

    public System.Collections.Generic.IReadOnlyList<string> Models { get; } =
        AssistantSettings.Models.Select(m => $"{m.Name} · {m.Short}").ToList();

    public int ModelIndex
    {
        get => AssistantSettings.Models.ToList().FindIndex(m => m.Id == _settings.Model);
        set
        {
            if (value < 0 || value >= AssistantSettings.Models.Count) return;
            _settings.Model = AssistantSettings.Models[value].Id;
            Changed();
            Changed(nameof(ModelDetail));
        }
    }

    public string ModelDetail => AssistantSettings.Models.FirstOrDefault(m => m.Id == _settings.Model)?.Detail ?? "";

    public bool IsInstalled => ClaudeCli.Locate() != null;

    public string Status => IsInstalled
        ? "Runs on your own Claude sign-in through Claude Code, with no API key. It can see and change YAPA, and nothing else on your computer."
        : "Needs Claude Code: install it from claude.com/code and sign in. It then runs on your Claude plan, with no API key.";

    public ObservableCollection<MemoryRow> Notes { get; } = [];
    public bool HasNotes => Notes.Count > 0;

    public ObservableCollection<ScheduledRow> Scheduled { get; } = [];
    public bool HasScheduled => Scheduled.Count > 0;

    public int ConversationCount => _memory.ConversationCount;
    public bool HasConversations => _memory.ConversationCount > 0;
    public string ConversationsText => _memory.ConversationCount switch
    {
        0 => "None yet. Finished conversations are kept here so it can look back.",
        1 => "1 conversation, kept on this computer so it can look back.",
        var n => $"{n} conversations, kept on this computer so it can look back.",
    };

    public IRelayCommand<string> RemoveNoteCommand { get; }
    public IRelayCommand<string> CancelScheduledCommand { get; }
    public IRelayCommand ClearConversationsCommand { get; }

    private void RefreshNotes()
    {
        Notes.Clear();
        foreach (var n in _memory.Notes) Notes.Add(new MemoryRow(n.Id, n.Text));
        Changed(nameof(HasNotes));
    }

    private void RefreshScheduled()
    {
        Scheduled.Clear();
        var now = DateTime.Now;
        foreach (var i in _scheduler.Items) Scheduled.Add(new ScheduledRow(i.Id, i.Describe(now)));
        Changed(nameof(HasScheduled));
    }

    /// <summary>The assistant may have remembered or scheduled something since the page opened.</summary>
    public void Reload()
    {
        RefreshNotes();
        RefreshScheduled();
        Changed(nameof(ConversationsText));
        Changed(nameof(HasConversations));
        Changed(nameof(IsInstalled));
        Changed(nameof(Status));
    }

    public void Detach() => _scheduler.Changed -= RefreshScheduled;

    protected override void ResetToDefaults()
    {
        _settings.ResetToDefaults();
        Changed(nameof(ModelDetail));
    }
}
