using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAPA.Avalonia.Assistant;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Journal;

namespace YAPA.Avalonia.Journal;

/// <summary>Colours for projects: the same project has the same colour every day.</summary>
public static class ProjectColors
{
    private static readonly Color[] Palette =
    [
        Color.Parse("#FFFF7A66"), Color.Parse("#FF4FD1A5"), Color.Parse("#FF9A8CFF"), Color.Parse("#FFF5BE5B"),
        Color.Parse("#FF62B8F5"), Color.Parse("#FFF27DB8"), Color.Parse("#FFA9D96A"), Color.Parse("#FFD3A97F"),
    ];

    public static readonly Color None = Color.Parse("#FF4A4A55");

    /// <summary>"interview-prep", "Interview prep" and "interview_prep" are one project.</summary>
    public static string Key(string project) => System.Text.RegularExpressions.Regex.Replace(project.ToLowerInvariant(), @"[\s_\-]+", " ").Trim();

    /// <summary>A project's own colour, the same every day.</summary>
    private static int Preferred(string project)
    {
        // FNV-1a: stable across runs, unlike string.GetHashCode.
        var hash = 2166136261u;
        foreach (var c in Key(project)) hash = (hash ^ c) * 16777619u;
        return (int)(hash % (uint)Palette.Length);
    }

    /// <summary>
    /// Colours for one day's projects, biggest first: each gets its own colour unless a bigger
    /// project that day already has it, then the next free one. No two share a colour on a day.
    /// </summary>
    public static Func<string?, Color> ForDay(IEnumerable<string> projectsBySize)
    {
        var taken = new Dictionary<string, Color>();
        var used = new HashSet<int>();
        foreach (var project in projectsBySize)
        {
            var key = Key(project);
            if (taken.ContainsKey(key)) continue;
            var index = Preferred(project);
            for (var probe = 0; probe < Palette.Length && used.Contains(index); probe++) index = (index + 1) % Palette.Length;
            used.Add(index);
            taken[key] = Palette[index];
        }
        return project => string.IsNullOrEmpty(project) ? None : taken.TryGetValue(Key(project), out var c) ? c : Palette[Preferred(project)];
    }
}

public sealed record ProjectChip(string Name, IBrush Color, string Time);

/// <summary>A heading and its lines, in an entry's "what happened" section.</summary>
public sealed record EvidenceGroup(string Heading, IReadOnlyList<EvidenceLine> Lines);

/// <param name="Quote">Words the user typed: shown as a quote.</param>
public sealed record EvidenceLine(string Time, string Text, bool Quote = false);

/// <summary>One pomodoro in the Day window, or one stretch off the timer.</summary>
public sealed partial class EntryViewModel : ObservableObject
{
    private readonly DayViewModel _day;

    public EntryViewModel(DayViewModel day, JournalEntry entry, TimeZoneInfo zone, Func<string?, Color> colors)
    {
        _day = day;
        Entry = entry;
        string Time(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString(ClockFormat.Is24Hour ? "HH:mm" : "h:mm", CultureInfo.CurrentCulture);
        Start = Time(entry.Block.StartUtc);
        Range = $"{Start} – {Time(entry.Block.EndUtc)}";
        Length = entry.OffTimer ? (entry.Block.Minutes < 3 ? "a moment" : $"~{entry.Block.Minutes} min")
            : entry.InProgress ? $"{entry.Block.Minutes} min in" : $"{entry.Block.Minutes} min";
        var color = colors(entry.Projects.FirstOrDefault());
        // Off the timer, the rail is a quieter version of the project's colour: it wasn't focus.
        Color = new SolidColorBrush(entry.OffTimer ? global::Avalonia.Media.Color.FromArgb(0x55, color.R, color.G, color.B) : color);
        Projects = entry.Projects.DistinctBy(ProjectColors.Key).Select(p => new ProjectChip(p, new SolidColorBrush(colors(p)), "")).ToList();
        Badges = BuildBadges(entry.Evidence);
        Evidence = BuildEvidence(entry.Evidence, Time);
        _title = entry.Title;
    }

    public JournalEntry Entry { get; }
    public int PomodoroId => Entry.Block.PomodoroId;
    public string Start { get; }
    public string Range { get; }
    public string Length { get; }
    public IBrush Color { get; }
    public IReadOnlyList<ProjectChip> Projects { get; }
    public IReadOnlyList<string> Badges { get; }
    public IReadOnlyList<EvidenceGroup> Evidence { get; }

    public string Detail => Entry.Detail;
    public bool HasDetail => Detail.Length > 0 && !IsEditing;
    public bool HasChips => Projects.Count > 0 || Badges.Count > 0;
    public bool HasEvidence => Evidence.Count > 0;
    public bool InProgress => Entry.InProgress;
    public bool IsOffTimer => Entry.OffTimer;
    public string NowLabel => IsOffTimer ? "NOW" : "UNDER WAY";
    public bool IsNamedByUser => Entry.UserTitle != null;

    /// <summary>Nothing on record and nobody said what it was.</summary>
    public bool IsBlank => Title.Length == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank), nameof(ShownTitle))]
    private string _title;

    public string ShownTitle => Title.Length > 0 ? Title : InProgress ? "Under way" : "Nothing on record";

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private bool _isEditing;

    [ObservableProperty] private string _editText = "";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Edit()
    {
        if (InProgress) return;
        EditText = Title;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void SaveEdit()
    {
        if (!IsEditing) return;
        IsEditing = false;
        var text = EditText.Trim();
        if (text == Title) return;
        // Cleared: the write-up names it again.
        Title = text.Length > 0 ? text : JournalBuilder.FallbackTitle(Entry.Evidence);
        _day.Rename(this, text.Length > 0 ? text : null);
    }

    private static IReadOnlyList<string> BuildBadges(BlockEvidence e)
    {
        var badges = new List<string>();
        if (e.Commits.Count > 0) badges.Add(e.Commits.Count == 1 ? "1 commit" : $"{e.Commits.Count} commits");
        var prompts = e.Sessions.Sum(s => s.Prompts.Count);
        if (e.Sessions.Count > 0) badges.Add(prompts > 0 ? $"{prompts} prompt{(prompts == 1 ? "" : "s")}" : "Claude Code");
        foreach (var site in e.Sites.Take(2)) badges.Add(site.Site);
        return badges;
    }

    private static IReadOnlyList<EvidenceGroup> BuildEvidence(BlockEvidence e, Func<DateTime, string> time)
    {
        var groups = new List<EvidenceGroup>();
        foreach (var s in e.Sessions)
        {
            var lines = new List<EvidenceLine>();
            foreach (var p in s.Prompts) lines.Add(new EvidenceLine(time(p.AtUtc), p.Text, Quote: true));
            if (lines.Count == 0) lines.Add(new EvidenceLine("", $"Busy for about {s.Minutes} min, nothing typed in this time"));
            var where = s.Project.Length > 0 ? $" · {s.Project}" : "";
            groups.Add(new EvidenceGroup($"Claude Code · {s.Title}{where}", lines));
        }
        if (e.Commits.Count > 0)
            groups.Add(new EvidenceGroup("Commits", e.Commits.Select(c => new EvidenceLine(time(c.AtUtc), $"{c.Subject}  ·  {c.Repo}")).ToList()));
        if (e.Sites.Count > 0)
            groups.Add(new EvidenceGroup("Pages", e.Sites.Select(s => new EvidenceLine("", $"{s.Site}  —  {string.Join("  ·  ", s.Titles)}")).ToList()));
        if (e.Apps.Count > 0)
            groups.Add(new EvidenceGroup("In front", e.Apps.Select(a => new EvidenceLine($"{a.Minutes} min",
                a.Titles.Count > 0 ? $"{a.App}  —  {string.Join("  ·  ", a.Titles)}" : a.App)).ToList()));
        return groups;
    }
}

public sealed class SessionViewModel
{
    public SessionViewModel(WorkSession session, SessionSummary? summary, IReadOnlyList<EntryViewModel> entries, TimeZoneInfo zone)
    {
        string Time(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString(ClockFormat.Is24Hour ? "HH:mm" : "h:mm", CultureInfo.CurrentCulture);
        var done = entries.Count(e => !e.InProgress && !e.IsOffTimer);
        var running = entries.Any(e => e.InProgress && !e.IsOffTimer);
        var count = (done, running) switch
        {
            (0, true) => "under way",
            (_, true) => $"{done} pomodoro{(done == 1 ? "" : "s")} and one under way",
            _ => $"{done} pomodoro{(done == 1 ? "" : "s")}",
        };
        Label = $"SESSION {session.Number}";
        Range = $"{Time(session.StartUtc)} – {(running ? "now" : Time(session.EndUtc))}  ·  {count}  ·  {DayViewModel.Duration(session.Minutes)}";
        Title = summary?.Title is { Length: > 0 } t ? t : "";
        Summary = summary?.Summary ?? "";
        Entries = entries;
    }

    /// <summary>Time at the computer between sessions (or before the first, after the last).</summary>
    public SessionViewModel(IReadOnlyList<EntryViewModel> offTimer, TimeZoneInfo zone, bool measured)
    {
        string Time(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString(ClockFormat.Is24Hour ? "HH:mm" : "h:mm", CultureInfo.CurrentCulture);
        var first = offTimer[0].Entry.Block;
        var last = offTimer[^1];
        IsOffTimer = true;
        Label = "OFF THE TIMER";
        Range = $"{Time(first.StartUtc)} – {(last.InProgress ? "now" : Time(last.Entry.Block.EndUtc))}";
        // Without the windows recorded, the stretches are moments that left a trace, not a length.
        if (measured) Range += $"  ·  about {DayViewModel.Duration(offTimer.Sum(e => e.Entry.Block.Minutes))} at the computer";
        Title = Summary = "";
        Entries = offTimer;
    }

    public bool IsOffTimer { get; }
    public string Label { get; }
    public string Range { get; }
    public string Title { get; }
    public string Summary { get; }
    public bool HasTitle => Title.Length > 0;
    public bool HasSummary => Summary.Length > 0;
    public IReadOnlyList<EntryViewModel> Entries { get; }
}

/// <summary>A block on the day's ribbon.</summary>
public sealed record RibbonBlock(double Start, double Length, Color Color, string Tip, int PomodoroId, bool InProgress, bool OffTimer = false);

/// <summary>
/// The Day window: one day of pomodoros with what was done in each, sessions and the day
/// written up, stepping back and forth through days. Reads off the UI thread; writes up in
/// the background and fills in as it arrives.
/// </summary>
public sealed partial class DayViewModel : ObservableObject, IDisposable
{
    private readonly JournalService _journal;
    private readonly JournalSettings _settings;
    private readonly TimeZoneInfo _zone = TimeZoneInfo.Local;
    private readonly DispatcherTimer _live;
    private CancellationTokenSource? _loading;
    private JournalDay? _current;
    private int _generation;
    private Func<string?, Color> _colors = ProjectColors.ForDay([]);

    public DayViewModel(JournalService journal, JournalSettings settings)
    {
        _journal = journal;
        _settings = settings;
        _date = journal.Today;
        _journal.DayChanged += OnDayChanged;
        // The pomodoro under way grows; today's view keeps up once a minute.
        _live = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _live.Tick += (_, _) => { if (IsToday && !IsWriting) _ = LoadAsync(write: false); };
        _live.Start();
    }

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public ObservableCollection<ProjectChip> ProjectTotals { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Weekday), nameof(DateText), nameof(IsToday), nameof(DateCaption))]
    private DateOnly _date;

    public string Weekday => IsToday ? "Today" : Date == _journal.Today.AddDays(-1) ? "Yesterday" : Date.ToString("dddd", CultureInfo.CurrentCulture);
    public string DateText => Date.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
    public string DateCaption => IsToday || Date == _journal.Today.AddDays(-1) ? DateText : Date.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    public bool IsToday => Date == _journal.Today;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private string _span = "";
    [ObservableProperty] private IReadOnlyList<RibbonBlock> _ribbon = [];
    [ObservableProperty] private DateTime _ribbonStartLocal;
    [ObservableProperty] private DateTime _ribbonEndLocal;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isWriting;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _showSignIn;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _hasPrevious;
    [ObservableProperty] private bool _hasNext;
    [ObservableProperty] private string _sources = "";

    /// <summary>What's read and what isn't, in the footer: it follows the settings.</summary>
    public string PrivacyNote
    {
        get
        {
            var text = new StringBuilder(_settings.WholeDay ? "Read on this computer, all day" : "Read on this computer, from inside pomodoros only");
            if (_settings.PauseApps.Count > 0) text.Append(". Nothing at all while ").Append(string.Join(" or ", _settings.PauseApps)).Append(" is open");
            text.Append(", and nothing from the apps you skip. The write-up goes to Claude through your own sign-in, with anything that looks like a key or a password blanked. Settings → Journal says what's read.");
            return text.ToString();
        }
    }

    public bool HasSummary => Summary.Length > 0;
    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(HasSummary));

    public bool HasStatus => Status.Length > 0;
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>Scroll to this pomodoro (a block on the ribbon was clicked).</summary>
    public event Action<EntryViewModel>? Reveal;

    public void Open(DateOnly? date = null)
    {
        if (date is { } d) Date = d;
        _ = LoadAsync(write: true);
    }

    [RelayCommand]
    private void Previous()
    {
        if (_journal.Neighbours(Date).Previous is { } p) Go(p);
    }

    [RelayCommand]
    private void Next()
    {
        var next = _journal.Neighbours(Date).Next;
        Go(next is { } n && n <= _journal.Today ? n : _journal.Today);
    }

    [RelayCommand]
    private void Today() => Go(_journal.Today);

    private void Go(DateOnly date)
    {
        if (date == Date) return;
        Date = date;
        Sessions.Clear();
        ProjectTotals.Clear();
        Headline = Summary = Stats = Span = Status = "";
        Ribbon = [];
        _ = LoadAsync(write: true);
    }

    [RelayCommand]
    private void Retry() => _ = LoadAsync(write: true);

    [RelayCommand]
    private async Task SignIn()
    {
        var outcome = await _journal.Account.SignInAsync();
        if (outcome is SignInOutcome.GotToken or SignInOutcome.SignedIn) await LoadAsync(write: true);
    }

    /// <summary>Reads the day; then, when it isn't written up for its evidence, writes it up.</summary>
    private async Task LoadAsync(bool write)
    {
        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        var generation = ++_generation;
        var date = Date;
        IsLoading = _current == null || _current.Date != date;
        try
        {
            var day = await Task.Run(() => _journal.Load(date), cancel.Token);
            if (generation != _generation) return;
            Show(day);
            IsLoading = false;
            if (!write || !day.Stale || !_settings.Summaries) return;

            IsWriting = true;
            Status = day.Summary == null ? "Writing up your day…" : "Catching up on the latest…";
            ShowSignIn = CanRetry = false;
            // Not cancelled on moving to another day: it's paid for, and that day opens written next time.
            var (written, problem) = await _journal.WriteAsync(date, CancellationToken.None);
            if (generation != _generation) return;
            IsWriting = false;
            Show(written);
            if (problem != null)
            {
                Status = problem.SignedOut
                    ? "Signed out of Claude, so this is the evidence without the write-up."
                    : $"Couldn't write it up: {problem.Message}";
                ShowSignIn = problem.SignedOut;
                CanRetry = !problem.SignedOut;
            }
            else
            {
                Status = "";
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                IsWriting = _journal.IsWriting && IsWriting;
            }
        }
    }

    private void OnDayChanged(DateOnly day)
    {
        if (day != Date) return;
        Dispatcher.UIThread.Post(() => { if (!IsWriting) _ = LoadAsync(write: false); });
    }

    private void Show(JournalDay day)
    {
        _current = day;
        var (previous, next) = _journal.Neighbours(day.Date);
        HasPrevious = previous != null;
        HasNext = !IsToday;
        IsEmpty = day.Entries.Count == 0;

        var expanded = Sessions.SelectMany(s => s.Entries).Where(e => e.IsExpanded).Select(e => e.PomodoroId).ToHashSet();
        var totals = Totals(day).ToList();
        _colors = ProjectColors.ForDay(totals.Select(t => t.Name).Concat(day.Entries.SelectMany(e => e.Projects)));
        var entries = day.Entries.Select(e => new EntryViewModel(this, e, _zone, _colors) { IsExpanded = expanded.Contains(e.Block.PomodoroId) }).ToList();
        Sessions.Clear();
        foreach (var group in Groups(day, entries)) Sessions.Add(group);

        var done = day.Count;
        var parts = new List<string>();
        if (done > 0)
        {
            parts.Add($"{done} pomodoro{(done == 1 ? "" : "s")}");
            parts.Add($"{Duration(day.Minutes)} focused");
            parts.Add($"{day.Sessions.Count} session{(day.Sessions.Count == 1 ? "" : "s")}");
        }
        if (day.OffTimerMinutes > 0 && day.WindowsRecorded) parts.Add($"{Duration(day.OffTimerMinutes)} off the timer");
        Stats = string.Join("  ·  ", parts);
        if (day.Entries.Count > 0)
        {
            string T(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, _zone).ToString(ClockFormat.Is24Hour ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);
            Span = $"{T(day.Entries[0].Block.StartUtc)} – {T(day.Entries[^1].Block.EndUtc)}";
        }
        Headline = day.Summary?.Headline is { Length: > 0 } h ? h : FallbackHeadline(day);
        Summary = day.Summary?.Summary ?? "";
        BuildRibbon(day);
        BuildTotals(day);
        Sources = day.Sources.Count == 0 ? "" : "Put together from " + Join(day.Sources) + ".";
    }

    private static string FallbackHeadline(JournalDay day)
    {
        var projects = Totals(day).Take(3).Select(p => p.Name).ToList();
        return projects.Count > 0 ? string.Join(", ", projects) : day.Count > 0 ? $"{day.Count} pomodoros" : "No pomodoros, but you were at the computer";
    }

    /// <summary>
    /// The day in order: each session with its pomodoros and the breaks inside it, and between
    /// them the time off the timer, one group per run of it.
    /// </summary>
    private IEnumerable<SessionViewModel> Groups(JournalDay day, IReadOnlyList<EntryViewModel> entries)
    {
        var offRun = new List<EntryViewModel>();
        var shown = new HashSet<int>();
        foreach (var entry in entries)
        {
            if (entry.Entry.Session == 0)
            {
                offRun.Add(entry);
                continue;
            }
            if (offRun.Count > 0)
            {
                yield return new SessionViewModel(offRun, _zone, day.WindowsRecorded);
                offRun = [];
            }
            var number = entry.Entry.Session;
            if (!shown.Add(number)) continue;
            var session = day.Sessions.First(s => s.Number == number);
            yield return new SessionViewModel(session, day.SessionSummary(number), entries.Where(e => e.Entry.Session == number).ToList(), _zone);
        }
        if (offRun.Count > 0) yield return new SessionViewModel(offRun, _zone, day.WindowsRecorded);
    }

    private void BuildRibbon(JournalDay day)
    {
        if (day.Entries.Count == 0)
        {
            Ribbon = [];
            return;
        }
        var start = TimeZoneInfo.ConvertTimeFromUtc(day.Entries[0].Block.StartUtc, _zone);
        var end = TimeZoneInfo.ConvertTimeFromUtc(day.Entries[^1].Block.EndUtc, _zone);
        // Whole hours either side, so the ticks land on round numbers.
        RibbonStartLocal = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0);
        RibbonEndLocal = new DateTime(end.Year, end.Month, end.Day, end.Hour, 0, 0).AddHours(end.Minute > 0 ? 1 : 0);
        Ribbon = day.Entries.Select(e =>
        {
            var s = TimeZoneInfo.ConvertTimeFromUtc(e.Block.StartUtc, _zone);
            var f = TimeZoneInfo.ConvertTimeFromUtc(e.Block.EndUtc, _zone);
            return new RibbonBlock((s - RibbonStartLocal).TotalMinutes, (f - s).TotalMinutes, _colors(e.Projects.FirstOrDefault()),
                e.Title.Length > 0 ? e.Title : "Nothing on record", e.Block.PomodoroId, e.InProgress, e.OffTimer);
        }).ToList();
    }

    private void BuildTotals(JournalDay day)
    {
        ProjectTotals.Clear();
        foreach (var (name, minutes) in Totals(day).Take(6))
            ProjectTotals.Add(new ProjectChip(name, new SolidColorBrush(_colors(name)), Duration(minutes)));
    }

    /// <summary>Time per project: a pomodoro's minutes shared among its projects.</summary>
    private static IEnumerable<(string Name, int Minutes)> Totals(JournalDay day)
    {
        // Per project, and per spelling of it: the spelling used for the most time names it.
        var totals = new Dictionary<string, Dictionary<string, double>>();
        // Focus time only: time off the timer is shown, not counted.
        foreach (var e in day.Entries.Where(e => !e.InProgress && !e.OffTimer))
        {
            var projects = e.Projects.DistinctBy(ProjectColors.Key).ToList();
            foreach (var p in projects)
            {
                var spellings = totals.TryGetValue(ProjectColors.Key(p), out var found) ? found : totals[ProjectColors.Key(p)] = [];
                spellings[p] = spellings.GetValueOrDefault(p) + (double)e.Block.Minutes / projects.Count;
            }
        }
        return totals.Values
            .Select(v => (Name: v.MaxBy(x => x.Value).Key, Minutes: (int)Math.Round(v.Values.Sum())))
            .OrderByDescending(t => t.Minutes)
            .Where(t => t.Minutes > 0);
    }

    public void RevealPomodoro(int pomodoroId)
    {
        var entry = Sessions.SelectMany(s => s.Entries).FirstOrDefault(e => e.PomodoroId == pomodoroId);
        if (entry == null) return;
        entry.IsExpanded = true;
        Reveal?.Invoke(entry);
    }

    internal void Rename(EntryViewModel entry, string? title)
    {
        var date = Date;
        _ = Task.Run(() => _journal.SetTitle(date, entry.PomodoroId, title));
    }

    /// <summary>The day as plain text: for a standup, a note, a message.</summary>
    public string AsText()
    {
        if (_current is not { } day || day.Entries.Count == 0) return "";
        var text = new StringBuilder();
        text.Append(DateText);
        if (Stats.Length > 0) text.Append(" — ").Append(Stats.Replace("  ·  ", ", "));
        text.Append('\n');
        if (Headline.Length > 0) text.Append(Headline).Append('\n');
        if (Summary.Length > 0) text.Append('\n').Append(Summary).Append('\n');
        foreach (var session in Sessions)
        {
            text.Append('\n').Append(session.Label[0]).Append(session.Label[1..].ToLowerInvariant()).Append(session.HasTitle ? ": " + session.Title : "")
                .Append(" (").Append(session.Range.Split("  ·  ")[0]).Append(")\n");
            foreach (var e in session.Entries.Where(e => !e.IsBlank))
            {
                text.Append(e.IsOffTimer && !session.IsOffTimer ? "  ◦ break " : "• ").Append(e.Start).Append("  ").Append(e.Title);
                if (e.Detail.Length > 0) text.Append(" — ").Append(e.Detail);
                text.Append('\n');
            }
        }
        return text.ToString();
    }

    public static string Duration(int minutes) => minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    public void Dispose()
    {
        _live.Stop();
        _loading?.Cancel();
        _journal.DayChanged -= OnDayChanged;
    }
}
