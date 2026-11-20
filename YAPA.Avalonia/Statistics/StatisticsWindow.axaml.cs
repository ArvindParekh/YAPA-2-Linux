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
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
