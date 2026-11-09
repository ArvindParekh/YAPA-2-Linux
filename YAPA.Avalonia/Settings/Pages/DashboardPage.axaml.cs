using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using YAPA.Shared.Contracts;
using YAPA.Shared.Statistics;

namespace YAPA.Avalonia.Settings.Pages;

public sealed class DaySummary
{
    public required string Day { get; init; }
    public required int Count { get; init; }
    public required int MaxCount { get; init; }
}

public partial class DashboardPage : UserControl, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly IPomodoroRepository _repo;

    private int _completedToday;
    public int CompletedToday
    {
        get => _completedToday;
        private set { _completedToday = value; Notify(); }
    }

    private List<DaySummary> _weekSummary = [];
    public List<DaySummary> WeekSummary
    {
        get => _weekSummary;
        private set { _weekSummary = value; Notify(); }
    }

    public DashboardPage() : this(App.Bootstrapper!.Resolve<IPomodoroRepository>()) { }

    public DashboardPage(IPomodoroRepository repo)
    {
        _repo = repo;
        InitializeComponent();
        DataContext = this;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var (today, week) = await Task.Run(() =>
        {
            var timeZone = TimeZoneInfo.Local;
            var nowUtc = DateTime.UtcNow;
            var todayLocal = LocalCalendar.DateOf(nowUtc, timeZone);

            // A week plus a day of slack either side; DailyTotals buckets by exact local date.
            var daily = DailyTotals.From(_repo.Between(nowUtc.AddDays(-8), nowUtc.AddDays(1)), timeZone);
            return (daily[todayLocal].Count, daily.Range(todayLocal.AddDays(-6), todayLocal));
        });

        var max = Math.Max(1, week.Max(d => d.Count));
        var summaries = week
            .Select((d, i) => new DaySummary
            {
                Day = i == week.Count - 1 ? "Today" : d.Date.ToString("ddd dd"),
                Count = d.Count,
                MaxCount = max,
            })
            .ToList();

        Dispatcher.UIThread.Post(() =>
        {
            CompletedToday = today;
            WeekSummary = summaries;
        });
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
        => _ = LoadAsync();
}
