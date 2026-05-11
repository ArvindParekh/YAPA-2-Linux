using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Windows;

public partial class MotivationalWindow : Window, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly IMainViewModel _viewModel;
    private readonly AvaloniaYapaThemeSettings _themeSettings;
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private IBrush _flashTarget = Brushes.Transparent;
    private bool _flashOn;
    private CancellationTokenSource _hideCts = new();

    // ── INPC properties ───────────────────────────────────────────────────────

    private IBrush _windowBackground = new SolidColorBrush(Color.Parse("#CC1a1a2e"));
    public IBrush WindowBackground
    {
        get => _windowBackground;
        private set { _windowBackground = value; Notify(); }
    }

    private IBrush _textBrush = Brushes.White;
    public IBrush TextBrush
    {
        get => _textBrush;
        private set { _textBrush = value; Notify(); }
    }

    private string _quoteText = "";
    public string QuoteText
    {
        get => _quoteText;
        private set { _quoteText = value; Notify(); }
    }

    private string _quoteSource = "";
    public string QuoteSource
    {
        get => _quoteSource;
        private set { _quoteSource = value; Notify(); }
    }

    private string _timerMinutes = "25";
    public string TimerMinutes
    {
        get => _timerMinutes;
        private set { _timerMinutes = value; Notify(); }
    }

    private string _timerSeconds = "00";
    public string TimerSeconds
    {
        get => _timerSeconds;
        private set { _timerSeconds = value; Notify(); }
    }

    private string _statusText = "YAPA 2.0";
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; Notify(); }
    }

    private bool _controlsVisible;
    public bool ControlsVisible
    {
        get => _controlsVisible;
        private set { _controlsVisible = value; Notify(); }
    }

    private global::Avalonia.Media.DropShadowDirectionEffect? _timerShadowEffect;
    public global::Avalonia.Media.DropShadowDirectionEffect? TimerShadowEffect
    {
        get => _timerShadowEffect;
        private set { _timerShadowEffect = value; Notify(); }
    }

    private bool _startVisible = true;
    public bool StartVisible { get => _startVisible; private set { _startVisible = value; Notify(); } }

    private bool _stopVisible;
    public bool StopVisible { get => _stopVisible; private set { _stopVisible = value; Notify(); } }

    private bool _pauseVisible;
    public bool PauseVisible { get => _pauseVisible; private set { _pauseVisible = value; Notify(); } }

    private bool _skipVisible;
    public bool SkipVisible { get => _skipVisible; private set { _skipVisible = value; Notify(); } }

    public ICommand StartCommand => _viewModel.StartCommand;
    public ICommand StopCommand  => _viewModel.StopCommand;
    public ICommand PauseCommand => _viewModel.PauseCommand;
    public ICommand SkipCommand  => _viewModel.SkipCommand;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MotivationalWindow()
    {
        var bs = App.Bootstrapper!;
        _viewModel     = bs.Resolve<IMainViewModel>();
        _themeSettings = bs.Resolve<AvaloniaYapaThemeSettings>();

        InitializeComponent();

        // Force transparency properties programmatically so they override theme defaults
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;

        // Set taskbar/dock icon
        using (var iconStream = AssetLoader.Open(new Uri("avares://YAPA.Avalonia/Assets/pomoTray.ico")))
            Icon = new WindowIcon(iconStream);

        DataContext = this;

        _flashTimer.Tick += OnFlashTick;
        _viewModel.Engine.PropertyChanged += OnEnginePropertyChanged;
        _viewModel.Engine.OnStarted += StopFlash;
        _viewModel.Engine.OnStopped += OnEngineStopped;

        // Apply appearance settings immediately, subscribe for live updates
        ApplyThemeSettings();
        _themeSettings.PropertyChanged += (_, __) => ApplyThemeSettings();

        RefreshQuote();
        UpdateTimer();
        UpdatePhase();
        UpdateStatus();
    }

    // ── Theme settings ────────────────────────────────────────────────────────

    private void ApplyThemeSettings()
    {
        Opacity   = _themeSettings.ClockOpacity;
        TextBrush = new SolidColorBrush(_themeSettings.TextColor);
        if (_themeSettings.DisableFlashingAnimation)
            StopFlash();

        TimerShadowEffect = _themeSettings.ShadowOpacity > 0
            ? new global::Avalonia.Media.DropShadowDirectionEffect
            {
                Color       = _themeSettings.ShadowColor,
                Opacity     = _themeSettings.ShadowOpacity,
                BlurRadius  = 8,
                Direction   = 315,
                ShadowDepth = 3,
            }
            : null;
    }

    // ── Opened ────────────────────────────────────────────────────────────────

    private void OnOpened(object? sender, EventArgs e)
    {
        // Suppress Mutter's compositor shadow on this transparent overlay window
        if (TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle)
            X11ShadowSuppressor.Apply(handle.Handle);
    }

    // ── Pointer events ────────────────────────────────────────────────────────

    private void OnPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
            BeginMoveDrag(e);
        else if (props.IsRightButtonPressed)
            _viewModel.ShowSettingsCommand?.Execute(null);
    }

    private void OnPointerEntered(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {
        _hideCts.Cancel();
        _hideCts = new CancellationTokenSource();
        ControlsVisible = true;
    }

    private async void OnPointerExited(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {
        var cts = _hideCts;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            ControlsVisible = false;
        }
        catch (OperationCanceledException) { }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (_themeSettings.MinimizeToTray)
            Hide();
        else
            WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                if (_viewModel.Engine.Phase == PomodoroPhase.Work)
                    _viewModel.PauseCommand.Execute(null);
                else if (_viewModel.StartCommand.CanExecute(null))
                    _viewModel.StartCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_viewModel.StopCommand.CanExecute(null))
                    _viewModel.StopCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ── Engine events ─────────────────────────────────────────────────────────

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPomodoroEngine.Elapsed):
            case nameof(IPomodoroEngine.DisplayValue):
                UpdateTimer();
                break;
            case nameof(IPomodoroEngine.Phase):
                UpdatePhase();
                UpdateStatus();
                StartFlash();
                RefreshQuote();
                break;
        }
    }

    private void UpdateTimer()
    {
        var total = _viewModel.Engine.DisplayValue;
        TimerMinutes = $"{total / 60:00}";
        TimerSeconds = $"{total % 60:00}";
    }

    private void UpdatePhase()
    {
        var phase = _viewModel.Engine.Phase;
        StartVisible = phase is PomodoroPhase.NotStarted or PomodoroPhase.WorkEnded
                            or PomodoroPhase.BreakEnded or PomodoroPhase.Pause;
        StopVisible  = phase is PomodoroPhase.Work or PomodoroPhase.Break
                            or PomodoroPhase.Pause or PomodoroPhase.WorkEnded;
        PauseVisible = phase is PomodoroPhase.Work;
        SkipVisible  = phase is PomodoroPhase.WorkEnded;
    }

    private void UpdateStatus()
    {
        StatusText = _viewModel.Engine.Phase switch
        {
            PomodoroPhase.NotStarted => "YAPA 2.0",
            PomodoroPhase.Work       => "Work",
            PomodoroPhase.WorkEnded  => "Work Ended",
            PomodoroPhase.Break      => "Break",
            PomodoroPhase.BreakEnded => "Break Ended",
            PomodoroPhase.Pause      => "Work Paused",
            _                        => string.Empty,
        };
    }

    private void RefreshQuote()
    {
        var q = Quotes.Random();
        QuoteText   = q.Text;
        QuoteSource = string.IsNullOrEmpty(q.Source) ? "" : $"— {q.Source}";
    }

    // ── Flash animation ───────────────────────────────────────────────────────

    private void StartFlash()
    {
        if (_themeSettings.DisableFlashingAnimation)
        {
            StopFlash();
            return;
        }

        var phase = _viewModel.Engine.Phase;
        if (phase == PomodoroPhase.WorkEnded)
            _flashTarget = new SolidColorBrush(Color.Parse("Tomato"));
        else if (phase == PomodoroPhase.BreakEnded)
            _flashTarget = new SolidColorBrush(Color.Parse("MediumSeaGreen"));
        else return;

        _flashOn = false;
        _flashTimer.Start();
    }

    private void StopFlash()
    {
        _flashTimer.Stop();
        _flashOn = false;
        WindowBackground = new SolidColorBrush(Color.Parse("#CC1a1a2e"));
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        _flashOn = !_flashOn;
        WindowBackground = _flashOn ? _flashTarget : new SolidColorBrush(Color.Parse("#CC1a1a2e"));
    }

    private void OnEngineStopped()
    {
        if (_viewModel.Engine.Phase == PomodoroPhase.NotStarted)
            StopFlash();
    }
}
