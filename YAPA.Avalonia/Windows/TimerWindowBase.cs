using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.ViewModels;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Windows;

/// <summary>
/// Behaviour shared by every timer theme: a borderless, transparent, always-on-top
/// widget you drag from anywhere, that remembers where you left it, stays out of
/// the shell's click-eating panels, and reveals its controls on hover.
/// Themes only supply layout (XAML) and size.
/// </summary>
public abstract class TimerWindowBase : Window
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan HideControlsDelay = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherTimer _settleTimer;
    private readonly DispatcherTimer _hideControlsTimer;
    private readonly ISettings _settings;
    private bool _pointerInside;
    private bool _restoringPosition;
    private bool _menuHooked; // Opened fires on every Show after a Hide

    protected TimerWindowBase()
    {
        var bs = App.Bootstrapper!;
        ViewModel = bs.Resolve<TimerViewModel>();
        Theme = bs.Resolve<AvaloniaYapaThemeSettings>();
        General = bs.Resolve<GeneralSettings>();
        _settings = bs.Resolve<ISettings>();
        PlacementGuard = bs.Resolve<IWindowPlacementGuard>();
        DataContext = ViewModel;

        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "YAPA 2";

        using (var icon = AssetLoader.Open(new Uri("avares://YAPA.Avalonia/Assets/pomoTray.ico")))
            Icon = new WindowIcon(icon);

        _settleTimer = new DispatcherTimer { Interval = SettleDelay };
        _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); OnPositionSettled(); };

        _hideControlsTimer = new DispatcherTimer { Interval = HideControlsDelay };
        _hideControlsTimer.Tick += (_, _) => { _hideControlsTimer.Stop(); RefreshControls(); };

        PositionChanged += (_, _) => { if (!_restoringPosition) _settleTimer.Restart(); };
        Theme.PropertyChanged += OnThemeSettingChanged;
        General.PropertyChanged += OnGeneralSettingChanged;
        PlacementGuard.ZonesChanged += OnPlacementZonesChanged;

        ApplyBehaviourSettings();
    }

    protected TimerViewModel ViewModel { get; }
    protected AvaloniaYapaThemeSettings Theme { get; }
    protected GeneralSettings General { get; }
    private IWindowPlacementGuard PlacementGuard { get; }

    /// <summary>Settings component that stores this theme's position.</summary>
    protected abstract string PositionKey { get; }

    // ── Appearance ───────────────────────────────────────────────────────────

    public static readonly StyledProperty<IBrush> TextBrushProperty =
        AvaloniaProperty.Register<TimerWindowBase, IBrush>(nameof(TextBrush), Brushes.White);

    public IBrush TextBrush
    {
        get => GetValue(TextBrushProperty);
        private set => SetValue(TextBrushProperty, value);
    }

    private void ApplyCommonAppearance()
    {
        TextBrush = new SolidColorBrush(Theme.TextColor);
        Opacity = Math.Clamp(Theme.ClockOpacity, 0.2, 1.0);
        OnAppearanceChanged();
    }

    // ── Controls reveal ──────────────────────────────────────────────────────

    public static readonly StyledProperty<bool> ControlsVisibleProperty =
        AvaloniaProperty.Register<TimerWindowBase, bool>(nameof(ControlsVisible), defaultValue: true);

    /// <summary>Bound by themes to show or hide the button column.</summary>
    public bool ControlsVisible
    {
        get => GetValue(ControlsVisibleProperty);
        private set => SetValue(ControlsVisibleProperty, value);
    }

    protected void RefreshControls()
        => ControlsVisible = !Theme.HideButtons || _pointerInside || ContextMenu?.IsOpen == true;

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _pointerInside = true;
        _hideControlsTimer.Stop();
        RefreshControls();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerInside = false;
        // Linger briefly so the buttons don't vanish as the pointer crosses the edge.
        _hideControlsTimer.Restart();
    }

    // ── Input ────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled) return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // Controls mark their presses handled, so this only fires on the background.
            BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Space:
                ViewModel.ToggleCommand.Execute(null);
                break;
            case Key.Escape when ViewModel.CanStop:
                ViewModel.StopCommand.Execute(null);
                break;
            case Key.S when !ctrl && ViewModel.CanSkip:
                ViewModel.SkipCommand.Execute(null);
                break;
            case Key.OemComma when ctrl:
                ViewModel.OpenSettingsCommand.Execute(null);
                break;
            case Key.I when ctrl:
                ViewModel.OpenStatisticsCommand.Execute(null);
                break;
            case Key.H when ctrl:
                ViewModel.HideToTrayCommand.Execute(null);
                break;
            case Key.Q when ctrl:
                ViewModel.QuitCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>The "_" button: tray when there is one, otherwise a normal minimize.</summary>
    protected void MinimizeOrHide()
    {
        if (Theme.MinimizeToTray) ViewModel.HideToTrayCommand.Execute(null);
        else WindowState = WindowState.Minimized;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Size first: placement clamps against the real size, not the XAML default.
        ApplyCommonAppearance();
        RestorePosition();
        ApplyBehaviourSettings();
        RefreshControls();
        // Not OnPositionSettled() here: on X11 Position only updates once the WM
        // confirms the move, and saving now would persist a stale value. The
        // PositionChanged that follows the move triggers the settle check.
        if (!_menuHooked && ContextMenu is { } menu)
        {
            _menuHooked = true;
            menu.Opened += (_, _) => RefreshControls();
            menu.Closed += (_, _) => RefreshControls();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SavePosition();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Theme.PropertyChanged -= OnThemeSettingChanged;
        General.PropertyChanged -= OnGeneralSettingChanged;
        PlacementGuard.ZonesChanged -= OnPlacementZonesChanged;
        _settleTimer.Stop();
        _hideControlsTimer.Stop();
        base.OnClosed(e);
    }

    private void OnThemeSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AvaloniaYapaThemeSettings.HideButtons)) RefreshControls();
        ApplyCommonAppearance();
    }

    private void OnGeneralSettingChanged(object? sender, PropertyChangedEventArgs e) => ApplyBehaviourSettings();

    /// <summary>Re-applies window-manager state. Called on open and whenever settings change.</summary>
    private void ApplyBehaviourSettings()
    {
        ShowInTaskbar = General.ShowInTaskbar;
        // Avalonia loses _NET_WM_STATE_ABOVE across Hide/Show on X11 (AvaloniaUI #16115);
        // toggling forces it to send the state again.
        Topmost = false;
        Topmost = General.AlwaysOnTop;
        WindowShellIntegration.Apply(this, General);
    }

    /// <summary>Themes override to apply size, colours and so on.</summary>
    protected virtual void OnAppearanceChanged() { }

    // ── Placement ────────────────────────────────────────────────────────────

    private void OnPlacementZonesChanged() => Dispatcher.UIThread.Post(OnPositionSettled);

    private void OnPositionSettled()
    {
        if (!IsVisible) return;

        if (General.AvoidShellPanels)
        {
            var bounds = new PixelRect(Position, PixelSize.FromSize(Bounds.Size, DesktopScaling));
            var nudged = PlacementGuard.Resolve(bounds, Screens.All.Select(s => s.Bounds).ToList());
            if (nudged is { } p && p != Position)
            {
                MoveTo(p);
            }
        }

        SavePosition();
    }

    private void MoveTo(PixelPoint position)
    {
        _restoringPosition = true;
        try { Position = position; }
        finally { _restoringPosition = false; }
    }

    private void RestorePosition()
    {
        var left = _settings.Get(PositionKey + ".Left", double.NaN, PositionKey, false);
        var top = _settings.Get(PositionKey + ".Top", double.NaN, PositionKey, false);
        var size = PixelSize.FromSize(new Size(Width, Height), DesktopScaling);
        var screens = Screens.All;

        PixelPoint? saved = double.IsNaN(left) || double.IsNaN(top)
            ? null
            : new PixelPoint((int)left, (int)top);

        MoveTo(WindowPlacement.Restore(saved, size, screens.Select(s => s.WorkingArea).ToList(),
            (Screens.Primary ?? screens.FirstOrDefault())?.WorkingArea));
    }

    private void SavePosition()
    {
        _settings.Update(PositionKey + ".Left", (double)Position.X, PositionKey, false);
        _settings.Update(PositionKey + ".Top", (double)Position.Y, PositionKey, false);
    }
}

internal static class DispatcherTimerExtensions
{
    public static void Restart(this DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }
}
