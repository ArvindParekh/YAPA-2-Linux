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
    private System.DateTime _pageShownAt = System.DateTime.UtcNow;

    public SettingsWindow()
    {
        var bs = App.Bootstrapper!;
        Pages = new List<SettingsPageViewModel>
        {
            new TimerPageViewModel(bs.Resolve<PomodoroEngineSettings>(), bs.Resolve<GeneralSettings>()),
            new AppearancePageViewModel(bs.Resolve<AvaloniaYapaThemeSettings>(), bs.Resolve<AvaloniaThemeSettings>(),
                bs.Resolve<YAPA.Avalonia.Assistant.TimerLooks>(), bs.Resolve<TimerTexts>()),
            bs.Resolve<SoundsPageFactory>().Create(),
            new AssistantPageViewModel(bs.Resolve<YAPA.Avalonia.Assistant.AssistantSettings>(), bs.Resolve<YAPA.Avalonia.Assistant.AssistantMemory>(),
                bs.Resolve<YAPA.Avalonia.Assistant.Scheduler>()),
            new GeneralPageViewModel(bs.Resolve<GeneralSettings>(), bs.Resolve<AvaloniaYapaThemeSettings>()),
            new AboutPageViewModel(),
        };
        _selectedPage = Pages[0];
        foreach (var sounds in Pages.OfType<SoundsPageViewModel>()) sounds.FilePicker = this;

        InitializeComponent();
        DataContext = this;
        // A freshly shown page asks to bring its selected swatches and lists into view, which
        // scrolled it halfway down; for a moment after a page switch, those requests are ignored.
        PageHost.AddHandler(RequestBringIntoViewEvent, (_, e) =>
        {
            if (System.DateTime.UtcNow - _pageShownAt < System.TimeSpan.FromMilliseconds(600)) e.Handled = true;
        }, global::Avalonia.Interactivity.RoutingStrategies.Bubble);
        // Before the first map, so the frame comes up dark rather than flipping.
        if (TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle)
            YAPA.Avalonia.Platform.Linux.X11.X11Window.PreferDarkFrame(handle.Handle);
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
            _pageShownAt = System.DateTime.UtcNow;
            Notify();
            // A new page starts at its top, once it has been laid out.
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => PageScroller?.ScrollToHome(), global::Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    public void RefreshPages()
    {
        foreach (var page in Pages) page.Refresh();
    }

    public void Navigate(SettingsPage page) => SelectedPage = Pages.FirstOrDefault(p => p.Page == page) ?? Pages[0];

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnResetClick(object? sender, RoutedEventArgs e) => SelectedPage.Reset();

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        // Coming back after dropping files into the Sounds folder shows them straight away.
        Activated += (_, _) =>
        {
            foreach (var sounds in Pages.OfType<SoundsPageViewModel>()) sounds.RescanLibrary();
            // The assistant may have remembered or scheduled something while this was open.
            foreach (var assistant in Pages.OfType<AssistantPageViewModel>()) assistant.Reload();
        };
    }

    protected override void OnClosed(System.EventArgs e)
    {
        foreach (var sounds in Pages.OfType<SoundsPageViewModel>()) { sounds.EndPreviews(); sounds.Detach(); }
        foreach (var assistant in Pages.OfType<AssistantPageViewModel>()) assistant.Detach();
        foreach (var appearance in Pages.OfType<AppearancePageViewModel>()) appearance.Detach();
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
