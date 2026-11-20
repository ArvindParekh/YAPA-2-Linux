using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YAPA.Avalonia.Statistics;

/// <summary>
/// Insights, as a page of the Hub: the statistics, where every day you can see (a cell, a bar,
/// the best day) opens in Your day.
/// </summary>
public partial class InsightsView : UserControl
{
    public InsightsView()
    {
        InitializeComponent();
        Heatmap.DayClicked += OpenDay;
    }

    private StatisticsViewModel? Model => DataContext as StatisticsViewModel;

    private static void OpenDay(DateOnly date) => App.Bootstrapper?.Resolve<YAPA.Avalonia.Services.AppNavigator>().ShowDay(date);

    private void OnDayBarClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DayBar bar }) OpenDay(bar.Date);
    }

    private void OnBestDayClick(object? sender, RoutedEventArgs e)
    {
        if (Model?.BestDay is { } day) OpenDay(day);
    }
}
