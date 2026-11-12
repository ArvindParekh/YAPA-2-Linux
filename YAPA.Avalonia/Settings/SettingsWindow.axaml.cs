using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;

namespace YAPA.Avalonia.Settings;

public partial class SettingsWindow : Window, INotifyPropertyChanged, IFilePicker
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
            new GeneralPageViewModel(bs.Resolve<GeneralSettings>(), bs.Resolve<AvaloniaYapaThemeSettings>(), bs.Resolve<YAPA.Avalonia.Assistant.AssistantSettings>()),
            new AboutPageViewModel(),
        };
        _selectedPage = Pages[0];
        foreach (var sounds in Pages.OfType<SoundsPageViewModel>()) sounds.FilePicker = this;

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

    public void RefreshPages()
    {
        foreach (var page in Pages) page.Refresh();
    }

    public void Navigate(SettingsPage page) => SelectedPage = Pages.FirstOrDefault(p => p.Page == page) ?? Pages[0];

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnResetClick(object? sender, RoutedEventArgs e) => SelectedPage.Reset();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        // Coming back after dropping files into the Sounds folder shows them straight away.
        Activated += (_, _) => { foreach (var sounds in Pages.OfType<SoundsPageViewModel>()) sounds.RescanLibrary(); };
    }

    protected override void OnClosed(System.EventArgs e)
    {
        foreach (var sounds in Pages.OfType<SoundsPageViewModel>()) { sounds.EndPreviews(); sounds.Detach(); }
        base.OnClosed(e);
    }

    private void OnPreviewVolumeClick(object? sender, RoutedEventArgs e)
        => (SelectedPage as SoundsPageViewModel)?.PreviewVolume();

    public async Task<string?> PickAudioFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio") { Patterns = ["*.wav", "*.ogg", "*.oga", "*.opus", "*.flac", "*.mp3", "*.m4a"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
