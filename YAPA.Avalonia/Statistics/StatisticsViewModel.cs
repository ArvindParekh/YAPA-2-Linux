using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Contracts;
using YAPA.Shared.Statistics;

namespace YAPA.Avalonia.Statistics;

public sealed record StatCard(string Title, string Value, string Detail);

public sealed record DayBar(string Label, string Tooltip, int Count, double Fraction, bool IsToday);

public sealed partial class StatisticsViewModel : ObservableObject, IDisposable
{
    public const string AllProfiles = "All profiles";

    /// <summary>A full year, like GitHub's contribution graph.</summary>
    private const int HeatmapWeeks = 53;
    private const int BarDays = 14;

    private readonly IPomodoroRepository _repository;
    private readonly IPomodoroEngine _engine;
    private readonly GeneralSettings _general;
    private bool _loading;

    public StatisticsViewModel(IPomodoroRepository repository, IPomodoroEngine engine, GeneralSettings general)
    {
        _repository = repository;
        _engine = engine;
        _general = general;
        _engine.OnPomodoroCompleted += OnCompleted;
    }

    public ObservableCollection<StatCard> Cards { get; } = [];
    public ObservableCollection<DayBar> Bars { get; } = [];
    public ObservableCollection<string> Profiles { get; } = [AllProfiles];

    [ObservableProperty] private Heatmap? _heatmap;
    [ObservableProperty] private string _heatmapSummary = "";
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private string _selectedProfile = AllProfiles;
    [ObservableProperty] private bool _hasMultipleProfiles;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private double _goalProgress;
    [ObservableProperty] private string _goalText = "";
    [ObservableProperty] private bool _hasGoal;

    partial void OnSelectedProfileChanged(string value) => _ = LoadAsync();

    private void OnCompleted() => Dispatcher.UIThread.Post(() => _ = LoadAsync());

    public async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var profile = SelectedProfile == AllProfiles ? null : SelectedProfile;
            var query = new StatisticsQuery(TimeZoneInfo.Local, DateTime.UtcNow)
            {
                Profile = profile,
                HeatmapWeeks = HeatmapWeeks,
                FirstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek,
            };
            // The table is small, but SQLite and the statistics pass still don't belong on the UI thread.
            var report = await Task.Run(() => PomodoroStatistics.Compute(_repository.All(), query));
            Apply(report);
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"[stats] load failed: {e}");
        }
        finally
        {
            _loading = false;
        }
    }

    private void Apply(StatisticsReport r)
    {
        foreach (var p in r.Profiles.Where(p => !Profiles.Contains(p))) Profiles.Add(p);
        HasMultipleProfiles = Profiles.Count > 2;
        IsEmpty = r.AllTime.Count == 0;

        var goal = _general.DailyGoal;
        HasGoal = goal > 0;
        GoalProgress = HasGoal ? Math.Min(1, r.TodayTotal.Count / (double)goal) : 0;
        GoalText = HasGoal ? $"{r.TodayTotal.Count} of {goal} today" : "";

        Cards.Clear();
        Cards.Add(new StatCard("Today", Count(r.TodayTotal.Count), Focus(r.TodayTotal.FocusMinutes)));
        Cards.Add(new StatCard("This week", Count(r.ThisWeek.Count), Focus(r.ThisWeek.FocusMinutes)));
        Cards.Add(new StatCard("Streak", Days(r.CurrentStreak.Length),
            r.BestStreak.IsEmpty ? "No streaks yet" : $"Best: {Days(r.BestStreak.Length)}"));
        Cards.Add(new StatCard("All time", Count(r.AllTime.Count), Focus(r.AllTime.FocusMinutes)));

        Bars.Clear();
        var first = r.Today.AddDays(-(BarDays - 1));
        var days = r.Daily.Range(first, r.Today);
        var max = Math.Max(1, days.Max(d => d.Count));
        foreach (var d in days)
        {
            var label = d.Date == r.Today ? "Today" : d.Date.ToString("ddd d", CultureInfo.CurrentCulture);
            Bars.Add(new DayBar(label, $"{d.Date:D}: {Count(d.Count)}, {Focus(d.FocusMinutes)}", d.Count, d.Count / (double)max, d.Date == r.Today));
        }

        Heatmap = r.Heatmap;
        var yearTotal = r.Heatmap.Weeks.SelectMany(w => w.Days).Where(c => !c.IsFuture).Sum(c => c.Count);
        HeatmapSummary = $"{Count(yearTotal)} in the last year";

        Footer = r.BestDay is { } best
            ? $"Best day: {best.Date:D} ({Count(best.Count)}) · Average on active days: {r.AverageCountPerActiveDay:0.#} pomodoros, {Focus((int)Math.Round(r.AverageMinutesPerActiveDay))}"
            : "Complete a pomodoro to start your history.";
    }

    private static string Count(int n) => n == 1 ? "1 pomodoro" : $"{n} pomodoros";
    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";

    private static string Focus(int minutes)
    {
        if (minutes < 60) return $"{minutes} min focused";
        var h = minutes / 60;
        var m = minutes % 60;
        return m == 0 ? $"{h} h focused" : $"{h} h {m} min focused";
    }

    public void Dispose() => _engine.OnPomodoroCompleted -= OnCompleted;
}
