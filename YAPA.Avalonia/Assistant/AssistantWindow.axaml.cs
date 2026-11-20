using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using YAPA.Avalonia.Platform.Linux.X11;
using YAPA.Avalonia.Specifics;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// The assistant panel: a card that opens beside the timer, takes a request, and closes
/// when you click elsewhere or press Escape. Closing keeps the conversation for a while
/// (<see cref="AssistantService.KeepAlive"/>), so reopening carries on where it was.
/// </summary>
public partial class AssistantWindow : Window
{
    private const double CardWidth = 380;
    private const double Gutter = 20; // around the card: its shadow, and room to grow
    private const double Gap = 8;     // between the timer and the card

    private static readonly TimeSpan HideFade = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan SettleAfterShow = TimeSpan.FromMilliseconds(400);

    private readonly AssistantViewModel _vm;
    private readonly GeneralSettings _general;
    private readonly DispatcherTimer _hideTimer;
    private DateTime _shownAt;
    private readonly System.Collections.Generic.List<string> _asked = [];
    private int _recalled = -1; // where Up has got to in _asked; -1 when not walking it
    private bool _following = true; // the reader is at the end of the conversation: new text keeps it in view
    private (int, int, int, int)? _inputRegion;
    private YAPA.Avalonia.Platform.Linux.X11.X11PointerWatch? _outside;

    /// <summary>The card, in screen pixels: a press anywhere else closes the panel.</summary>
    private PixelRect CardOnScreen()
    {
        var s = RenderScaling;
        var b = Card.Bounds;
        return new PixelRect(Position.X + (int)(b.X * s), Position.Y + (int)(b.Y * s), (int)(b.Width * s), (int)(b.Height * s));
    }

    public AssistantWindow() : this(App.Bootstrapper!.Resolve<AssistantViewModel>(), App.Bootstrapper!.Resolve<GeneralSettings>())
    {
    }

    public AssistantWindow(AssistantViewModel vm, GeneralSettings general)
    {
        _vm = vm;
        _general = general;
        InitializeComponent();
        DataContext = vm;

        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        using (var icon = AssetLoader.Open(new Uri("avares://YAPA.Avalonia/Assets/pomoTray.ico")))
            Icon = new WindowIcon(icon);

        _hideTimer = new DispatcherTimer { Interval = HideFade };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
            _vm.Hidden();
        };

        vm.ActionCompleted += () => Orb.Pulse();
        vm.Spoke += () => Orb.Nudge(0.16);
        vm.Answered += () => Orb.Pulse();
        // Streaming text keeps the end in view only while the reader is there; scrolled up to
        // read something earlier, they stay put. A new question always goes to the end.
        vm.ContentGrew += () =>
        {
            if (_following) Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        };
        vm.Asked += () =>
        {
            _following = true;
            Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        };

        Deactivated += OnDeactivated;
        // Typing always goes to the prompt, wherever focus is (after clicking Undo, say).
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        Prompt.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        Prompt.TextChanged += (_, _) => Orb.Nudge();
        Prompt.GotFocus += (_, _) => Field.Classes.Set("focused", true);
        Prompt.LostFocus += (_, _) => Field.Classes.Set("focused", false);
        Scroller.ScrollChanged += (_, e) =>
        {
            Scroller.Classes.Set("scrolled", Scroller.Offset.Y > 1);
            Scroller.Classes.Set("more", Scroller.Offset.Y < Scroller.Extent.Height - Scroller.Viewport.Height - 1);
            // Only a move of the reader's own counts: content growing under them changes the extent, not the offset.
            if (e.OffsetDelta.Y != 0)
                _following = Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 24;
            JumpButton.Classes.Set("shown", !_following && Scroller.Classes.Contains("more"));
        };
        Card.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) UpdateInputRegion();
        };
    }

    public bool IsShowing => IsVisible && !_hideTimer.IsEnabled;

    /// <summary>Opens the panel beside <paramref name="anchor"/> (the timer), or near the top of the screen without one.</summary>
    public void ShowNear(Window? anchor)
    {
        _hideTimer.Stop();
        if (anchor is { IsVisible: true, WindowState: not WindowState.Minimized }) Place(anchor);
        else Place(null);

        if (!IsVisible)
        {
            // Mutter drops these when a window is withdrawn; set them again before it maps.
            ApplyShellState();
            Show();
        }
        _shownAt = DateTime.UtcNow;
        Orb.Resume();
        _outside ??= YAPA.Avalonia.Platform.Linux.X11.X11PointerWatch.TryCreate();
        _outside?.Start(CardOnScreen, () => Dispatcher.UIThread.Post(Dismiss));
        Activate();
        Card.Classes.Set("shown", true);
        _vm.Opened();
        Dispatcher.UIThread.Post(() => Prompt.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Fades the card out, then hides the window.</summary>
    public void Dismiss()
    {
        _outside?.Stop();
        if (!IsVisible || _hideTimer.IsEnabled) return;
        Card.Classes.Set("shown", false);
        _hideTimer.Start();
    }

    private void Place(Window? anchor)
    {
        var screen = (anchor != null ? Screens.ScreenFromWindow(anchor) : null) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var scale = screen?.Scaling ?? 1;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int Px(double dip) => (int)Math.Round(dip * scale);

        bool above;
        double cardMax;
        int x, y;
        if (anchor != null)
        {
            var a = new PixelRect(anchor.Position, PixelSize.FromSize(anchor.Bounds.Size, anchor.DesktopScaling));
            var spaceBelow = work.Bottom - (a.Bottom + Px(Gap));
            var spaceAbove = a.Y - Px(Gap) - work.Y;
            // Below reads naturally, as if it came out of the timer; above only when there's no room.
            above = spaceBelow < Px(340) && spaceAbove > spaceBelow;
            cardMax = Math.Clamp((above ? spaceAbove : spaceBelow) / scale, 220, 560);
            // Centred on the timer, kept on the screen.
            x = a.X + a.Width / 2 - Px(CardWidth) / 2;
            x = Math.Clamp(x, work.X + Px(8), Math.Max(work.X + Px(8), work.Right - Px(CardWidth) - Px(8)));
            y = above ? a.Y - Px(Gap) - Px(cardMax) : a.Bottom + Px(Gap);
        }
        else
        {
            above = false;
            cardMax = Math.Min(520, work.Height / scale * 0.6);
            x = work.X + (work.Width - Px(CardWidth)) / 2;
            y = work.Y + work.Height / 5;
        }

        Width = CardWidth + 2 * Gutter;
        Height = cardMax + 2 * Gutter;
        // The conversation's row takes what the rest leaves, so the prompt can grow without pushing out of the card.
        Card.MaxHeight = cardMax;
        Card.VerticalAlignment = above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Card.Classes.Set("above", above);
        Position = new PixelPoint(x - Px(Gutter), y - Px(Gutter));
    }

    private void ApplyShellState()
    {
        if (TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle || handle.Handle == IntPtr.Zero) return;
        var xid = handle.Handle;
        X11Window.SuppressCompositorShadow(xid);
        X11Window.SetAbove(xid, true);
        X11Window.SetSkipTaskbar(xid, true);
        X11Window.SetOnAllWorkspaces(xid, _general.ShowOnAllWorkspaces);
        _inputRegion = null; // the server forgets nothing, but re-sending after a map is cheap insurance
        UpdateInputRegion();
    }

    /// <summary>Clicks in the transparent margin go to whatever is underneath.</summary>
    private void UpdateInputRegion()
    {
        if (TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle || handle.Handle == IntPtr.Zero) return;
        var bounds = Card.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var s = RenderScaling;
        var region = ((int)Math.Floor(bounds.X * s), (int)Math.Floor(bounds.Y * s), (int)Math.Ceiling(bounds.Width * s), (int)Math.Ceiling(bounds.Height * s));
        if (_inputRegion == region) return;
        _inputRegion = region;
        X11Window.SetInputRegion(handle.Handle, region);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // The window manager can bounce focus while mapping a window; only a deliberate click
        // elsewhere, after the panel has settled, closes it.
        if (DateTime.UtcNow - _shownAt > SettleAfterShow) Dismiss();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The window manager (Alt+F4) closes it like Escape does; the app keeps the window.
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            Dismiss();
        }
        base.OnClosing(e);
    }

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter or Key.Return when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Remember(_vm.Input);
                _vm.SubmitCommand.Execute(null);
                e.Handled = true;
                break;
            // Like a shell: earlier requests back, to fix or repeat. Only from an empty prompt or one
            // Up filled in, and only on its first line, so Up still moves the caret in a long prompt.
            case Key.Up when _asked.Count > 0 && Recalling() && OnFirstLine():
                _recalled = _recalled < 0 ? _asked.Count - 1 : Math.Max(0, _recalled - 1);
                Recall(_asked[_recalled]);
                e.Handled = true;
                break;
            case Key.Down when _recalled >= 0 && Recalling():
                _recalled++;
                Recall(_recalled < _asked.Count ? _asked[_recalled] : "");
                if (_recalled >= _asked.Count) _recalled = -1;
                e.Handled = true;
                break;
            case Key.N when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _vm.NewConversationCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                Dismiss();
                e.Handled = true;
                break;
        }
    }

    private bool Recalling() => string.IsNullOrEmpty(_vm.Input) || _recalled >= 0 && _vm.Input == _asked[_recalled];

    private bool OnFirstLine() => !(_vm.Input ?? "")[..Math.Min(Prompt.CaretIndex, (_vm.Input ?? "").Length)].Contains('\n');

    private void Recall(string text)
    {
        _vm.Input = text;
        // After the binding has put the text in: setting it now would be undone by that.
        Dispatcher.UIThread.Post(() => Prompt.CaretIndex = Prompt.Text?.Length ?? 0, DispatcherPriority.Background);
    }

    private void Remember(string? question)
    {
        _recalled = -1;
        var q = question?.Trim();
        if (string.IsNullOrEmpty(q)) return;
        _asked.Remove(q);
        _asked.Add(q);
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (Prompt.IsFocused || string.IsNullOrEmpty(e.Text) || e.Source is SelectableTextBlock) return;
        Prompt.Focus();
        _vm.Input += e.Text;
        Prompt.CaretIndex = _vm.Input.Length;
        e.Handled = true;
    }

    private void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Suggestion suggestion })
        {
            if (_vm.UseSuggestion(suggestion)) Remember(suggestion.Prompt);
            Prompt.Focus();
            // A filled-in prompt: select the part most likely to change, the number.
            if (!suggestion.Send && System.Text.RegularExpressions.Regex.Match(_vm.Input, @"\d+|an hour") is { Success: true } m)
            {
                Prompt.SelectionStart = m.Index;
                Prompt.SelectionEnd = m.Index + m.Length;
            }
            else
            {
                Prompt.CaretIndex = _vm.Input.Length;
            }
        }
    }

    private void OnNewConversationClick(object? sender, RoutedEventArgs e) => Prompt.Focus();

    private void OnJumpClick(object? sender, RoutedEventArgs e)
    {
        _following = true;
        JumpButton.Classes.Set("shown", false);
        Scroller.ScrollToEnd();
        Prompt.Focus();
    }

    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Exchange exchange }) _vm.RetryCommand.Execute(exchange);
        Prompt.Focus();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Exchange exchange } || Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(exchange.Answer);
        exchange.Copied = true;
        Prompt.Focus();
        // A tick for a moment, then the copy icon again.
        DispatcherTimer.RunOnce(() => exchange.Copied = false, TimeSpan.FromSeconds(1.6));
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Exchange exchange }) _vm.UndoCommand.Execute(exchange);
        Prompt.Focus();
    }
}
