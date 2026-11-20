using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.ViewModels;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// The command bar: one field that runs the app's actions and asks YAPA. Before a conversation,
/// what's typed filters the actions (with asking always on offer); once there is one, Enter
/// replies and "/" brings the actions back. It opens beside the timer, or over the hub when
/// summoned there, and closes when you click elsewhere or press Escape. Closing keeps the
/// conversation for a while (<see cref="AssistantService.KeepAlive"/>), so reopening carries on.
/// </summary>
public partial class AssistantWindow : Window
{
    private const double CardWidth = 460;
    private const double Gutter = 20; // around the card: its shadow, and room to grow
    private const double Gap = 8;     // between the timer and the card

    private static readonly TimeSpan HideFade = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan SettleAfterShow = TimeSpan.FromMilliseconds(400);

    private readonly AssistantViewModel _vm;
    private readonly GeneralSettings _general;
    private readonly DispatcherTimer _hideTimer;
    private DateTime _shownAt;
    private readonly List<string> _asked = [];
    private int _recalled = -1; // where Up has got to in _asked; -1 when not walking it
    private bool _following = true; // the reader is at the end of the conversation: new text keeps it in view
    private (int, int, int, int)? _inputRegion;
    private X11PointerWatch? _outside;

    private readonly ObservableCollection<PaletteRow> _rows = [];
    private int _selected;
    private bool _paletteShown;

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
        Card.Width = CardWidth;
        List.ItemsSource = _rows;

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
        vm.PropertyChanged += OnViewModelChanged;
        // Suggestions change after the conversation does (a new one, say): once both have, list again.
        vm.Suggestions.CollectionChanged += (_, _) => RefreshSoon();
        // The timer can move on while the bar is open (an interval ends): Pause becomes Start break.
        if (Timer is { } timer)
            timer.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TimerViewModel.Phase) && IsShowing) RefreshSoon();
            };

        Deactivated += OnDeactivated;
        // Typing always goes to the field, wherever focus is (after clicking Undo, say).
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        Prompt.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        Prompt.TextChanged += (_, _) => Orb.Nudge();
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
        Refresh();
    }

    public bool IsShowing => IsVisible && !_hideTimer.IsEnabled;

    private static AppNavigator? Navigator => App.Bootstrapper?.Resolve<AppNavigator>();

    private static TimerViewModel? Timer => App.Bootstrapper?.Resolve<TimerViewModel>();

    /// <summary>Opens the panel beside <paramref name="anchor"/> (the timer), or near the top of the screen without one.</summary>
    public void ShowNear(Window? anchor)
    {
        _hideTimer.Stop();
        if (anchor is { IsVisible: true, WindowState: not WindowState.Minimized }) PlaceBeside(anchor);
        else PlaceOver(null);
        Present();
    }

    /// <summary>Opens the panel over <paramref name="window"/>, near its top, as a command palette opens over an app.</summary>
    public void ShowOver(Window window)
    {
        _hideTimer.Stop();
        PlaceOver(window);
        Present();
    }

    private void Present()
    {
        if (!IsVisible)
        {
            // Mutter drops these when a window is withdrawn; set them again before it maps.
            ApplyShellState();
            Show();
        }
        _shownAt = DateTime.UtcNow;
        Orb.Resume();
        _outside ??= X11PointerWatch.TryCreate();
        _outside?.Start(CardOnScreen, () => Dispatcher.UIThread.Post(Dismiss));
        Activate();
        Card.Classes.Set("shown", true);
        _vm.Opened();
        _listedFor = null; // a fresh opening starts at the top
        Refresh();
        Dispatcher.UIThread.Post(() => Prompt.Focus(), DispatcherPriority.Input);
        // Above the timer the card grows upwards, from its bottom edge: as the list narrowed to
        // what's typed, the field would move down under the caret. It keeps its opening height.
        if (Card.VerticalAlignment == VerticalAlignment.Bottom)
            Dispatcher.UIThread.Post(() => Card.MinHeight = Card.Bounds.Height, DispatcherPriority.Background);
    }

    /// <summary>Fades the card out, then hides the window.</summary>
    public void Dismiss()
    {
        _outside?.Stop();
        if (!IsVisible || _hideTimer.IsEnabled) return;
        Card.Classes.Set("shown", false);
        _hideTimer.Start();
    }

    private void PlaceBeside(Window anchor)
    {
        var screen = Screens.ScreenFromWindow(anchor) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var scale = screen?.Scaling ?? 1;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int Px(double dip) => (int)Math.Round(dip * scale);

        var a = new PixelRect(anchor.Position, PixelSize.FromSize(anchor.Bounds.Size, anchor.DesktopScaling));
        var spaceBelow = work.Bottom - (a.Bottom + Px(Gap));
        var spaceAbove = a.Y - Px(Gap) - work.Y;
        // Below reads naturally, as if it came out of the timer; above only when there's no room.
        var above = spaceBelow < Px(340) && spaceAbove > spaceBelow;
        var cardMax = Math.Clamp((above ? spaceAbove : spaceBelow) / scale, 220, 560);
        // Centred on the timer, kept on the screen.
        var x = a.X + a.Width / 2 - Px(CardWidth) / 2;
        x = Math.Clamp(x, work.X + Px(8), Math.Max(work.X + Px(8), work.Right - Px(CardWidth) - Px(8)));
        var y = above ? a.Y - Px(Gap) - Px(cardMax) : a.Bottom + Px(Gap);
        Fit(scale, cardMax, above, new PixelPoint(x, y));
    }

    /// <summary>Centred on <paramref name="window"/> (or the primary screen), a fifth of the way down.</summary>
    private void PlaceOver(Window? window)
    {
        var screen = (window != null ? Screens.ScreenFromWindow(window) : null) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var scale = screen?.Scaling ?? 1;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int Px(double dip) => (int)Math.Round(dip * scale);

        var area = window != null
            ? new PixelRect(window.Position, PixelSize.FromSize(window.Bounds.Size, window.DesktopScaling)).Intersect(work)
            : work;
        if (area.Width < Px(CardWidth) || area.Height < Px(240)) area = work;
        var top = area.Y + Math.Max(Px(56), area.Height / 7);
        var cardMax = Math.Clamp((area.Bottom - top - Px(24)) / scale, 220, 540);
        var x = area.X + (area.Width - Px(CardWidth)) / 2;
        Fit(scale, cardMax, false, new PixelPoint(x, top));
    }

    private void Fit(double scale, double cardMax, bool above, PixelPoint cardAt)
    {
        int Px(double dip) => (int)Math.Round(dip * scale);
        Width = CardWidth + 2 * Gutter;
        Height = cardMax + 2 * Gutter;
        // The body's row takes what the rest leaves, so the field can grow without pushing out of the card.
        Card.MaxHeight = cardMax;
        Card.MinHeight = 0;
        Card.VerticalAlignment = above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Card.Classes.Set("above", above);
        Position = new PixelPoint(cardAt.X - Px(Gutter), cardAt.Y - Px(Gutter));
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

    // ── The list ─────────────────────────────────────────────────────────────

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantViewModel.Input) or nameof(AssistantViewModel.HasConversation)
            or nameof(AssistantViewModel.ShowPrompt) or nameof(AssistantViewModel.IsBusy) or nameof(AssistantViewModel.IsUnavailable))
            Refresh();
    }

    /// <summary>"/" in a conversation: the actions, without leaving it.</summary>
    private bool Slashed => _vm.HasConversation && (_vm.Input ?? "").StartsWith('/');

    /// <summary>
    /// Rebuilds what's under the field from what's typed: the actions that match (or, before
    /// anything is typed, suggestions and the likeliest actions), or the conversation.
    /// </summary>
    private void Refresh()
    {
        _refreshPosted = false;
        var input = _vm.Input ?? "";
        var palette = !_vm.HasConversation || Slashed;
        _rows.Clear();
        if (palette && Navigator is { } navigator && Timer is { } timer)
        {
            var canAsk = _vm.ShowPrompt;
            var query = Slashed ? input[1..] : input;
            var actions = PaletteActions.Build(timer, navigator, navigator.IsTimerShowing);
            var typed = query.Trim().Length > 0;
            IReadOnlyList<PaletteCommand> commands = typed
                ? CommandPalette.Rank(query, actions, canAsk && !Slashed ? AskTyped : null)
                : PaletteActions.Opening(canAsk && !Slashed ? _vm.Suggestions.Where(q => !q.Acts).Select(AsCommand) : [], actions);
            string? group = null;
            foreach (var c in commands)
            {
                // Headings only over the opening list: a filtered list reads best as one ranked run.
                var header = !typed && c.Group != group ? c.Group : null;
                group = c.Group;
                _rows.Add(new PaletteRow(c, header));
            }
        }
        _paletteShown = palette;
        // Same text, new rows (the timer moved on): the selection stays on the same action if it's
        // still there. New text starts again from the best match.
        var keep = input == _listedFor ? _rows.ToList().FindIndex(r => r.Title == _selectedTitle) : -1;
        _selected = Math.Max(0, keep);
        _listedFor = input;
        if (_rows.Count > 0) _rows[_selected].IsSelected = true;
        _selectedTitle = _rows.Count > 0 ? _rows[_selected].Title : null;

        ListScroller.IsVisible = palette;
        if (_selected == 0) ListScroller.Offset = default;
        Conversation.IsVisible = !palette;
        Body.IsVisible = palette ? _rows.Count > 0 : _vm.HasConversation;
        SendButton.IsVisible = !palette && !_vm.IsBusy;
        Prompt.PlaceholderText = _vm.HasConversation ? "Reply, or type / for actions"
            : _vm.ShowPrompt ? "Ask YAPA, or type a command…" : "Type a command…";
        UpdateHints();
    }

    private bool _refreshPosted;
    private string? _listedFor;
    private string? _selectedTitle;

    /// <summary>One refresh for a burst of changes (suggestions arrive one item at a time).</summary>
    private void RefreshSoon()
    {
        if (_refreshPosted) return;
        _refreshPosted = true;
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private PaletteCommand AskTyped(string text)
        => new(text, "", PaletteActions.Icons.Spark, Send, PaletteActions.AskGroup, Ask: true, Stays: true);

    private PaletteCommand AsCommand(Suggestion s)
        => new(s.Label, s.Prompt, PaletteActions.Icons.Spark, () => UseSuggestion(s), PaletteActions.AskGroup, Question: true, Stays: true);

    private void Select(int index, bool reveal = true)
    {
        if (_rows.Count == 0) return;
        index = (index % _rows.Count + _rows.Count) % _rows.Count;
        if (index == _selected && _rows[index].IsSelected) return;
        if (_selected < _rows.Count) _rows[_selected].IsSelected = false;
        _selected = index;
        _rows[index].IsSelected = true;
        _selectedTitle = _rows[index].Title;
        if (reveal) List.ContainerFromIndex(index)?.BringIntoView();
        UpdateHints();
    }

    private void Run(PaletteRow row)
    {
        var command = row.Command;
        if (!command.Stays)
        {
            _vm.Input = "";
            Dismiss();
        }
        else if (Slashed)
        {
            _vm.Input = "";
        }
        command.Run();
    }

    private void Send()
    {
        if (!_vm.ShowPrompt) return;
        Remember(_vm.Input);
        _vm.SubmitCommand.Execute(null);
    }

    private void OnRowPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: PaletteRow row }) Select(_rows.IndexOf(row), reveal: false);
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && sender is Control { DataContext: PaletteRow row }) Run(row);
    }

    /// <summary>The foot's keys: the ones that do something right now.</summary>
    private void UpdateHints()
    {
        Hints.Children.Clear();
        void Keycap(params string[] keys)
        {
            foreach (var k in keys) Hints.Children.Add(new Border { Classes = { "kbd" }, Child = new TextBlock { Text = k } });
        }
        void Label(string text, bool last = false)
            => Hints.Children.Add(new TextBlock { Text = text, Margin = new Thickness(1, 0, last ? 0 : 9, 0) });

        if (_paletteShown && _rows.Count > 0)
        {
            Keycap("↑", "↓");
            Label("Select");
            Keycap("↵");
            var row = _rows[Math.Min(_selected, _rows.Count - 1)];
            Label(row.IsAsk || row.IsQuestion ? "Ask" : "Run");
            Keycap("esc");
            Label((_vm.Input ?? "").Length > 0 ? "Clear" : "Close", last: true);
        }
        else if (Slashed)
        {
            // "/" and nothing matches: nothing to run, and Enter won't send it.
            Label("No action by that name");
            Keycap("esc");
            Label("Clear", last: true);
        }
        else if (_vm.HasConversation)
        {
            Keycap("↵");
            Label("Send");
            Keycap("/");
            Label("Actions");
            Keycap("ctrl", "N");
            Label("New", last: true);
        }
        // One line beside the keys; a message that matters (Claude Code missing) gets the foot to itself.
        if (_vm.IsUnavailable) Hints.Children.Clear();
        var wrap = Hints.Children.Count == 0;
        CaptionText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        CaptionText.TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;
    }

    // ── The field ────────────────────────────────────────────────────────────

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        var listed = _paletteShown && _rows.Count > 0;
        switch (e.Key)
        {
            case Key.Enter or Key.Return when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (listed) Run(_rows[_selected]);
                // "/" that matches nothing is a typo, not a question: sending it would spend a request.
                else if (_vm.HasConversation && !Slashed) Send();
                e.Handled = true;
                break;
            case Key.Down when listed:
                Select(_selected + 1);
                e.Handled = true;
                break;
            case Key.Up when listed:
                Select(_selected - 1);
                e.Handled = true;
                break;
            // Like a shell: earlier requests back, to fix or repeat. Only from an empty field or one
            // Up filled in, and only on its first line, so Up still moves the caret in a long one.
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
                // As in any palette: Escape clears what's typed first, then closes.
                if (!string.IsNullOrEmpty(_vm.Input) && _paletteShown) _vm.Input = "";
                else Dismiss();
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

    private void UseSuggestion(Suggestion suggestion)
    {
        if (_vm.UseSuggestion(suggestion)) Remember(suggestion.Prompt);
        Prompt.Focus();
        // A filled-in request: select the part most likely to change, the number.
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

    // ── The conversation ─────────────────────────────────────────────────────

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
