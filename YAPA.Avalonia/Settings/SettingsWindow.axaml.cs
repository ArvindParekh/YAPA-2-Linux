using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;

namespace YAPA.Avalonia.Settings;

public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private SettingsPageViewModel _selectedPage;

    public SettingsWindow()
    {
        var bs = App.Bootstrapper!;
        Pages = new List<SettingsPageViewModel>
        {
            new TimerPageViewModel(bs.Resolve<PomodoroEngineSettings>()),
            new AppearancePageViewModel(bs.Resolve<AvaloniaYapaThemeSettings>(), bs.Resolve<AvaloniaThemeSettings>()),
            bs.Resolve<SoundsPageFactory>().Create(),
            new GeneralPageViewModel(bs.Resolve<GeneralSettings>(), bs.Resolve<AvaloniaYapaThemeSettings>()),
            new AboutPageViewModel(),
        };
        _selectedPage = Pages[0];

        InitializeComponent();
        DataContext = this;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SettingsPageViewModel> Pages { get; }

    public SettingsPageViewModel SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedPage)) return;
            _selectedPage = value;
            Notify();
        }
    }

    public void Navigate(SettingsPage page) => SelectedPage = Pages.FirstOrDefault(p => p.Page == page) ?? Pages[0];

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnResetClick(object? sender, RoutedEventArgs e) => SelectedPage.Reset();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
