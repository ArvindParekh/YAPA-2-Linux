using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// The day at a glance: each pomodoro as a block on an hour scale, coloured by its project,
/// with the breaks as the gaps between. Hovering names a block; clicking one goes to it.
/// </summary>
public sealed class DayRibbon : Control
{
    public static readonly StyledProperty<IReadOnlyList<RibbonBlock>> BlocksProperty =
        AvaloniaProperty.Register<DayRibbon, IReadOnlyList<RibbonBlock>>(nameof(Blocks), []);

    public static readonly StyledProperty<DateTime> StartProperty = AvaloniaProperty.Register<DayRibbon, DateTime>(nameof(Start));
    public static readonly StyledProperty<DateTime> EndProperty = AvaloniaProperty.Register<DayRibbon, DateTime>(nameof(End));

    private const double BarHeight = 22;
    private const double LabelGap = 7;
    private const double Gap = 1.5; // between blocks that touch

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#0EFFFFFF"));
    private static readonly IBrush Tick = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#FF6E6E78"));
    private static readonly Typeface Face = new(new FontFamily(App.UiFontUri));

    private int _hovered = -1;

    static DayRibbon()
    {
        AffectsRender<DayRibbon>(BlocksProperty, StartProperty, EndProperty);
        AffectsMeasure<DayRibbon>(BlocksProperty);
    }

    public DayRibbon() => Cursor = new Cursor(StandardCursorType.Hand);

    public IReadOnlyList<RibbonBlock> Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public DateTime Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public DateTime End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    /// <summary>A block was clicked; the pomodoro's id.</summary>
    public event Action<int>? BlockClicked;

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, BarHeight + LabelGap + 14);

    private double TotalMinutes => Math.Max(60, (End - Start).TotalMinutes);

    private Rect BlockRect(RibbonBlock b)
    {
        var scale = Bounds.Width / TotalMinutes;
        var x = b.Start * scale;
        var w = Math.Max(3, b.Length * scale - Gap);
        return new Rect(x, 0, w, BarHeight);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= 0) return;
        context.DrawRectangle(Track, null, new RoundedRect(new Rect(0, 0, width, BarHeight), 7));

        var blocks = Blocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var rect = BlockRect(b);
            var color = b.Color;
            var alpha = b.InProgress ? (byte)0x8C : i == _hovered ? (byte)0xFF : (byte)0xE0;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            context.DrawRectangle(brush, null, new RoundedRect(rect, 5));
            if (i == _hovered)
                context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), 1.2), new RoundedRect(rect.Deflate(0.6), 5));
        }

        // Hour marks: every hour when there's room, else every two or three.
        var hours = TotalMinutes / 60;
        var step = width / hours > 46 ? 1 : width / hours > 24 ? 2 : 3;
        for (var h = 0; h <= hours + 0.01; h += step)
        {
            var x = h * 60 * width / TotalMinutes;
            context.DrawRectangle(Tick, null, new Rect(Math.Min(x, width - 1), BarHeight + 1, 1, 4));
            var time = Start.AddHours(h);
            var text = new FormattedText(ClockFormat.Is24Hour ? time.ToString("HH", CultureInfo.InvariantCulture) : time.ToString("h tt", CultureInfo.InvariantCulture).ToLowerInvariant(),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, Label);
            var left = Math.Clamp(x - text.Width / 2, 0, width - text.Width);
            context.DrawText(text, new Point(left, BarHeight + LabelGap));
        }
    }

    private int HitTest(Point p)
    {
        if (p.Y > BarHeight + 2) return -1;
        var blocks = Blocks;
        for (var i = 0; i < blocks.Count; i++)
            if (BlockRect(blocks[i]).Inflate(new Thickness(1, 3)).Contains(p)) return i;
        return -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit == _hovered) return;
        _hovered = hit;
        ToolTip.SetTip(this, hit >= 0 ? Blocks[hit].Tip : null);
        ToolTip.SetIsOpen(this, hit >= 0);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = -1;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit >= 0) BlockClicked?.Invoke(Blocks[hit].PomodoroId);
    }
}
