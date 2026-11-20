using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using YAPA.Avalonia.Journal;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Settings;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.Statistics;
using YAPA.Avalonia.ViewModels;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Hub;

/// <summary>A place in the Hub's "Your time" group: Today, Week, Insights.</summary>
/// <param name="Keys">The keys that go there ("G T"), shown on hover.</param>
public sealed record HubLink(string Key, string Title, Geometry Icon, string Keys);

/// <summary>
/// The Hub: everything that isn't the timer, in one window. Your day and your week, Insights,
/// and Settings' pages, side by side in a sidebar; G then T, W, I or S goes to each, and Ctrl+K
/// opens the command bar. The window draws its own frame (title bar, buttons, resize edges).
/// </summary>
public partial class HubWindow : Window, INotifyPropertyChanged, IFilePicker
{
    public const string TodayKey = "today", WeekKey = "week", InsightsKey = "insights";

    private SettingsPageViewModel? _selectedPage;
    private HubLink? _selectedLink;
    private DateTime _pageShownAt = DateTime.UtcNow;
    private DateTime _goPressedAt = DateTime.MinValue; // "G", waiting for the second key
    private IBrush _stateBrush = Brushes.Gray;

    public HubWindow()
    {
        var bs = App.Bootstrapper!;
        Timer = bs.Resolve<TimerViewModel>();
        Day = new DayViewModel(bs.Resolve<JournalService>(), bs.Resolve<JournalSettings>());
        Insights = new StatisticsViewModel(bs.Resolve<IPomodoroRepository>(), bs.Resolve<IPomodoroEngine>(), bs.Resolve<GeneralSettings>(),
            bs.Resolve<PomodoroEngineSettings>());
        Links =
        [
            new(TodayKey, "Today", Geometry.Parse("M 12 3.5 V 5.5 M 12 18.5 V 20.5 M 3.5 12 H 5.5 M 18.5 12 H 20.5 M 6 6 L 7.4 7.4 M 16.6 16.6 L 18 18 M 6 18 L 7.4 16.6 M 16.6 7.4 L 18 6 M 16 12 A 4 4 0 1 1 8 12 A 4 4 0 1 1 16 12 Z"), "G T"),
            new(WeekKey, "Week", Geometry.Parse("M 5 6 H 19 A 1.5 1.5 0 0 1 20.5 7.5 V 19 A 1.5 1.5 0 0 1 19 20.5 H 5 A 1.5 1.5 0 0 1 3.5 19 V 7.5 A 1.5 1.5 0 0 1 5 6 Z M 3.5 10.5 H 20.5 M 8 3.5 V 7.5 M 16 3.5 V 7.5"), "G W"),
            new(InsightsKey, "Insights", Geometry.Parse("M 4 20 H 20 M 7 16 V 11 M 12 16 V 6 M 17 16 V 9"), "G I"),
        ];
        Pages =
        [
            new TimerPageViewModel(bs.Resolve<PomodoroEngineSettings>(), bs.Resolve<GeneralSettings>()),
            new AppearancePageViewModel(bs.Resolve<AvaloniaYapaThemeSettings>(), bs.Resolve<AvaloniaThemeSettings>(),
                bs.Resolve<YAPA.Avalonia.Assistant.TimerLooks>(), bs.Resolve<TimerTexts>()),
            bs.Resolve<SoundsPageFactory>().Create(),
            new AssistantPageViewModel(bs.Resolve<YAPA.Avalonia.Assistant.AssistantSettings>(), bs.Resolve<YAPA.Avalonia.Assistant.AssistantMemory>(),
                bs.Resolve<YAPA.Avalonia.Assistant.Scheduler>(), bs.Resolve<YAPA.Avalonia.Assistant.ClaudeAccount>()),
            new JournalPageViewModel(bs.Resolve<JournalSettings>(), bs.Resolve<JournalService>()),
            new GeneralPageViewModel(bs.Resolve<GeneralSettings>(), bs.Resolve<AvaloniaYapaThemeSettings>()),
            new AboutPageViewModel(),
        ];
        foreach (var sounds in Pages.OfType<SoundsPageViewModel>()) sounds.FilePicker = this;

        InitializeComponent();
        DataContext = this;
        WindowDecorations = WindowDecorations.None;

        // A freshly shown page asks to bring its selected swatches and lists into view, which
        // scrolled it halfway down; for a moment after a page switch, those requests are ignored.
        PageHost.AddHandler(RequestBringIntoViewEvent, (_, e) =>
        {
            if (DateTime.UtcNow - _pageShownAt < TimeSpan.FromMilliseconds(600)) e.Handled = true;
        }, RoutingStrategies.Bubble);

        // W in the journal switches day and week: the sidebar follows.
        Day.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DayViewModel.IsWeek) && SelectedLink?.Key is TodayKey or WeekKey)
                SetLink(Links.First(l => l.Key == (Day.IsWeek ? WeekKey : TodayKey)));
        };
        Timer.PropertyChanged += OnTimerChanged;
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        RefreshTimer();
        SetLink(Links[0]);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public TimerViewModel Timer { get; }
    public DayViewModel Day { get; }
    public StatisticsViewModel Insights { get; }
    public IReadOnlyList<HubLink> Links { get; }
    public IReadOnlyList<SettingsPageViewModel> Pages { get; }

    /// <summary>Today, Week or Insights; null while a settings page is shown.</summary>
    public HubLink? SelectedLink
    {
        get => _selectedLink;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedLink)) return;
            SetLink(value);
        }
    }

    /// <summary>The settings page shown; null while Your day, the week or Insights is.</summary>
    public SettingsPageViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedPage)) return;
            _selectedPage = value;
            _selectedLink = null;
            _pageShownAt = DateTime.UtcNow;
            Changed();
            Changed(nameof(SelectedLink));
            ChangedPlace();
            Dispatcher.UIThread.Post(() => PageScroller?.ScrollToHome(), DispatcherPriority.Loaded);
        }
    }

    public bool ShowsJournal => _selectedLink?.Key is TodayKey or WeekKey;
    public bool ShowsInsights => _selectedLink?.Key == InsightsKey;
    public bool ShowsSettings => _selectedPage != null;

    /// <summary>The title bar's "where you are": the group…</summary>
    public string Section => ShowsSettings ? "Settings" : "Your time";

    /// <summary>…and the page.</summary>
    public string PageTitle => _selectedPage?.Title ?? _selectedLink?.Title ?? "";

    /// <summary>The timer's state colour, for the mini timer's dot and line.</summary>
    public IBrush StateBrush
    {
        get => _stateBrush;
        private set
        {
            _stateBrush = value;
            Changed();
        }
    }

    // ── Going places ─────────────────────────────────────────────────────────

    private void SetLink(HubLink link)
    {
        _selectedLink = link;
        _selectedPage = null;
        Changed(nameof(SelectedLink));
        Changed(nameof(SelectedPage));
        ChangedPlace();
        switch (link.Key)
        {
            case TodayKey when Day.IsWeek:
                Day.ShowDayViewCommand.Execute(null);
                break;
            case WeekKey when !Day.IsWeek:
                Day.ShowWeekCommand.Execute(null);
                break;
            case InsightsKey:
                _ = Insights.LoadAsync();
                break;
        }
    }

    private void ChangedPlace()
    {
        Changed(nameof(ShowsJournal));
        Changed(nameof(ShowsInsights));
        Changed(nameof(ShowsSettings));
        Changed(nameof(Section));
        Changed(nameof(PageTitle));
    }

    /// <summary>Your day, on <paramref name="date"/> (today when null).</summary>
    public void ShowDay(DateOnly? date)
    {
        SetLink(Links[0]);
        if (date is { } d) Day.OpenDay(d);
        else Day.Open();
    }

    public void ShowInsights() => SetLink(Links[2]);

    public void ShowWeek() => SetLink(Links[1]);

    public void ShowSettings(SettingsPage page) => SelectedPage = Pages.FirstOrDefault(p => p.Page == page) ?? Pages[0];

    public void RefreshPages()
    {
        foreach (var page in Pages) page.Refresh();
    }

    /// <summary>
    /// Ctrl K, and G then a letter as in Linear: G T today, G W the week, G I insights, G S settings.
    /// Handled on the way down, before the page sees the keys: Your day has single-letter keys of its
    /// own (W toggles day and week), and G W from the week must stay on the week.
    /// </summary>
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.K)
        {
            OpenCommandBar();
            e.Handled = true;
            return;
        }
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        if (DateTime.UtcNow - _goPressedAt < TimeSpan.FromSeconds(1.2))
        {
            _goPressedAt = DateTime.MinValue;
            switch (e.Key)
            {
                case Key.T: ShowDay(null); e.Handled = true; return;
                case Key.W: ShowWeek(); e.Handled = true; return;
                case Key.I: ShowInsights(); e.Handled = true; return;
                case Key.S: SelectedPage = Pages[0]; e.Handled = true; return;
            }
        }
        if (e.Key == Key.G && !ctrl)
        {
            _goPressedAt = DateTime.UtcNow;
            e.Handled = true;
        }
    }

    private void OpenCommandBar() => App.Bootstrapper?.Resolve<AppNavigator>().ShowAssistant(this);

    private void OnSearchClick(object? sender, RoutedEventArgs e) => OpenCommandBar();

    private void OnMiniTimerClick(object? sender, RoutedEventArgs e) => App.Bootstrapper?.Resolve<AppNavigator>().ShowTimer();

    private void OnResetClick(object? sender, RoutedEventArgs e) => SelectedPage?.Reset();

    private void OnPreviewVolumeClick(object? sender, RoutedEventArgs e) => (SelectedPage as SoundsPageViewModel)?.PreviewVolume();

    // ── The timer, live ──────────────────────────────────────────────────────

    private void OnTimerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimerViewModel.Phase) or nameof(TimerViewModel.Progress)) RefreshTimer();
    }

    private void RefreshTimer()
    {
        var key = Timer.Phase switch
        {
            PomodoroPhase.Work => "Focus",
            PomodoroPhase.Break => "Rest",
            PomodoroPhase.Pause or PomodoroPhase.WorkEnded or PomodoroPhase.BreakEnded => "Hold",
            _ => "Text3",
        };
        if (this.TryFindResource(key, out var brush) && brush is IBrush b && !ReferenceEquals(b, _stateBrush)) StateBrush = b;
        var progress = Timer.Phase is PomodoroPhase.NotStarted ? 0 : Math.Clamp(Timer.Progress, 0, 1);
        MiniProgress.RenderTransform = new ScaleTransform(progress, 1);
    }

    // ── The window's own frame ───────────────────────────────────────────────

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) BeginMoveDrag(e);
    }

    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximized();

    private void ToggleMaximized() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var windowEdge)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
            BeginResizeDrag(windowEdge, e);
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────

    protected override void OnOpened(EventArgs e)
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

    protected override void OnClosed(EventArgs e)
    {
        Timer.PropertyChanged -= OnTimerChanged;
        Day.Dispose();
        Insights.Dispose();
        foreach (var sounds in Pages.OfType<SoundsPageViewModel>()) { sounds.EndPreviews(); sounds.Detach(); }
        foreach (var assistant in Pages.OfType<AssistantPageViewModel>()) assistant.Detach();
        foreach (var appearance in Pages.OfType<AppearancePageViewModel>()) appearance.Detach();
        foreach (var journal in Pages.OfType<JournalPageViewModel>()) journal.Detach();
        base.OnClosed(e);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
