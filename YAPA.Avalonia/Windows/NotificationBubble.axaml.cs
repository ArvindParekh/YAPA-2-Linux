using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Platform.Linux.X11;
using YAPA.Avalonia.Specifics;

namespace YAPA.Avalonia.Windows;

/// <summary>
/// A notice as a bubble on the timer: title, a line, the next step as buttons, a tail that
/// points at the timer. It never takes focus, survives Show Desktop like the timer does, and
/// goes away by itself unless it's waiting on a decision (an interval that ended).
/// </summary>
public partial class NotificationBubble : Window
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(8);
    private const double Gap = 2; // between the timer and the tail's tip

    public static readonly StyledProperty<IBrush> AccentProperty =
        AvaloniaProperty.Register<NotificationBubble, IBrush>(nameof(Accent), new SolidColorBrush(Color.Parse("#FFFF6B57")));

    /// <summary>The dot and the main button: tomato after work, green after a break, violet for a reminder.</summary>
    public IBrush Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private readonly GeneralSettings _general;
    private readonly DispatcherTimer _linger;
    private readonly DispatcherTimer _hide;
    private Action<string>? _onAction;
    private bool _persistent;

    public NotificationBubble() : this(App.Bootstrapper!.Resolve<GeneralSettings>()) { }

    public NotificationBubble(GeneralSettings general)
    {
        _general = general;
        InitializeComponent();
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        // A notice must never pull focus from whatever the user is typing into.
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _linger = new DispatcherTimer { Interval = Linger };
        _linger.Tick += (_, _) => Dismiss();
        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            Hide();
        };

        // Reading it shouldn't race the clock: the countdown waits while the pointer is on it.
        PointerEntered += (_, _) => _linger.Stop();
        PointerExited += (_, _) => { if (!_persistent && IsVisible) _linger.Start(); };
        Root.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) UpdateInputRegion(); };
    }

    public void Present(DesktopNotification notice, Window timer, Action<string> onAction)
    {
        _hide.Stop();
        _onAction = onAction;
        _persistent = notice.Persistent;

        Accent = new SolidColorBrush(Color.Parse(notice.Category switch
        {
            DesktopNotification.BreakCategory => "#FF3CCB9B",
            DesktopNotification.ReminderCategory => "#FF8B7CFF",
            _ => "#FFFF6B57",
        }));
        TitleText.Text = notice.Title;
        BodyText.Text = notice.Body;
        BodyText.IsVisible = notice.Body.Length > 0;

        Actions.Children.Clear();
        // "default" is the desktop's "click the notification"; here the bubble already is on the timer.
        var buttons = notice.Actions.Where(a => a.Key != "default" && a.Label != "Show timer").ToList();
        for (var i = 0; i < buttons.Count; i++)
        {
            var action = buttons[i];
            var button = new Button { Content = action.Label, Classes = { "act" } };
            if (i == 0) button.Classes.Add("primary");
            button.Click += (_, _) =>
            {
                Dismiss();
                _onAction?.Invoke(action.Key);
            };
            Actions.Children.Add(button);
        }
        Actions.IsVisible = buttons.Count > 0;

        Place(timer);
        if (!IsVisible)
        {
            WindowShellIntegration.ApplyFloating(this, _general);
            Show();
        }
        Root.Classes.Set("shown", true);

        _linger.Stop();
        if (!_persistent) _linger.Start();
    }

    public void Dismiss()
    {
        _linger.Stop();
        if (!IsVisible || _hide.IsEnabled) return;
        Root.Classes.Set("shown", false);
        _hide.Start();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Dismiss();

    /// <summary>Under the timer when there's room, above it otherwise; the tail stays on the timer's centre.</summary>
    private void Place(Window timer)
    {
        var screen = Screens.ScreenFromWindow(timer) ?? Screens.Primary;
        var scale = screen?.Scaling ?? 1;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int Px(double dip) => (int)Math.Round(dip * scale);

        var t = new PixelRect(timer.Position, PixelSize.FromSize(timer.Bounds.Size, timer.DesktopScaling));
        // Measure first: the height decides whether it fits below.
        Root.Measure(new Size(Width, double.PositiveInfinity));
        var height = Px(Root.DesiredSize.Height + Root.Margin.Top + Root.Margin.Bottom);
        var width = Px(Width);
        var above = t.Bottom + Px(Gap) + height > work.Bottom && t.Y - height > work.Y;

        // The tail on top pointing up when below the timer; at the bottom pointing down when above.
        Root.Classes.Set("above", above);
        Root.Margin = above ? new Thickness(14, 22, 14, 4) : new Thickness(14, 4, 14, 22);
        Card.Margin = above ? new Thickness(0, 0, 0, 9) : new Thickness(0, 9, 0, 0);
        Tail.VerticalAlignment = above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Tail.Margin = above ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0);
        Tail.BorderThickness = above ? new Thickness(0, 0, 1, 1) : new Thickness(1, 1, 0, 0);

        var x = Math.Clamp(t.X + t.Width / 2 - width / 2, work.X + Px(4), Math.Max(work.X + Px(4), work.Right - width - Px(4)));
        var y = above ? t.Y - Px(Gap) - height : t.Bottom + Px(Gap);
        Position = new PixelPoint(x, y);

        // When the bubble had to slide sideways to stay on screen, its tail still points at the timer.
        var tailCentre = (t.X + t.Width / 2 - x) / scale - Root.Margin.Left;
        Tail.HorizontalAlignment = HorizontalAlignment.Left;
        Tail.Margin = new Thickness(Math.Clamp(tailCentre - 7, 18, Width - Root.Margin.Left - Root.Margin.Right - 32), Tail.Margin.Top, 0, Tail.Margin.Bottom);
    }

    /// <summary>Clicks in the transparent margin around the card go through to the desktop.</summary>
    private void UpdateInputRegion()
    {
        if (TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle || handle.Handle == IntPtr.Zero) return;
        var b = Card.Bounds;
        if (b.Width <= 0) return;
        var s = RenderScaling;
        var origin = Card.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        X11Window.SetInputRegion(handle.Handle,
            ((int)(origin.X * s), (int)(origin.Y * s), (int)Math.Ceiling(b.Width * s), (int)Math.Ceiling(b.Height * s)));
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            Dismiss();
        }
        base.OnClosing(e);
    }
}
