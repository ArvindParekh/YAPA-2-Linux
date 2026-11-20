using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using YAPA.Avalonia.Platform.Linux.X11;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Windows;

/// <summary>
/// The Studio timer: the redesign's card. Everything that changes with the timer's state (the
/// dot, the progress hairline, the alert glow) takes one colour from <see cref="StateBrush"/>.
/// </summary>
public partial class StudioWindow : TimerWindowBase
{
    // The XAML is laid out at this size (card and its shadow's margin); Size scales it.
    private const double DesignWidth = 328;
    private const double DesignHeight = 132;
    private const double DesignSize = 200; // the Size setting this layout is drawn for
    private const double TrackWidth = 296 - 36;

    public StudioWindow()
    {
        InitializeComponent();
        _leave.Tick += (_, _) =>
        {
            _leave.Stop();
            Hovering = IsPointerOver || MenuButton.ContextMenu?.IsOpen == true;
        };
        ViewModel.PropertyChanged += OnTimerChanged;
        // The card sits in a Viewbox, so its own bounds never change after the first layout: the
        // window's size (the Size setting) and its scaling are what move the clickable area.
        Card.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) UpdateInputRegion();
        };
        ScalingChanged += (_, _) => PostInputRegion();
        RefreshState();
    }

    protected override string PositionKey => "StudioWindow";

    protected override Control MenuAnchor => MenuButton;

    public static readonly StyledProperty<IBrush> StateBrushProperty =
        AvaloniaProperty.Register<StudioWindow, IBrush>(nameof(StateBrush), Brushes.Gray);

    /// <summary>The state's colour: ember in focus, mint on a break, amber when held, grey when idle.</summary>
    public IBrush StateBrush
    {
        get => GetValue(StateBrushProperty);
        private set => SetValue(StateBrushProperty, value);
    }

    public static readonly StyledProperty<string> TimeDisplayProperty =
        AvaloniaProperty.Register<StudioWindow, string>(nameof(TimeDisplay), "00:00");

    /// <summary>The time, with a "+" while an ended interval runs over.</summary>
    public string TimeDisplay
    {
        get => GetValue(TimeDisplayProperty);
        private set => SetValue(TimeDisplayProperty, value);
    }

    public static readonly StyledProperty<string> CycleTextProperty =
        AvaloniaProperty.Register<StudioWindow, string>(nameof(CycleText), "");

    /// <summary>"2/4": where this pomodoro sits in the cycle; empty when idle.</summary>
    public string CycleText
    {
        get => GetValue(CycleTextProperty);
        private set => SetValue(CycleTextProperty, value);
    }

    public static readonly StyledProperty<string> AsideTextProperty =
        AvaloniaProperty.Register<StudioWindow, string>(nameof(AsideText), "");

    /// <summary>The status line's middle: the user's own note when there is one, else when the goal will be reached.</summary>
    public string AsideText
    {
        get => GetValue(AsideTextProperty);
        private set => SetValue(AsideTextProperty, value);
    }

    public static readonly StyledProperty<bool> HoveringProperty =
        AvaloniaProperty.Register<StudioWindow, bool>(nameof(Hovering));

    /// <summary>
    /// The pointer is on the card (or the menu is open): skip, stop and the window's buttons show.
    /// At rest the card is just the state, the time and the one button.
    /// </summary>
    public bool Hovering
    {
        get => GetValue(HoveringProperty);
        private set => SetValue(HoveringProperty, value);
    }

    private readonly global::Avalonia.Threading.DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(700) };

    protected override void OnPointerEntered(global::Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _leave.Stop();
        Hovering = true;
    }

    protected override void OnPointerExited(global::Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // A moment's grace, so the buttons don't vanish as the pointer crosses the card's edge.
        _leave.Stop();
        _leave.Start();
    }

    private void OnTimerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModels.TimerViewModel.Progress):
                UpdateProgress();
                break;
            case nameof(ViewModels.TimerViewModel.TimeText) or nameof(ViewModels.TimerViewModel.IsOverrun):
                UpdateTime();
                break;
            case nameof(ViewModels.TimerViewModel.Phase) or nameof(ViewModels.TimerViewModel.IsOffRecord)
                or nameof(ViewModels.TimerViewModel.CycleSuffix) or nameof(ViewModels.TimerViewModel.NoteSuffix)
                or nameof(ViewModels.TimerViewModel.GoalForecastText) or nameof(ViewModels.TimerViewModel.HasGoalForecast)
                or nameof(ViewModels.TimerViewModel.CounterText):
                RefreshState();
                break;
        }
    }

    private void RefreshState()
    {
        var key = ViewModel.Phase switch
        {
            PomodoroPhase.Work => "Focus",
            PomodoroPhase.Break => "Rest",
            PomodoroPhase.Pause or PomodoroPhase.WorkEnded or PomodoroPhase.BreakEnded => "Hold",
            _ => "Text3",
        };
        if (this.TryFindResource(key, out var brush) && brush is IBrush b) StateBrush = b;
        // The classic timer's suffix reads " · Cycle 2"; here the cycle is the number alone, as the pomodoro's place in it.
        // Left out when the counter at the right already is the place in the cycle.
        var settings = EngineSettings;
        CycleText = ViewModel.Phase is PomodoroPhase.NotStarted || settings.Counter == CounterEnum.PomodoroIndex ? ""
            : ViewModel.Engine.Index + "/" + Math.Max(1, settings.PomodorosBeforeLongBreak);
        var note = ViewModel.NoteSuffix.TrimStart(' ', '·').Trim();
        // The count is already at the right ("3/12"), so the forecast needs only its time: "goal by 18:40".
        AsideText = note.Length > 0 ? note
            : ViewModel.HasGoalForecast ? System.Text.RegularExpressions.Regex.Replace(ViewModel.GoalForecastText, @"^\d+ ", "goal ").Replace("after midnight", "past midnight")
            : "";
        UpdateTime();
        UpdateProgress();
    }

    private static YAPA.Shared.Common.PomodoroEngineSettings EngineSettings => App.Bootstrapper!.Resolve<YAPA.Shared.Common.PomodoroEngineSettings>();

    private void UpdateTime() => TimeDisplay = (ViewModel.IsOverrun ? "+" : "") + ViewModel.TimeText;

    private void UpdateProgress()
    {
        var p = ViewModel.Phase is PomodoroPhase.NotStarted ? 0 : Math.Clamp(ViewModel.Progress, 0, 1);
        ProgressFill.Width = TrackWidth * p;
    }

    protected override void OnAppearanceChanged()
    {
        var scale = Math.Max(0.6, Appearance.Width / DesignSize);
        Width = Math.Round(DesignWidth * scale);
        Height = Math.Round(DesignHeight * scale);
        StatusLabel.IsVisible = Appearance.ShowStatusText;
        Progress.IsVisible = Appearance.ShowProgress;
        // Without the flashing alert, the glow holds still instead of breathing.
        Glow.Classes.Set("still", Appearance.DisableFlashingAnimation);
    }

    /// <summary>Clicks in the shadow's margin go to whatever is underneath, as if the card were the window.</summary>
    private void UpdateInputRegion()
    {
        if (TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle || handle.Handle == IntPtr.Zero) return;
        var origin = Card.TranslatePoint(new Point(0, 0), this);
        if (origin is not { } o) return;
        var s = RenderScaling;
        var scale = Bounds.Width / DesignWidth;
        X11Window.SetInputRegion(handle.Handle, ((int)Math.Floor(o.X * s), (int)Math.Floor(o.Y * s),
            (int)Math.Ceiling(Card.Bounds.Width * scale * s), (int)Math.Ceiling(Card.Bounds.Height * scale * s)));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) PostInputRegion();
    }

    /// <summary>After layout, when the card has its new place in the window.</summary>
    private void PostInputRegion() => global::Avalonia.Threading.Dispatcher.UIThread.Post(UpdateInputRegion, global::Avalonia.Threading.DispatcherPriority.Background);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateInputRegion();
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= OnTimerChanged;
        base.OnClosed(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => MinimizeOrHide();

    private void OnMenuClick(object? sender, RoutedEventArgs e)
    {
        OpenActionsMenu(MenuButton);
        // Hover was held while the menu was open; once it closes, let the buttons go if the pointer has.
        if (MenuButton.ContextMenu is { } menu && !_watchingMenu)
        {
            _watchingMenu = true;
            menu.Closed += (_, _) => { _leave.Stop(); _leave.Start(); };
        }
    }

    private bool _watchingMenu;
}
