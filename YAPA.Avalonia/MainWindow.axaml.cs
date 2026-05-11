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
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── Services ──────────────────────────────────────────────────────────────
    private readonly IMainViewModel _viewModel;
    private readonly SnapshotService _snapshot;
    private readonly ISettings _settings;
    private readonly AvaloniaYapaThemeSettings _themeSettings;

    // ── Flash animation ───────────────────────────────────────────────────────
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _flashOn;
    private IBrush _flashTarget = Brushes.Transparent;

    // ── Hover-hide controls ───────────────────────────────────────────────────
    private CancellationTokenSource _hideCts = new();

    // ── Settings keys ─────────────────────────────────────────────────────────
    private const string KeyLeft = "MainWindow.Left";
    private const string KeyTop  = "MainWindow.Top";

    // ── INPC backing fields ───────────────────────────────────────────────────

    private IBrush _flashBackground = Brushes.Transparent;
    public IBrush FlashBackground
    {
        get => _flashBackground;
        private set { _flashBackground = value; Notify(); }
    }

    private IBrush _textBrush = Brushes.White;
    public IBrush TextBrush
    {
        get => _textBrush;
        private set { _textBrush = value; Notify(); }
    }

    private bool _hundredsVisible;
    public bool HundredsVisible
    {
        get => _hundredsVisible;
        private set { _hundredsVisible = value; Notify(); }
    }

    private string _digitH = "0";
    public string DigitH
    {
        get => _digitH;
        private set { _digitH = value; Notify(); }
    }

    private string _tensMinutes = "0";
    public string TensMinutes
    {
        get => _tensMinutes;
        private set { _tensMinutes = value; Notify(); }
    }

    private string _onesMinutes = "0";
    public string OnesMinutes
    {
        get => _onesMinutes;
        private set { _onesMinutes = value; Notify(); }
    }

    private bool _showSeconds = true;
    public bool ShowSeconds
    {
        get => _showSeconds;
        private set { _showSeconds = value; Notify(); }
    }

    private string _tensSeconds = "0";
    public string TensSeconds
    {
        get => _tensSeconds;
        private set { _tensSeconds = value; Notify(); }
    }

    private string _onesSeconds = "0";
    public string OnesSeconds
    {
        get => _onesSeconds;
        private set { _onesSeconds = value; Notify(); }
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

    private bool _startVisible = true;
    public bool StartVisible
    {
        get => _startVisible;
        private set { _startVisible = value; Notify(); }
    }

    private bool _stopVisible;
    public bool StopVisible
    {
        get => _stopVisible;
        private set { _stopVisible = value; Notify(); }
    }

    private bool _pauseVisible;
    public bool PauseVisible
    {
        get => _pauseVisible;
        private set { _pauseVisible = value; Notify(); }
    }

    private bool _skipVisible;
    public bool SkipVisible
    {
        get => _skipVisible;
        private set { _skipVisible = value; Notify(); }
    }

    private string _counter = "0";
    public string Counter
    {
        get => _counter;
        private set { _counter = value; Notify(); }
    }

    private bool _statusTextVisible = true;
    public bool StatusTextVisible
    {
        get => _statusTextVisible;
        private set { _statusTextVisible = value; Notify(); }
    }

    private double _digitCellWidth = 36;
    public double DigitCellWidth
    {
        get => _digitCellWidth;
        private set { _digitCellWidth = value; Notify(); }
    }

    private global::Avalonia.Media.DropShadowDirectionEffect? _timerShadowEffect;
    public global::Avalonia.Media.DropShadowDirectionEffect? TimerShadowEffect
    {
        get => _timerShadowEffect;
        private set { _timerShadowEffect = value; Notify(); }
    }

    public ICommand StartCommand => _viewModel.StartCommand;
    public ICommand StopCommand  => _viewModel.StopCommand;
    public ICommand PauseCommand => _viewModel.PauseCommand;
    public ICommand SkipCommand  => _viewModel.SkipCommand;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainWindow()
    {
        var bs = App.Bootstrapper!;
        _viewModel    = bs.Resolve<IMainViewModel>();
        _snapshot     = bs.Resolve<SnapshotService>();
        _settings     = bs.Resolve<ISettings>();
        _themeSettings = bs.Resolve<AvaloniaYapaThemeSettings>();

        InitializeComponent();

        // Force transparency properties programmatically so they override theme defaults
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;

        // Set taskbar/dock icon from the same embedded asset used by the tray
        using (var iconStream = AssetLoader.Open(new Uri("avares://YAPA.Avalonia/Assets/pomoTray.ico")))
            Icon = new WindowIcon(iconStream);

        DataContext = this;

        _flashTimer.Tick += OnFlashTick;

        _viewModel.Engine.PropertyChanged += OnEnginePropertyChanged;
        _viewModel.Engine.OnStarted += StopFlash;
        _viewModel.Engine.OnStopped += OnEngineStopped;

        App.ExternalCommandLine += OnExternalCommandLine;

        // Apply all appearance settings immediately, then subscribe for live updates
        ApplyThemeSettings();
        _themeSettings.PropertyChanged += (_, __) => ApplyThemeSettings();

        UpdateDisplay();
        UpdatePhase();
        UpdateStatusText();
        Counter = _viewModel.Engine.Counter.ToString();
    }

    // ── Theme settings ────────────────────────────────────────────────────────

    private void ApplyThemeSettings()
    {
        // Width/Height: maintain original 130:60 aspect ratio
        var w = Math.Max(80, _themeSettings.Width);
        var h = (int)Math.Round(w * 60.0 / 130.0);
        Width  = w;
        Height = h;

        Opacity          = _themeSettings.ClockOpacity;
        TextBrush        = new SolidColorBrush(_themeSettings.TextColor);
        ShowSeconds      = !_themeSettings.HideSeconds;
        StatusTextVisible = _themeSettings.ShowStatusText;

        // HideButtons=true  → hover-reveal (default); HideButtons=false → always visible
        if (!_themeSettings.HideButtons)
            ControlsVisible = true;
        else
            ControlsVisible = false;

        DigitCellWidth = _themeSettings.DigitCellWidth;

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

    // ── Opened / Closing ──────────────────────────────────────────────────────

    private async void OnOpened(object? sender, EventArgs e)
    {
        // Note: we used to stamp _NET_WM_WINDOW_TYPE_UTILITY / _DOCK here to
        // suppress Mutter's compositor shadow on the transparent overlay, but
        // both broke usability: UTILITY caused the WM to vanish the window on
        // focus loss, and DOCK disabled drag-move and right-click. A NORMAL
        // window keeps the small Mutter shadow but everything else works.

        RestoreWindowPosition();

        var args = Environment.GetCommandLineArgs();
        var (shouldAsk, snap) = _snapshot.TryLoadSnapshot(args);
        if (!shouldAsk || snap == null) return;

        var profile = snap.PomodoroProfile;
        var intervalSec = profile?.WorkTime ?? _viewModel.Engine.WorkTime;
        var remaining = TimeSpan.FromSeconds(Math.Max(0, intervalSec - snap.PausedTime));

        var dialog = new Windows.ResumeDialog(remaining);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true)
            _snapshot.ApplySnapshot(snap);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveWindowPosition();
        _flashTimer.Stop();
        App.ExternalCommandLine -= OnExternalCommandLine;
        _viewModel.Engine.PropertyChanged -= OnEnginePropertyChanged;
        _viewModel.Engine.OnStarted -= StopFlash;
        _viewModel.Engine.OnStopped -= OnEngineStopped;
    }

    // ── Pointer events ────────────────────────────────────────────────────────

    private void OnPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
        else if (props.IsRightButtonPressed)
        {
            _viewModel.ShowSettingsCommand?.Execute(null);
        }
    }

    private void OnPointerEntered(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {
        // Only show controls on hover when HideButtons=true (hover-reveal mode)
        if (!_themeSettings.HideButtons) return;
        _hideCts.Cancel();
        _hideCts = new CancellationTokenSource();
        ControlsVisible = true;
    }

    private async void OnPointerExited(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {
        // Only auto-hide controls when in hover-reveal mode
        if (!_themeSettings.HideButtons) return;
        var cts = _hideCts;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            ControlsVisible = false;
        }
        catch (OperationCanceledException) { }
    }

    // ── Button handlers ───────────────────────────────────────────────────────

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
                UpdateDisplay();
                break;
            case nameof(IPomodoroEngine.Phase):
                UpdatePhase();
                UpdateStatusText();
                StartFlash();
                break;
            case nameof(IPomodoroEngine.Counter):
                Counter = _viewModel.Engine.Counter.ToString();
                break;
        }
    }

    // ── Display update ────────────────────────────────────────────────────────

    private void UpdateDisplay()
    {
        var total = _viewModel.Engine.DisplayValue;
        var minutes = total / 60;
        var seconds  = total % 60;

        var h  = minutes / 100;
        var tm = minutes / 10 % 10;
        var om = minutes % 10;
        var ts = seconds / 10;
        var os = seconds % 10;

        HundredsVisible = h > 0;
        DigitH       = h.ToString();
        TensMinutes  = tm.ToString();
        OnesMinutes  = om.ToString();
        TensSeconds  = ts.ToString();
        OnesSeconds  = os.ToString();

        if (!ShowSeconds && minutes == 0 && seconds > 0)
        {
            TensMinutes = "<";
            OnesMinutes = "1";
        }
    }

    // ── Phase update ──────────────────────────────────────────────────────────

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

    private void UpdateStatusText()
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
        else
            return;

        _flashOn = false;
        _flashTimer.Start();
    }

    private void StopFlash()
    {
        _flashTimer.Stop();
        _flashOn = false;
        FlashBackground = Brushes.Transparent;
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        _flashOn = !_flashOn;
        FlashBackground = _flashOn ? _flashTarget : Brushes.Transparent;
    }

    private void OnEngineStopped()
    {
        // Stopping from Work/Pause lands at WorkEnded — flash should keep running there.
        // Stopping from Break/WorkEnded lands at NotStarted — clear the flash.
        if (_viewModel.Engine.Phase == PomodoroPhase.NotStarted)
            StopFlash();
    }

    // ── External command-line (second instance) ───────────────────────────────

    private void OnExternalCommandLine(string[] args)
    {
        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case Specifics.CommandLineConstants.Start:    _viewModel.StartCommand.Execute(null); break;
                case Specifics.CommandLineConstants.Stop:     _viewModel.StopCommand.Execute(null);  break;
                case Specifics.CommandLineConstants.Pause:    _viewModel.PauseCommand.Execute(null); break;
                case Specifics.CommandLineConstants.Reset:    _viewModel.ResetCommand.Execute(null); break;
                case Specifics.CommandLineConstants.Skip:     _viewModel.SkipCommand.Execute(null);  break;
                case Specifics.CommandLineConstants.Settings: _viewModel.ShowSettingsCommand.Execute(null); break;
            }
        }
    }

    // ── Window position ───────────────────────────────────────────────────────

    private void RestoreWindowPosition()
    {
        try
        {
            var left = _settings.Get(KeyLeft, double.NaN, "MainWindow", false);
            var top  = _settings.Get(KeyTop,  double.NaN, "MainWindow", false);
            if (!double.IsNaN(left) && !double.IsNaN(top))
                Position = new global::Avalonia.PixelPoint((int)left, (int)top);
        }
        catch { /* ignore */ }
    }

    private void SaveWindowPosition()
    {
        try
        {
            _settings.Update(KeyLeft, (double)Position.X, "MainWindow", false);
            _settings.Update(KeyTop,  (double)Position.Y, "MainWindow", false);
            _settings.Save();
        }
        catch { /* ignore */ }
    }
}
