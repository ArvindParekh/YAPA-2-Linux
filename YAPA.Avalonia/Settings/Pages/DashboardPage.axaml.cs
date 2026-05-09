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
        var today = _repo.CompletedToday();
        var now = DateTime.Now;
        var days = new List<DaySummary>();
        int max = 1;

        for (int i = 6; i >= 0; i--)
        {
            var day = now.Date.AddDays(-i);
            var count = await Task.Run(() => _repo.After(day).Count());
            if (count > max) max = count;
            days.Add(new DaySummary
            {
                Day = i == 0 ? "Today" : day.ToString("ddd dd"),
                Count = count,
                MaxCount = 0, // placeholder
            });
        }

        // Re-create with correct MaxCount
        var finalDays = new List<DaySummary>();
        foreach (var d in days)
            finalDays.Add(new DaySummary { Day = d.Day, Count = d.Count, MaxCount = max });

        Dispatcher.UIThread.Post(() =>
        {
            CompletedToday = today;
            WeekSummary = finalDays;
        });
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
        => _ = LoadAsync();
}
