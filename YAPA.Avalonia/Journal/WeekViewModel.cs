using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Journal;

/// <summary>A day in the week view: its focus, as a bar against the week's fullest day, and its headline.</summary>
/// <param name="IsFuture">Still to come: there to keep the week's shape, and nothing to open.</param>
public sealed record WeekDayRow(DateOnly Date, string Weekday, string DateText, string Count, string Focus, string Headline,
    double Fraction, bool IsToday, bool IsEmpty, bool IsWritten, bool IsFuture = false);

/// <summary>
/// Your week: the days of one week with their focus and headlines, the projects across them,
/// and the week written up in a few lines from the days' own write-ups.
/// </summary>
public sealed partial class WeekViewModel : ObservableObject
{
    private readonly JournalService _journal;
    private readonly JournalSettings _settings;
    private CancellationTokenSource? _loading;
    private int _generation;
    private JournalWeek? _current;

    public WeekViewModel(JournalService journal, JournalSettings settings)
    {
        _journal = journal;
        _settings = settings;
        _start = JournalService.WeekStart(journal.Today);
    }

    public ObservableCollection<WeekDayRow> Days { get; } = [];
    public ObservableCollection<ProjectChip> Projects { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Range), nameof(IsThisWeek), nameof(CanNext))]
    private DateOnly _start;

    public bool IsThisWeek => Start == JournalService.WeekStart(_journal.Today);
    public bool CanNext => Start < JournalService.WeekStart(_journal.Today);

    public string Title => IsThisWeek ? "This week"
        : Start == JournalService.WeekStart(_journal.Today).AddDays(-7) ? "Last week"
        : "Week of " + Start.ToString("d MMMM", CultureInfo.CurrentCulture);

    public string Range
    {
        get
        {
            var end = Start.AddDays(6);
            var culture = CultureInfo.CurrentCulture;
            return Start.Month == end.Month
                ? $"{Start.Day}–{end.ToString("d MMMM yyyy", culture)}"
                : $"{Start.ToString("d MMMM", culture)} – {end.ToString("d MMMM yyyy", culture)}";
        }
    }

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isWriting;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _showSignIn;
    [ObservableProperty] private bool _canRetry;

    public bool HasSummary => Summary.Length > 0;
    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(HasSummary));

    public bool HasStatus => Status.Length > 0;
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public void Go(DateOnly start)
    {
        if (start > JournalService.WeekStart(_journal.Today)) return;
        if (start != Start)
        {
            Start = start;
            Days.Clear();
            Projects.Clear();
            Headline = Summary = Stats = Status = "";
            _current = null;
        }
        _ = LoadAsync(write: true);
    }

    /// <summary>Reads the week; then, when its write-up isn't for the days as they are, writes it.</summary>
    public async Task LoadAsync(bool write)
    {
        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        var generation = ++_generation;
        var start = Start;
        IsLoading = _current == null || _current.Start != start;
        try
        {
            var week = await Task.Run(() => _journal.LoadWeek(start), cancel.Token);
            if (generation != _generation) return;
            Show(week);
            IsLoading = false;
            if (!write || !week.Stale || !_settings.Summaries) return;

            IsWriting = true;
            ShowSignIn = CanRetry = false;
            Status = week.Summary == null ? "Writing up your week…" : "Catching up on the latest…";
            void Progress(DateOnly day) => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation) Status = $"Writing up {day.ToString("dddd", CultureInfo.CurrentCulture)} first…";
            });
            // Not cancelled on moving to another week: it's paid for, and the week opens written next time.
            var (written, problem) = await _journal.WriteWeekAsync(start, Progress, CancellationToken.None);
            if (generation != _generation) return;
            IsWriting = false;
            Show(written);
            if (problem != null)
            {
                Status = problem.SignedOut ? "Signed out of Claude, so this is the week without its write-up." : DayViewModel.ProblemText(problem.Message);
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
                IsWriting = IsWriting && _journal.IsWriting;
            }
        }
    }

    public void Cancel() => _loading?.Cancel();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task Retry() => LoadAsync(write: true);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task SignIn()
    {
        var outcome = await _journal.Account.SignInAsync();
        if (outcome is YAPA.Avalonia.Assistant.SignInOutcome.GotToken or YAPA.Avalonia.Assistant.SignInOutcome.SignedIn) await LoadAsync(write: true);
    }

    private void Show(JournalWeek week)
    {
        _current = week;
        IsEmpty = week.IsEmpty;
        var totals = week.Projects();
        var colors = ProjectColors.ForDay(totals.Select(t => t.Name));
        Projects.Clear();
        foreach (var (name, minutes) in totals.Take(8))
            Projects.Add(new ProjectChip(name, new SolidColorBrush(colors(name)), DayViewModel.Duration(minutes)));

        var parts = new List<string>();
        if (week.Pomodoros > 0)
        {
            parts.Add($"{week.Pomodoros} pomodoro{(week.Pomodoros == 1 ? "" : "s")}");
            parts.Add($"{DayViewModel.Duration(week.FocusMinutes)} focused");
            parts.Add($"{week.ActiveDays} day{(week.ActiveDays == 1 ? "" : "s")} with pomodoros");
        }
        Stats = string.Join("  ·  ", parts);
        Headline = week.Summary?.Headline is { Length: > 0 } h ? h : FallbackHeadline(week, totals);
        Summary = week.Summary?.Summary ?? "";

        Days.Clear();
        var culture = CultureInfo.CurrentCulture;
        var max = Math.Max(1, week.Days.Count == 0 ? 0 : week.Days.Max(d => d.Minutes));
        var today = _journal.Today;
        for (var i = 0; i < 7; i++)
        {
            var date = week.Start.AddDays(i);
            var day = week.Days.FirstOrDefault(d => d.Date == date);
            var weekday = date == today ? "Today" : date.ToString("ddd", culture);
            if (day == null)
            {
                // Still to come: a placeholder, so the week keeps its shape.
                Days.Add(new WeekDayRow(date, weekday, date.ToString("d MMM", culture), "", "", "", 0, false, true, false, IsFuture: true));
                continue;
            }
            var headline = day.Summary?.Headline is { Length: > 0 } written ? written
                : day.Entries.Count == 0 ? "Nothing on record"
                : day.Count == 0 ? "No pomodoros"
                : "Not written up yet";
            Days.Add(new WeekDayRow(date, weekday, date.ToString("d MMM", culture),
                day.Count == 0 ? "" : $"{day.Count}",
                day.Minutes == 0 ? "" : DayViewModel.Duration(day.Minutes),
                headline, day.Minutes / (double)max, date == today, day.Entries.Count == 0, day.Summary != null));
        }
    }

    private static string FallbackHeadline(JournalWeek week, IReadOnlyList<(string Name, int Minutes)> totals)
    {
        if (week.IsEmpty) return "Nothing on record this week";
        var top = totals.Take(3).Select(t => t.Name).ToList();
        return top.Count > 0 ? string.Join(", ", top) : $"{week.Pomodoros} pomodoro{(week.Pomodoros == 1 ? "" : "s")}";
    }

    /// <summary>The week as plain text: for a weekly review, a status update, a message.</summary>
    public string AsText()
    {
        if (_current is not { } week || week.IsEmpty) return "";
        var text = new StringBuilder();
        text.Append(Title).Append(" (").Append(Range).Append(')');
        if (Stats.Length > 0) text.Append(" — ").Append(Stats.Replace("  ·  ", ", "));
        text.Append('\n').Append(Headline).Append('\n');
        if (Summary.Length > 0) text.Append('\n').Append(Summary).Append('\n');
        text.Append('\n');
        foreach (var d in Days.Where(d => !d.IsEmpty))
        {
            text.Append("• ").Append(d.Weekday).Append(' ').Append(d.DateText);
            if (d.Count.Length > 0) text.Append(" (").Append(d.Count).Append(", ").Append(d.Focus).Append(')');
            text.Append(": ").Append(d.Headline).Append('\n');
        }
        if (Projects.Count > 0) text.Append("\nBy project: ").Append(string.Join(", ", Projects.Select(p => $"{p.Name} {p.Time}"))).Append('\n');
        return text.ToString();
    }
}
