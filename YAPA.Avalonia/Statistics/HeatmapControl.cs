using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using YAPA.Shared.Statistics;

namespace YAPA.Avalonia.Statistics;

/// <summary>
/// GitHub-style contribution grid: one column per week, one row per weekday.
/// Drawn directly rather than as hundreds of child controls; tooltips and clicks are
/// resolved from the pointer position. A click on a day asks for that day (Your day).
/// </summary>
public sealed class HeatmapControl : Control
{
    private const double Cell = 11;
    private const double Gap = 3;
    private const double LabelColumn = 30;
    private const double LabelRow = 16;

    public static readonly StyledProperty<Heatmap?> HeatmapProperty =
        AvaloniaProperty.Register<HeatmapControl, Heatmap?>(nameof(Heatmap));

    // Tomato ramp, matching the timer's "work" colour. Level 0 depends on the theme.
    private static readonly Color[] Ramp =
    [
        Color.Parse("#FFC9BE"),
        Color.Parse("#FF9580"),
        Color.Parse("#F3603F"),
        Color.Parse("#C2391B"),
    ];

    static HeatmapControl()
    {
        AffectsRender<HeatmapControl>(HeatmapProperty);
        AffectsMeasure<HeatmapControl>(HeatmapProperty);
    }

    public Heatmap? Heatmap
    {
        get => GetValue(HeatmapProperty);
        set => SetValue(HeatmapProperty, value);
    }

    /// <summary>A day (not in the future) was clicked.</summary>
    public event Action<DateOnly>? DayClicked;

    private static readonly IPen TodayRing = new Pen(new SolidColorBrush(Color.Parse("#FFFF6B57")), 1.5);

    /// <summary>The day under <paramref name="p"/>, if there's one there and it isn't in the future.</summary>
    private HeatmapCell? CellAt(Point p)
    {
        var map = Heatmap;
        if (map == null || p.X < LabelColumn || p.Y < LabelRow) return null;
        var w = (int)Math.Floor((p.X - LabelColumn) / (Cell + Gap));
        var row = (int)Math.Floor((p.Y - LabelRow) / (Cell + Gap));
        if (w < 0 || w >= map.Weeks.Count || row < 0 || row >= 7) return null;
        var cell = map[w, row];
        return cell.IsFuture ? null : cell;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var weeks = Heatmap?.Weeks.Count ?? 53;
        return new Size(LabelColumn + weeks * (Cell + Gap), LabelRow + 7 * (Cell + Gap));
    }

    public override void Render(DrawingContext context)
    {
        var map = Heatmap;
        if (map == null) return;

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var empty = new SolidColorBrush(dark ? Color.Parse("#2B2B2E") : Color.Parse("#EBEDF0"));
        var future = new SolidColorBrush(Colors.Transparent);
        var label = new SolidColorBrush(dark ? Color.Parse("#A0A0A0") : Color.Parse("#606060"));
        var typeface = new Typeface(FontFamily.Default);
        var culture = CultureInfo.CurrentCulture;
        var today = DateOnly.FromDateTime(DateTime.Now);

        // Weekday labels on alternate rows, like GitHub, to keep them legible.
        for (var row = 1; row < 7; row += 2)
        {
            var name = culture.DateTimeFormat.GetAbbreviatedDayName(map.RowDays[row]);
            var text = new FormattedText(name, culture, FlowDirection.LeftToRight, typeface, 10, label);
            context.DrawText(text, new Point(0, LabelRow + row * (Cell + Gap) - 1));
        }

        var lastMonth = -1;
        for (var w = 0; w < map.Weeks.Count; w++)
        {
            var week = map.Weeks[w];
            var x = LabelColumn + w * (Cell + Gap);

            // Month label above the first week that starts in a new month.
            var month = week.Days[0].Date.Month;
            if (month != lastMonth && w < map.Weeks.Count - 1)
            {
                if (lastMonth != -1 || week.Days[0].Date.Day <= 7)
                {
                    var text = new FormattedText(culture.DateTimeFormat.GetAbbreviatedMonthName(month), culture,
                        FlowDirection.LeftToRight, typeface, 10, label);
                    context.DrawText(text, new Point(x, 0));
                }
                lastMonth = month;
            }

            for (var row = 0; row < week.Days.Count; row++)
            {
                var cell = week.Days[row];
                IBrush brush = cell.IsFuture ? future
                    : cell.Level == 0 ? empty
                    : new SolidColorBrush(Ramp[Math.Clamp(cell.Level, 1, 4) - 1]);
                var rect = new Rect(x, LabelRow + row * (Cell + Gap), Cell, Cell);
                context.DrawRectangle(brush, null, new RoundedRect(rect, 2.5));
                if (cell.Date == today) context.DrawRectangle(null, TodayRing, new RoundedRect(rect.Inflate(1.5), 3.5));
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        string? tip = null;
        if (CellAt(e.GetPosition(this)) is { } cell)
        {
            var count = cell.Count == 0 ? "No pomodoros" : cell.Count == 1 ? "1 pomodoro" : $"{cell.Count} pomodoros";
            tip = $"{count} on {cell.Date.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture)}. Click to see it.";
        }
        Cursor = tip != null ? HandCursor : null;

        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
            ToolTip.SetIsOpen(this, tip != null);
        }
    }

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left && CellAt(e.GetPosition(this)) is { } cell)
        {
            ToolTip.SetIsOpen(this, false);
            DayClicked?.Invoke(cell.Date);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
    }
}
