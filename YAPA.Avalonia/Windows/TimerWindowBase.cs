using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly TimerContextMenu _actionsMenu = new();

    protected TimerWindowBase()
    {
        var bs = App.Bootstrapper!;
        ViewModel = bs.Resolve<TimerViewModel>();
        Appearance = bs.Resolve<AvaloniaYapaThemeSettings>();
        General = bs.Resolve<GeneralSettings>();
        Assistant = bs.Resolve<YAPA.Avalonia.Assistant.AssistantSettings>();
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

        _actionsMenu.DataContext = ViewModel;
        _actionsMenu.Opened += (_, _) => RefreshControls();
        _actionsMenu.Closed += (_, _) => RefreshControls();

        _settleTimer = new DispatcherTimer { Interval = SettleDelay };
        _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); OnPositionSettled(); };

        _hideControlsTimer = new DispatcherTimer { Interval = HideControlsDelay };
        _hideControlsTimer.Tick += (_, _) => { _hideControlsTimer.Stop(); RefreshControls(); };

        PositionChanged += (_, _) => { if (!_restoringPosition) _settleTimer.Restart(); };
        Appearance.PropertyChanged += OnThemeSettingChanged;
        General.PropertyChanged += OnGeneralSettingChanged;
        PlacementGuard.ZonesChanged += OnPlacementZonesChanged;

        ApplyBehaviourSettings();
    }

    protected TimerViewModel ViewModel { get; }
    protected AvaloniaYapaThemeSettings Appearance { get; }
    protected GeneralSettings General { get; }
    private YAPA.Avalonia.Assistant.AssistantSettings Assistant { get; }
    private IWindowPlacementGuard PlacementGuard { get; }

    /// <summary>Where the actions menu opens from when invoked by keyboard (the ⋯ button).</summary>
    protected abstract Control MenuAnchor { get; }

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
        TextBrush = new SolidColorBrush(Appearance.TextColor);
        Opacity = Math.Clamp(Appearance.ClockOpacity, 0.2, 1.0);
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
        => ControlsVisible = !Appearance.HideButtons || _pointerInside || _actionsMenu.IsOpen;

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

        var button = e.GetCurrentPoint(this).Properties;
        if (button.IsLeftButtonPressed && e.ClickCount == 2 && Assistant.Enabled)
        {
            // The first press already started a move; with no motion the window manager let
            // go on its release, so this second press arrives here as usual.
            ViewModel.OpenAssistantCommand.Execute(null);
            e.Handled = true;
        }
        else if (button.IsLeftButtonPressed)
        {
            // Controls mark their presses handled, so this only fires on the background.
            // On the press, not the release: once BeginMoveDrag hands the pointer to the
            // window manager the release can't be relied on. So a drag also counts as "seen it".
            ViewModel.StopOverrunCommand.Execute(null);
            BeginMoveDrag(e);
            e.Handled = true;
        }
        else if (button.IsRightButtonPressed)
        {
            // As in the original YAPA: right-click anywhere opens settings.
            // Every other action is on the ⋯ menu, in the tray, or a key away.
            ViewModel.OpenSettingsCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>The ⋯ button (and the Menu key) open the actions menu below it.</summary>
    protected void OpenActionsMenu(Control anchor)
    {
        // Opening a ContextMenu that isn't attached to any control silently does
        // nothing on X11, so attach it to the ⋯ button (right-clicking ⋯ opens it too).
        if (!ReferenceEquals(anchor.ContextMenu, _actionsMenu)) anchor.ContextMenu = _actionsMenu;
        _actionsMenu.Placement = PlacementMode.BottomEdgeAlignedRight;
        _actionsMenu.Open(anchor);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        // "/" opens the assistant, as it opens search in many apps. By symbol, not key, so it
        // works on any keyboard layout.
        if (e.KeySymbol == "/" && !ctrl && Assistant.Enabled)
        {
            ViewModel.OpenAssistantCommand.Execute(null);
            e.Handled = true;
            return;
        }

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
            case Key.L when !ctrl && ViewModel.CanLongBreak:
                ViewModel.LongBreakCommand.Execute(null);
                break;
            case Key.Apps:
            case Key.F10 when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                OpenActionsMenu(MenuAnchor);
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
        if (Appearance.MinimizeToTray) ViewModel.HideToTrayCommand.Execute(null);
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
        // Even when the window lands exactly where it was saved, no
        // PositionChanged fires; check against the dock explicitly.
        Dispatcher.UIThread.Post(() => KeepClearOfShellPanels(), DispatcherPriority.Background);
        // Not OnPositionSettled() here: on X11 Position only updates once the WM
        // confirms the move, and saving now would persist a stale value. The
        // PositionChanged that follows the move triggers the settle check.
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SavePosition();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Appearance.PropertyChanged -= OnThemeSettingChanged;
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

    /// <summary>Called just before every Show.</summary>
    public void ReapplyShellState()
    {
        WindowShellIntegration.Apply(this, General);
        PlacementGuard.Prime(Screens.All.Select(s => s.Bounds).ToList());
    }

    /// <summary>Themes override to apply size, colours and so on.</summary>
    protected virtual void OnAppearanceChanged() { }

    // ── Placement ────────────────────────────────────────────────────────────

    // The dock appeared or changed under us. Moving triggers PositionChanged,
    // and the settle that follows saves the new position.
    private void OnPlacementZonesChanged() => Dispatcher.UIThread.Post(() => KeepClearOfShellPanels());

    private void OnPositionSettled()
    {
        if (!IsVisible) return;
        if (!KeepClearOfShellPanels()) SavePosition();
    }

    /// <returns>True if the window had to move.</returns>
    private bool KeepClearOfShellPanels()
    {
        if (!IsVisible || !General.AvoidShellPanels) return false;

        var bounds = new PixelRect(Position, PixelSize.FromSize(Bounds.Size, DesktopScaling));
        if (PlacementGuard.Resolve(bounds, Screens.All.Select(s => s.Bounds).ToList()) is not { } p || p == Position)
            return false;

        // Not MoveTo: this move should go through the normal settle, which saves it.
        Position = p;
        return true;
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
