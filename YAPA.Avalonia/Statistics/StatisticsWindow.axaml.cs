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
        var bs = App.Bootstrapper!;
        _vm = new StatisticsViewModel(
            bs.Resolve<YAPA.Shared.Contracts.IPomodoroRepository>(),
            bs.Resolve<YAPA.Shared.Contracts.IPomodoroEngine>(),
            bs.Resolve<YAPA.Avalonia.Specifics.GeneralSettings>());
        DataContext = _vm;
        _ = _vm.LoadAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
