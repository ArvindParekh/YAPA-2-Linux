using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace YAPA.Avalonia.Settings.Controls;

/// <summary>
/// One cycle drawn to scale: each pomodoro and break as a segment, the long break at the end.
/// The lengths above it stop being abstract numbers: you see what 50 · 10 · 30 × 4 adds up to.
/// </summary>
public sealed class CycleStrip : Control
{
    public static readonly StyledProperty<double> WorkProperty = AvaloniaProperty.Register<CycleStrip, double>(nameof(Work), 25);
    public static readonly StyledProperty<double> ShortBreakProperty = AvaloniaProperty.Register<CycleStrip, double>(nameof(ShortBreak), 5);
    public static readonly StyledProperty<double> LongBreakProperty = AvaloniaProperty.Register<CycleStrip, double>(nameof(LongBreak), 15);
    public static readonly StyledProperty<int> CountProperty = AvaloniaProperty.Register<CycleStrip, int>(nameof(Count), 4);

    static CycleStrip()
    {
        AffectsRender<CycleStrip>(WorkProperty, ShortBreakProperty, LongBreakProperty, CountProperty);
    }

    public double Work { get => GetValue(WorkProperty); set => SetValue(WorkProperty, value); }
    public double ShortBreak { get => GetValue(ShortBreakProperty); set => SetValue(ShortBreakProperty, value); }
    public double LongBreak { get => GetValue(LongBreakProperty); set => SetValue(LongBreakProperty, value); }
    public int Count { get => GetValue(CountProperty); set => SetValue(CountProperty, value); }

    private static readonly IBrush WorkBrush = new SolidColorBrush(Color.Parse("#FFFF6B57"));
    private static readonly IBrush BreakBrush = new SolidColorBrush(Color.Parse("#B33CCB9B"));
    private static readonly IBrush LongBrush = new SolidColorBrush(Color.Parse("#FF2FB874"));

    protected override Size MeasureOverride(Size availableSize) => new(0, 10);

    public override void Render(DrawingContext context)
    {
        var count = Math.Max(1, Count);
        var work = Math.Max(1, Work);
        var shortBreak = Math.Max(1, ShortBreak);
        var longBreak = Math.Max(1, LongBreak);
        var total = count * work + (count - 1) * shortBreak + longBreak;

        const double gap = 3;
        var segments = count * 2;
        var usable = Bounds.Width - gap * (segments - 1);
        if (usable <= 0) return;
        var h = Bounds.Height;

        var x = 0.0;
        void Segment(double minutes, IBrush brush)
        {
            // Never thinner than a dot, so a 1-minute break still shows.
            var w = Math.Max(h, usable * minutes / total);
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, 0, w, h), h / 2));
            x += w + gap;
        }

        for (var i = 0; i < count; i++)
        {
            Segment(work, WorkBrush);
            Segment(i == count - 1 ? longBreak : shortBreak, i == count - 1 ? LongBrush : BreakBrush);
        }
    }
}
