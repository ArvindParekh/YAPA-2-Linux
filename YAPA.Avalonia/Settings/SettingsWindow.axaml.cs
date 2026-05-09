using Avalonia.Controls;
using Avalonia.Interactivity;
using YAPA.Avalonia.Settings.Pages;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;
using IPomodoroRepository = YAPA.Shared.Contracts.IPomodoroRepository;

namespace YAPA.Avalonia.Settings;

public partial class SettingsWindow : Window
{
    private readonly ISettings _settings;
    private readonly DashboardPage _dashPage;
    private readonly EngineSettingsPage _enginePage;
    private readonly ThemeSettingsPage _themePage;
    private readonly SoundSettingsPage _soundPage;
    private readonly ThemeSelectorPage _themeSelectorPage;

    public SettingsWindow()
    {
        var bs = App.Bootstrapper!;
        _settings = bs.Resolve<ISettings>();

        _dashPage          = new DashboardPage(bs.Resolve<IPomodoroRepository>());
        _enginePage        = new EngineSettingsPage(bs.Resolve<PomodoroEngineSettings>());
        _themePage         = new ThemeSettingsPage(bs.Resolve<AvaloniaYapaThemeSettings>());
        _soundPage         = new SoundSettingsPage(
            bs.Resolve<PomodoroEngineSettings>(),
            bs.Resolve<AvaloniaSoundNotificationsSettings>(),
            bs.Resolve<AvaloniaMusicPlayerSettings>());
        _themeSelectorPage = new ThemeSelectorPage(bs.Resolve<AvaloniaThemeSettings>());

        InitializeComponent();
        NavList.SelectedIndex = 0;
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        PageContent.Content = NavList.SelectedIndex switch
        {
            0 => _dashPage,
            1 => _enginePage,
            2 => _themePage,
            3 => _soundPage,
            4 => _themeSelectorPage,
            _ => null,
        };
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _settings.Save();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _settings.Load();
        Close();
    }
}
