using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace YAPA.Avalonia.Statistics;

/// <summary>A bottom-anchored bar filling <see cref="Fraction"/> of the available height.</summary>
public sealed class BarFill : Control
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<BarFill, double>(nameof(Fraction));
    public static readonly StyledProperty<bool> HighlightProperty = AvaloniaProperty.Register<BarFill, bool>(nameof(Highlight));

    private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#F3603F"), 0.55);
    private static readonly IBrush Today = new SolidColorBrush(Color.Parse("#F3603F"));
    private static readonly IBrush Empty = new SolidColorBrush(Color.Parse("#808080"), 0.18);

    static BarFill() => AffectsRender<BarFill>(FractionProperty, HighlightProperty);

    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public bool Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    public override void Render(DrawingContext context)
    {
        var b = Bounds;
        if (Fraction <= 0)
        {
            // A sliver, so empty days still read as days rather than gaps.
            context.DrawRectangle(Empty, null, new RoundedRect(new Rect(0, b.Height - 3, b.Width, 3), 1.5));
            return;
        }
        var h = Math.Max(3, b.Height * Math.Clamp(Fraction, 0, 1));
        context.DrawRectangle(Highlight ? Today : Normal, null, new RoundedRect(new Rect(0, b.Height - h, b.Width, h), 3));
    }
}
