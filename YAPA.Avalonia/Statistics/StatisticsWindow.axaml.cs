using System;
using Avalonia.Controls;
using YAPA.Avalonia.Statistics;

namespace YAPA.Avalonia.Windows;

public partial class StatisticsWindow : Window
{
    private readonly StatisticsViewModel _vm;

    public StatisticsWindow()
    {
        InitializeComponent();
        // Before the first map, so the frame comes up dark like the content, as Settings and Your day do.
        if (TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle)
            YAPA.Avalonia.Platform.Linux.X11.X11Window.PreferDarkFrame(handle.Handle);
        var bs = App.Bootstrapper!;
        _vm = new StatisticsViewModel(
            bs.Resolve<YAPA.Shared.Contracts.IPomodoroRepository>(),
            bs.Resolve<YAPA.Shared.Contracts.IPomodoroEngine>(),
            bs.Resolve<YAPA.Avalonia.Specifics.GeneralSettings>(),
            bs.Resolve<YAPA.Shared.Common.PomodoroEngineSettings>());
        DataContext = _vm;
        _ = _vm.LoadAsync();
        Heatmap.DayClicked += OpenDay;
    }

    /// <summary>A day picked here opens in Your day, where what was done in it is.</summary>
    private static void OpenDay(DateOnly date) => App.Bootstrapper?.Resolve<YAPA.Avalonia.Services.AppNavigator>().ShowDay(date);

    private void OnDayBarClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DayBar bar }) OpenDay(bar.Date);
    }

    private void OnBestDayClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm.BestDay is { } day) OpenDay(day);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
