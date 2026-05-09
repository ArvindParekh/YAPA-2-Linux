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
    private readonly IPomodoroEngine? _engine;

    private int _completedToday;
    public int CompletedToday
    {
        get => _completedToday;
        private set { _completedToday = value; Notify(); }
    }

    private string _focusedTodayDisplay = "";
    public string FocusedTodayDisplay
    {
        get => _focusedTodayDisplay;
        private set { _focusedTodayDisplay = value; Notify(); }
    }

    private List<DaySummary> _weekSummary = [];
    public List<DaySummary> WeekSummary
    {
        get => _weekSummary;
        private set { _weekSummary = value; Notify(); }
    }

    public DashboardPage() : this(
        App.Bootstrapper!.Resolve<IPomodoroRepository>(),
        App.Bootstrapper!.Resolve<IPomodoroEngine>()) { }

    public DashboardPage(IPomodoroRepository repo, IPomodoroEngine? engine = null)
    {
        _repo = repo;
        _engine = engine;
        InitializeComponent();
        DataContext = this;
        if (_engine != null)
            _engine.OnPomodoroCompleted += OnPomodoroCompleted;
        _ = LoadAsync();
    }

    private void OnPomodoroCompleted() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var today = _repo.CompletedToday();

        var fallbackMin = (_engine?.WorkTime ?? 1500) / 60;
        var (finalDays, minutesToday) = await Task.Run(() =>
        {
            var now = DateTime.UtcNow;
            var weekStart = now.Date.AddDays(-6);
            var records = _repo.After(weekStart);

            // Group by UTC date string so the grouping key matches how strftime stores dates
            var byDay = records
                .GroupBy(p => p.DateTime.ToString("yyyy-MM-dd"))
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Count));

            int max = 1;
            var days = new List<DaySummary>();
            for (int i = 6; i >= 0; i--)
            {
                var day = now.Date.AddDays(-i);
                var key = day.ToString("yyyy-MM-dd");
                var count = byDay.TryGetValue(key, out var c) ? c : 0;
                if (count > max) max = count;
                days.Add(new DaySummary
                {
                    Day = i == 0 ? "Today" : day.ToString("ddd dd"),
                    Count = count,
                    MaxCount = 0,
                });
            }

            var final = days.Select(d => new DaySummary { Day = d.Day, Count = d.Count, MaxCount = max }).ToList();

            // Compute focused minutes today from DurationMin stored per pomodoro
            var todayKey = now.Date.ToString("yyyy-MM-dd");
            var mins = records
                .Where(r => r.DateTime.ToString("yyyy-MM-dd") == todayKey)
                .Sum(r => r.DurationMin > 0 ? r.DurationMin : fallbackMin);

            return (final, mins);
        });

        var focusedDisplay = minutesToday >= 60
            ? $"≈ {minutesToday / 60}h {minutesToday % 60}m focused today"
            : $"≈ {minutesToday}m focused today";

        Dispatcher.UIThread.Post(() =>
        {
            CompletedToday = today;
            WeekSummary = finalDays;
            FocusedTodayDisplay = minutesToday > 0 ? focusedDisplay : "";
        });
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
        => _ = LoadAsync();
}
