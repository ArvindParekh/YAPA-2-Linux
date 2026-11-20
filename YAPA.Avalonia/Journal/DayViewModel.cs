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
        var color = entry.OffRecord ? ProjectColors.None : colors(entry.Projects.FirstOrDefault());
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

    /// <summary>A pomodoro whose time is off the record: it's there, and nothing about it is.</summary>
    public bool IsOffRecord => Entry.OffRecord;

    /// <summary>Anything to forget: off the record already has nothing.</summary>
    public bool CanForget => !IsOffRecord;

    public bool CanRename => !InProgress && !IsOffRecord;

    public string ForgetNote => IsOffTimer
        ? "Its windows and write-up are deleted, and nothing from that time is read again. Your browser's and Claude Code's own history aren't touched."
        : "What was recorded in it and its write-up are deleted, and nothing from that time is read again. The pomodoro stays, and still counts. " +
          "Your browser's and Claude Code's own history aren't touched.";

    /// <summary>"Forget 21:40 – 22:15?", up to now for the one going on.</summary>
    public string ForgetQuestion => InProgress ? $"Forget {Start} until now?" : $"Forget {Range.Replace(" – ", "–")}?";

    /// <summary>Ask YAPA conversations that would go with it; read when the confirmation opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConversations), nameof(ConversationsText))]
    private int _conversations;

    [ObservableProperty] private bool _forgetConversations = true;

    public bool HasConversations => Conversations > 0;
    public string ConversationsText => DayViewModel.ConversationsLabel(Conversations);

    public void PrepareForget() => Conversations = _day.ConversationsSince(Entry.Block.StartUtc);

    [RelayCommand]
    private void Forget() => _day.Forget(Entry.Block.StartUtc, InProgress ? null : Entry.Block.EndUtc, ForgetConversations && HasConversations);
    public string NowLabel => IsOffTimer ? "NOW" : Entry.Paused ? "PAUSED" : "UNDER WAY";

    /// <summary>Held: the badge takes the timer's held colour rather than focus.</summary>
    public bool IsHeld => !IsOffTimer && Entry.Paused;
    public bool IsNamedByUser => Entry.UserTitle != null;

    /// <summary>Nothing on record and nobody said what it was.</summary>
    public bool IsBlank => Title.Length == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank), nameof(ShownTitle))]
    private string _title;

    public string ShownTitle => IsOffRecord ? "Off the record" : Title.Length > 0 ? Title
        : InProgress ? (Entry.Paused ? "Paused" : "Under way") : "Nothing on record";

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
        if (InProgress || IsOffRecord) return;
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
        var state = entries.Any(e => e.InProgress && e.Entry.Paused) ? "paused" : "under way";
        var count = (done, running) switch
        {
            (0, true) => state,
            (_, true) => $"{done} pomodoro{(done == 1 ? "" : "s")} and one {state}",
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

/// <summary>A day in the calendar: its number, and a dot when anything happened on it.</summary>
public sealed partial class CalendarDay : ObservableObject
{
    public CalendarDay(DateOnly date, bool inMonth, bool isToday, bool isSelected, bool isFuture)
    {
        Date = date;
        InMonth = inMonth;
        IsToday = isToday;
        IsSelected = isSelected;
        IsFuture = isFuture;
    }

    public DateOnly Date { get; }
    public string Label => Date.Day.ToString(CultureInfo.CurrentCulture);
    public bool InMonth { get; }
    public bool IsToday { get; }
    public bool IsSelected { get; }
    public bool IsFuture { get; }
    public bool CanPick => !IsFuture;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPomodoros), nameof(OnlyPresence), nameof(DotOpacity), nameof(Tip))]
    private int _pomodoros;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlyPresence), nameof(Tip))]
    private bool _atComputer;

    public bool HasPomodoros => Pomodoros > 0;

    /// <summary>At the computer, without a pomodoro: a quieter mark.</summary>
    public bool OnlyPresence => AtComputer && Pomodoros == 0;

    /// <summary>Fuller for a fuller day, so a month reads at a glance.</summary>
    public double DotOpacity => Pomodoros == 0 ? 0 : 0.4 + 0.6 * Math.Min(1.0, Pomodoros / 10.0);

    public string Tip => Pomodoros > 0 ? $"{Pomodoros} pomodoro{(Pomodoros == 1 ? "" : "s")}" : AtComputer ? "At the computer, no pomodoros" : "";
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
    private readonly DispatcherTimer _noticeTimer;

    public DayViewModel(JournalService journal, JournalSettings settings)
    {
        _journal = journal;
        _settings = settings;
        _date = journal.Today;
        _calendarMonth = new DateOnly(_date.Year, _date.Month, 1);
        _journal.DayChanged += OnDayChanged;
        _journal.OffRecord.Changed += OnOffRecordChanged;
        RefreshOffRecord();
        var names = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
        var firstDay = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        WeekdayInitials = Enumerable.Range(0, 7).Select(i => names[(firstDay + i) % 7]).ToList();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice = "";
        };
        Week = new WeekViewModel(journal, settings);
        Week.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WeekViewModel.Start))
            {
                OnPropertyChanged(nameof(Weekday));
                OnPropertyChanged(nameof(DateCaption));
                OnPropertyChanged(nameof(CanGoForward));
                OnPropertyChanged(nameof(AtToday));
            }
        };
        // The pomodoro under way grows; today's view keeps up once a minute.
        _live = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _live.Tick += (_, _) => { if (!IsWeek && IsToday && !IsWriting) _ = LoadAsync(write: false); };
        _live.Start();
    }

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public ObservableCollection<ProjectChip> ProjectTotals { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Weekday), nameof(DateText), nameof(IsToday), nameof(DateCaption), nameof(AtToday))]
    private DateOnly _date;

    public string Weekday => IsWeek ? Week.Title
        : IsToday ? "Today" : Date == _journal.Today.AddDays(-1) ? "Yesterday" : Date.ToString("dddd", CultureInfo.CurrentCulture);
    public string DateText => Date.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
    public string DateCaption => IsWeek ? Week.Range
        : IsToday || Date == _journal.Today.AddDays(-1) ? DateText : Date.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    // ── Week ─────────────────────────────────────────────────────────────────

    /// <summary>Your week: the same window, a week at a time.</summary>
    public WeekViewModel Week { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Weekday), nameof(DateCaption), nameof(CanGoBack), nameof(CanGoForward), nameof(IsDay), nameof(AtToday))]
    private bool _isWeek;

    public bool IsDay => !IsWeek;

    public bool CanGoBack => IsWeek || HasPrevious;
    public bool CanGoForward => IsWeek ? Week.CanNext : HasNext;

    partial void OnHasPreviousChanged(bool value) => OnPropertyChanged(nameof(CanGoBack));
    partial void OnHasNextChanged(bool value) => OnPropertyChanged(nameof(CanGoForward));

    [RelayCommand]
    private void ShowWeek()
    {
        if (IsWeek) return;
        IsWeek = true;
        Week.Go(JournalService.WeekStart(Date));
    }

    [RelayCommand]
    private void ShowDayView()
    {
        if (!IsWeek) return;
        IsWeek = false;
        _ = LoadAsync(write: true);
    }

    /// <summary>Today, in the day view, whatever was being browsed (the hub stays open across days).</summary>
    public void ShowToday() => OpenDay(_journal.Today);

    /// <summary>This week, whatever week was being browsed.</summary>
    public void ShowThisWeek()
    {
        IsWeek = true;
        Week.Go(JournalService.WeekStart(_journal.Today));
    }

    /// <summary>A day picked in the week: the day view, on it.</summary>
    public void OpenDay(DateOnly date)
    {
        IsWeek = false;
        if (date == Date) _ = LoadAsync(write: true);
        else Go(date);
    }
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
            text.Append(". Nothing while you're off the record");
            if (_settings.PauseApps.Count > 0) text.Append(" or ").Append(string.Join(" or ", _settings.PauseApps)).Append(" is open");
            if (_settings.SkipAdult) text.Append(", nothing from adult sites");
            text.Append(", nothing from the apps you skip, and nothing from time you forget. The write-up goes to Claude through your own sign-in, " +
                        "with anything that looks like a key or a password blanked. Settings → Journal says what's read.");
            return text.ToString();
        }
    }

    // ── Off the record ───────────────────────────────────────────────────────

    [ObservableProperty] private bool _isOffRecord;
    [ObservableProperty] private string _offRecordText = "";

    private void OnOffRecordChanged() => Dispatcher.UIThread.Post(() =>
    {
        RefreshOffRecord();
        if (IsToday && !IsWriting) _ = LoadAsync(write: false);
    });

    private void RefreshOffRecord()
    {
        var until = _journal.OffRecord.Until;
        IsOffRecord = until != null;
        OffRecordText = until is { } u && u < OffTheRecord.Indefinitely
            ? $"Off the record until {Time(u)}. Nothing is being recorded, and nothing from this time will be used."
            : "Off the record until you turn it back on. Nothing is being recorded, and nothing from this time will be used.";
    }

    /// <param name="minutes">How long; empty for until turned back on.</param>
    [RelayCommand]
    private void GoOffRecord(string? minutes)
        => _journal.OffRecord.Start(int.TryParse(minutes, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : null);

    [RelayCommand]
    private void BackOnRecord() => _journal.OffRecord.Stop();

    // ── Forgetting ───────────────────────────────────────────────────────────

    /// <summary>A line under the day for a few seconds: what was just forgotten.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    public bool HasNotice => Notice.Length > 0;

    [ObservableProperty] private bool _isForgetting;

    /// <summary>The stretch to forget, as typed: "21:40", and "now" (today) or "24:00" for the end of the day.</summary>
    [ObservableProperty] private string _forgetFrom = "";
    [ObservableProperty] private string _forgetTo = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForgetProblem))]
    private string _forgetProblem = "";

    public bool HasForgetProblem => ForgetProblem.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForgetConversations), nameof(ForgetConversationsText))]
    private int _forgetConversationCount;

    [ObservableProperty] private bool _forgetConversations = true;

    public bool HasForgetConversations => ForgetConversationCount > 0;
    public string ForgetConversationsText => ConversationsLabel(ForgetConversationCount);

    public static string ConversationsLabel(int count)
        => count == 1 ? "Also delete the Ask YAPA conversation since then" : $"Also delete the {count} Ask YAPA conversations since then";

    internal int ConversationsSince(DateTime utc) => _journal.ConversationsSince(utc);

    /// <summary>The Forget panel opened: start from the last quarter of an hour today, or the whole of a past day.</summary>
    public void PrepareForgetRange()
    {
        if (IsToday) Preset("15");
        else Preset("day");
        ForgetProblem = "";
    }

    [RelayCommand]
    private void ForgetPreset(string which)
    {
        Preset(which);
        ForgetProblem = "";
    }

    private void Preset(string which)
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _zone);
        switch (which)
        {
            case "15" or "60" when IsToday:
                var start = now.AddMinutes(-int.Parse(which, CultureInfo.InvariantCulture));
                ForgetFrom = (start.Date < now.Date ? now.Date : start).ToString("HH:mm", CultureInfo.InvariantCulture);
                ForgetTo = "now";
                break;
            default:
                ForgetFrom = "00:00";
                ForgetTo = IsToday ? "now" : "24:00";
                break;
        }
    }

    partial void OnForgetFromChanged(string value)
    {
        ForgetConversationCount = ParseForget(value, isEnd: false) is { } from ? _journal.ConversationsSince(from) : 0;
    }

    /// <returns>True when it went ahead; otherwise <see cref="ForgetProblem"/> says why.</returns>
    public bool ForgetTypedRange()
    {
        var from = ParseForget(ForgetFrom, isEnd: false);
        var to = ParseForget(ForgetTo, isEnd: true);
        var now = DateTime.UtcNow;
        ForgetProblem = from == null || to == null
            ? IsToday ? "Times are like 21:40; the end can also be \"now\"." : "Times are like 21:40; the end can also be 24:00."
            : from >= now ? "That hasn't happened yet. To keep what's coming off the record, go off the record below."
            : to <= from ? "The end comes before the start."
            : "";
        if (HasForgetProblem) return false;
        Forget(from!.Value, to!.Value < now ? to : null, ForgetConversations && HasForgetConversations);
        return true;
    }

    private DateTime? ParseForget(string text, bool isEnd)
    {
        var t = text.Trim().ToLowerInvariant();
        if (isEnd && t == "now" && IsToday) return DateTime.UtcNow;
        if (isEnd && t is "24:00" or "24") return _journal.Bounds(Date).To;
        if (!TimeOnly.TryParseExact(t, ["HH:mm", "H:mm", "HH.mm", "H.mm", "HHmm", "%H"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        return TimeZoneInfo.ConvertTimeToUtc(Date.ToDateTime(time), _zone);
    }

    /// <param name="toUtc">Null: up to now.</param>
    internal async void Forget(DateTime fromUtc, DateTime? toUtc, bool conversations)
    {
        IsForgetting = true;
        try
        {
            var outcome = await Task.Run(() => _journal.Forget(fromUtc, toUtc ?? DateTime.UtcNow, conversations));
            var deleted = outcome.Conversations > 0 ? $", with {outcome.Conversations} Ask YAPA conversation{(outcome.Conversations == 1 ? "" : "s")}" : "";
            Notice = $"Forgot {Time(outcome.FromUtc)}–{Time(outcome.ToUtc)}{deleted}. Your browser's and Claude Code's own history weren't touched; YAPA won't read that time again.";
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException)
        {
            Notice = $"Couldn't forget it: {e.Message}";
        }
        finally
        {
            IsForgetting = false;
        }
        await LoadAsync(write: true);
    }

    private string Time(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone).ToString(ClockFormat.Is24Hour ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);

    // ── Calendar ─────────────────────────────────────────────────────────────

    public ObservableCollection<CalendarDay> CalendarDays { get; } = [];
    public IReadOnlyList<string> WeekdayInitials { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarTitle), nameof(CanCalendarNext))]
    private DateOnly _calendarMonth;

    public string CalendarTitle => CalendarMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    public bool CanCalendarNext => CalendarMonth < new DateOnly(_journal.Today.Year, _journal.Today.Month, 1);

    /// <summary>Asks the window to close the calendar: a day was picked.</summary>
    public event Action? CalendarDone;

    /// <summary>The calendar opened: the month of the day shown.</summary>
    public void PrepareCalendar() => ShowMonth(new DateOnly(Date.Year, Date.Month, 1));

    [RelayCommand]
    private void CalendarPrevious() => ShowMonth(CalendarMonth.AddMonths(-1));

    [RelayCommand]
    private void CalendarNext()
    {
        if (CanCalendarNext) ShowMonth(CalendarMonth.AddMonths(1));
    }

    [RelayCommand]
    private void PickDay(CalendarDay day)
    {
        if (day.IsFuture) return;
        CalendarDone?.Invoke();
        OpenDay(day.Date);
    }

    private int _monthGeneration;

    /// <summary>Six weeks around the month, so the calendar keeps its height; the marks fill in once read.</summary>
    private async void ShowMonth(DateOnly first)
    {
        CalendarMonth = first;
        var generation = ++_monthGeneration;
        var today = _journal.Today;
        var lead = ((int)first.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var start = first.AddDays(-lead);
        CalendarDays.Clear();
        for (var i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            CalendarDays.Add(new CalendarDay(d, d.Month == first.Month, d == today, d == Date, d > today));
        }
        try
        {
            var marks = await Task.Run(() => _journal.Marks(start, start.AddDays(41)));
            if (generation != _monthGeneration) return;
            foreach (var day in CalendarDays)
                if (marks.TryGetValue(day.Date, out var mark))
                {
                    day.Pomodoros = mark.Pomodoros;
                    day.AtComputer = mark.AtComputer;
                }
        }
        catch (Exception e) when (e is InvalidOperationException or System.IO.IOException)
        {
            // Without marks the calendar still works.
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
        if (IsWeek) Week.Go(Week.Start.AddDays(-7));
        else if (_journal.Neighbours(Date).Previous is { } p) Go(p);
    }

    [RelayCommand]
    private void Next()
    {
        if (IsWeek)
        {
            if (Week.CanNext) Week.Go(Week.Start.AddDays(7));
            return;
        }
        var next = _journal.Neighbours(Date).Next;
        Go(next is { } n && n <= _journal.Today ? n : _journal.Today);
    }

    [RelayCommand]
    private void Today()
    {
        if (IsWeek) Week.Go(JournalService.WeekStart(_journal.Today));
        else Go(_journal.Today);
    }

    /// <summary>Back at this week or today: the Today button has nothing to do.</summary>
    public bool AtToday => IsWeek ? Week.IsThisWeek : IsToday;

    private void Go(DateOnly date)
    {
        if (date == Date || date > _journal.Today) return;
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
                    : ProblemText(problem.Message);
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
        if (IsWeek)
        {
            // A day of the week shown changed (a pomodoro done, a write-up in): the week reads it again.
            if (JournalService.WeekStart(day) == Week.Start) Dispatcher.UIThread.Post(() => { if (!Week.IsWriting) _ = Week.LoadAsync(write: false); });
            return;
        }
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

    /// <summary>
    /// Why the write-up didn't happen, in a line: a known failure in plain words, a short message
    /// as it is, and anything else (a stack trace, a wall of output) left to the log, which has it.
    /// </summary>
    internal static string ProblemText(string message)
    {
        if (YAPA.Avalonia.Assistant.AssistantViewModel.KnownFailure(message) is { } known) return known;
        var first = message.Trim().Split('\n')[0].Trim();
        var readable = first.Length is > 0 and <= 120 && !first.StartsWith("Traceback", StringComparison.Ordinal)
                       && !message.Contains("\n   at ", StringComparison.Ordinal);
        return readable ? $"Couldn't write it up: {first}" : "Couldn't write it up this time. Try again; the details are in the log.";
    }

    private static string FallbackHeadline(JournalDay day)
    {
        var projects = Totals(day).Take(3).Select(p => p.Name).ToList();
        return projects.Count > 0 ? string.Join(", ", projects)
            : day.Count > 0 ? $"{day.Count} pomodoro{(day.Count == 1 ? "" : "s")}"
            : "No pomodoros, but you were at the computer";
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
            return new RibbonBlock((s - RibbonStartLocal).TotalMinutes, (f - s).TotalMinutes,
                e.OffRecord ? ProjectColors.None : _colors(e.Projects.FirstOrDefault()),
                e.OffRecord ? "Off the record" : e.Title.Length > 0 ? e.Title : "Nothing on record", e.Block.PomodoroId, e.InProgress, e.OffTimer);
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

    /// <summary>The day (or the week) as plain text: for a standup, a note, a message.</summary>
    public string AsText()
    {
        if (IsWeek) return Week.AsText();
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
        _noticeTimer.Stop();
        _loading?.Cancel();
        Week.Cancel();
        _journal.DayChanged -= OnDayChanged;
        _journal.OffRecord.Changed -= OnOffRecordChanged;
    }
}
