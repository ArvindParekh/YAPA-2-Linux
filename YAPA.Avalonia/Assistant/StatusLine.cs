using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace YAPA.Avalonia.Assistant;

/// <summary>What the assistant is doing right now ("Reading your day"), and whether it's changing something.</summary>
public sealed record AgentStatus(string Text, bool Acting)
{
    public static readonly AgentStatus Thinking = new("Thinking", false);
}

/// <summary>
/// The order statuses are shown in. Each stays at least <see cref="Hold"/>, so a tool that
/// answers in milliseconds is still read; a backlog keeps only the newest three rather than
/// trailing further and further behind what's actually happening.
/// </summary>
internal sealed class StatusTicker
{
    public const double Hold = 0.7;

    /// <summary>
    /// Thinking replaced this soon after the line appeared is swapped in place: it was only
    /// holding the line for the step that followed. A real step is never swapped away unseen.
    /// </summary>
    public const double Settle = 0.15;

    private readonly Queue<AgentStatus> _waiting = new();

    public AgentStatus? Current { get; private set; }

    /// <summary>What <see cref="Current"/> replaced, rolling out; null on the line's first status.</summary>
    public AgentStatus? Previous { get; private set; }

    public double ChangedAt { get; private set; }

    public bool HasWaiting => _waiting.Count > 0;

    public void Push(AgentStatus status, double now)
    {
        var last = _waiting.Count > 0 ? _waiting.Last() : Current;
        if (status == last) return;
        if (Current == null || Current == AgentStatus.Thinking && Previous == null && _waiting.Count == 0 && now - ChangedAt < Settle)
        {
            var first = Current == null;
            Current = status;
            if (first) ChangedAt = now;
            return;
        }
        // Thinking still waiting when the next step arrives was only the gap between two steps:
        // showing it would cost the next step its turn.
        if (_waiting.Count > 0 && _waiting.Last() == AgentStatus.Thinking) DropLast();
        _waiting.Enqueue(status);
        while (_waiting.Count > 3) _waiting.Dequeue();
        Advance(now);
    }

    /// <returns>True when the next status took over.</returns>
    public bool Advance(double now)
    {
        if (_waiting.Count == 0 || now - ChangedAt < Hold) return false;
        Previous = Current;
        Current = _waiting.Dequeue();
        ChangedAt = now;
        return true;
    }

    private void DropLast()
    {
        var kept = _waiting.Take(_waiting.Count - 1).ToList();
        _waiting.Clear();
        foreach (var s in kept) _waiting.Enqueue(s);
    }

    public void Reset()
    {
        _waiting.Clear();
        Current = Previous = null;
    }
}

/// <summary>
/// The assistant's status, inline where its answer will appear: a small spark (a glowing core
/// with two comets on crossed orbits, in the orb's colours) and the step it's on, with a sheen
/// sweeping across the words. A new step rolls the old one up and out.
///
/// It sits in the conversation rather than by the orb because that's where the eye is after
/// sending: on the question just asked, by the prompt.
/// Paced by its own timer, like the orb, and only while it's shown.
/// </summary>
public sealed class StatusLine : Control
{
    public static readonly StyledProperty<AgentStatus?> StatusProperty =
        AvaloniaProperty.Register<StatusLine, AgentStatus?>(nameof(Status));

    public AgentStatus? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private const double LineHeight = 22;
    private const double SparkSize = 22;
    private const double TextLeft = 30;
    private const double FontSize = 13.5;
    private const double Roll = 0.38;      // seconds for one status to roll out and the next in
    private const double Entrance = 0.42;
    private const double Rise = 13;        // how far a rolling status travels, through a slot the line's height

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StatusTicker _ticker = new();
    private DispatcherTimer? _frames;
    private double _shownAt;
    private double _lastFrame;
    private double _spin;        // comets' angle: advances faster while acting, so a change of pace never jumps
    private double _acting;      // 0 thinking … 1 acting, eased
    private OrbControl.Vector3 _a, _b, _c;
    private FormattedText? _current, _previous;
    private AgentStatus? _currentFor, _previousFor;
    private double _maxTextWidth = double.PositiveInfinity;
    private double _textWidthFor = double.NaN; // the width the cached texts were laid out for
    private bool _measuredRolling;

    public StatusLine()
    {
        SetColors(1);
        ClipToBounds = false;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StatusProperty)
        {
            if (Status is { } status)
            {
                var fresh = _ticker.Current == null;
                _ticker.Push(status, Now);
                if (fresh) Appear();
            }
            else
            {
                _ticker.Reset();
            }
            InvalidateMeasure();
            StartAnimating();
        }
        else if (change.Property == IsVisibleProperty)
        {
            StartAnimating();
        }
    }

    private void Appear()
    {
        _shownAt = Now;
        SetColors(_ticker.Current!.Acting ? 1 : 0);
    }

    private TopLevel? _window;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Hiding the window stops the frames; nothing else would start them again on show.
        _window = TopLevel.GetTopLevel(this);
        if (_window != null) _window.PropertyChanged += OnWindowChanged;
        StartAnimating();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window != null) _window.PropertyChanged -= OnWindowChanged;
        _window = null;
        _frames?.Stop();
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) StartAnimating();
    }

    private bool Live => Status != null && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is { IsVisible: true };

    private void StartAnimating()
    {
        if (!Live || _frames?.IsEnabled == true) return;
        _lastFrame = Now;
        _frames ??= CreateFrameTimer();
        _frames.Start();
    }

    // A timer, not RequestAnimationFrame: that runs the renderer every vsync whether or not
    // anything changed. 40 a second is smooth for motion this small.
    private DispatcherTimer CreateFrameTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / 40) };
        timer.Tick += (_, _) => OnFrame();
        return timer;
    }

    private void OnFrame()
    {
        if (!Live)
        {
            _frames?.Stop();
            return;
        }
        var now = Now;
        var dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;

        // The line is as wide as the wider of the two statuses while one rolls out of the other.
        if (_ticker.Advance(now) || Rolling != _measuredRolling) InvalidateMeasure();
        var acting = _ticker.Current?.Acting == true ? 1.0 : 0.0;
        _acting += (acting - _acting) * (1 - Math.Exp(-dt * 4));
        _spin += dt * (2 * Math.PI / 1.7) * (1 + 0.65 * _acting);
        SetColors(_acting);
        InvalidateVisual();
    }

    private void SetColors(double acting)
    {
        var t = OrbPalette.Thinking;
        var a = OrbPalette.Acting;
        _a = OrbControl.Vector3.From(t.A).Lerp(OrbControl.Vector3.From(a.A), acting);
        _b = OrbControl.Vector3.From(t.B).Lerp(OrbControl.Vector3.From(a.B), acting);
        _c = OrbControl.Vector3.From(t.C).Lerp(OrbControl.Vector3.From(a.C), acting);
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        _maxTextWidth = Math.Max(0, availableSize.Width - TextLeft);
        _measuredRolling = Rolling;
        var width = TextLeft + Math.Max(Text(_ticker.Current, ref _current, ref _currentFor)?.Width ?? 0,
                                        Rolling ? Text(_ticker.Previous, ref _previous, ref _previousFor)?.Width ?? 0 : 0);
        return new Size(Math.Min(width, availableSize.Width), LineHeight);
    }

    private bool Rolling => _ticker.Previous != null && Now - _ticker.ChangedAt < Roll;

    /// <summary>The status as laid-out text, made again only when it changes.</summary>
    private FormattedText? Text(AgentStatus? status, ref FormattedText? cached, ref AgentStatus? cachedFor)
    {
        if (status == null) return null;
        if (_textWidthFor != _maxTextWidth)
        {
            _current = _previous = null;
            _textWidthFor = _maxTextWidth;
        }
        if (cached != null && ReferenceEquals(cachedFor, status)) return cached;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Medium);
        cached = new FormattedText(status.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, FontSize, Brushes.White)
        {
            MaxTextWidth = double.IsInfinity(_maxTextWidth) ? 0 : _maxTextWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        cachedFor = status;
        return cached;
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        if (_ticker.Current == null) return;
        var now = Now;
        var since = now - _shownAt;
        var entrance = Ease.CubicOut(Math.Clamp(since / Entrance, 0, 1));
        var sinceChange = now - _ticker.ChangedAt;
        var changed = _ticker.Previous != null ? Math.Clamp(sinceChange / 0.6, 0, 1) : 1;

        using (context.PushOpacity(entrance))
        {
            // The spark: grows in with a little overshoot, flares on each new step.
            var scale = Ease.BackOut(Math.Clamp(since / Entrance, 0, 1));
            context.Custom(new SparkDraw(new Rect(0, (LineHeight - SparkSize) / 2, SparkSize, SparkSize),
                (float)_spin, (float)(now * 0.35), (float)scale, (float)(1 - changed), _a, _b, _c));

            var roll = _ticker.Previous != null ? Ease.CubicOut(Math.Clamp(sinceChange / Roll, 0, 1)) : 1;
            var slideIn = (1 - entrance) * 5; // the first status drifts in from the left as it fades up
            var current = Text(_ticker.Current, ref _current, ref _currentFor)!;
            // Like an odometer: the old step leaves through the top of a slot as the new one comes
            // up from below. Each fades over its own part of the roll, so they never sit on each other.
            using (context.PushClip(new Rect(TextLeft - 8, 0, Bounds.Width - TextLeft + 8, LineHeight)))
            {
                DrawStatus(context, current, now, TextLeft - slideIn, Rise * (1 - roll), Math.Clamp((roll - 0.2) / 0.8, 0, 1));
                if (roll < 1 && Text(_ticker.Previous, ref _previous, ref _previousFor) is { } previous)
                    DrawStatus(context, previous, now, TextLeft, -Rise * roll, Math.Clamp(1 - roll / 0.6, 0, 1));
            }
        }
    }

    private void DrawStatus(DrawingContext context, FormattedText text, double now, double x, double dy, double opacity)
    {
        if (opacity <= 0.01) return;
        var y = (LineHeight - text.Height) / 2 + dy;
        text.SetForegroundBrush(Sheen(text.Width, now));
        using (context.PushOpacity(opacity))
        using (context.PushTransform(Matrix.CreateTranslation(x, y)))
            context.DrawText(text, default);
    }

    /// <summary>
    /// Dim text with a band of light passing over it, iridescent at its edges in the spark's
    /// colours. The band moves at a steady speed, so short and long statuses shimmer alike.
    /// </summary>
    private IBrush Sheen(double width, double now)
    {
        const double band = 72;
        const double speed = 120;   // pixels a second
        const double rest = 0.45;   // between passes
        var travel = width + 2 * band;
        var period = travel / speed + rest;
        var centre = -band + (now % period) * speed;

        var dim = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        var tintA = Tint(_a, 0.45);
        var tintC = Tint(_c, 0.45);
        return new ImmutableLinearGradientBrush(
            [
                new ImmutableGradientStop(0, dim),
                new ImmutableGradientStop(0.28, tintA),
                new ImmutableGradientStop(0.5, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                new ImmutableGradientStop(0.72, tintC),
                new ImmutableGradientStop(1, dim),
            ],
            startPoint: new RelativePoint(centre - band / 2, 0, RelativeUnit.Absolute),
            endPoint: new RelativePoint(centre + band / 2, 0, RelativeUnit.Absolute));

        static Color Tint(OrbControl.Vector3 c, double white)
            => Color.FromArgb(0xF0, (byte)(255 * (c.X + (1 - c.X) * white)), (byte)(255 * (c.Y + (1 - c.Y) * white)), (byte)(255 * (c.Z + (1 - c.Z) * white)));
    }

    private static class Ease
    {
        public static double CubicOut(double t) => 1 - Math.Pow(1 - t, 3);

        public static double BackOut(double t)
        {
            const double s = 1.9;
            var u = t - 1;
            return 1 + u * u * ((s + 1) * u + s);
        }
    }

    /// <summary>
    /// The spark, drawn with Skia: a small atom. A glowing core, two faint orbits tilted across
    /// each other and slowly precessing, and a comet on each, dimmer on the far side and passing
    /// behind the core, so it reads as turning in depth even at twenty pixels. The tracks are
    /// what make it legible that small; comets alone were just smudges.
    /// </summary>
    /// <param name="spin">How far the comets have travelled round their orbits.</param>
    /// <param name="precession">How far the orbits themselves have turned.</param>
    /// <param name="flare">1 the moment a new step begins, fading to 0: the core brightens and a ring leaves it.</param>
    private sealed class SparkDraw(Rect bounds, float spin, float precession, float scale, float flare,
        OrbControl.Vector3 a, OrbControl.Vector3 b, OrbControl.Vector3 c) : ICustomDrawOperation
    {
        private const float RadiusX = 8.6f, RadiusY = 3.3f;
        private const float Tilt = 0.62f;
        private const int Tail = 34;
        private const float TailStep = 0.055f; // radians between the tail's dots: about 105 degrees of tail
        private static readonly OrbControl.Vector3 White = new(1, 1, 1);

        public Rect Bounds => bounds.Inflate(6);

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;

            canvas.Save();
            canvas.Translate((float)bounds.Center.X, (float)bounds.Center.Y);
            canvas.Scale(scale);

            var comets = new[]
            {
                new Comet(spin, Tilt + precession, a),
                new Comet(spin * 1.09f + MathF.PI, -Tilt + precession, c),
            };

            using var paint = new SKPaint { IsAntialias = true };
            foreach (var comet in comets) DrawTrack(canvas, paint, comet);
            foreach (var comet in comets) DrawComet(canvas, paint, comet, front: false);
            DrawCore(canvas);
            foreach (var comet in comets) DrawComet(canvas, paint, comet, front: true);
            if (flare > 0) DrawRing(canvas);

            canvas.Restore();
        }

        private readonly record struct Comet(float Angle, float Tilt, OrbControl.Vector3 Colour);

        /// <returns>Where on its orbit, and how near: -1 the far side, 1 the near side.</returns>
        private static (SKPoint At, float Depth) OnOrbit(float angle, float tilt)
        {
            float x = RadiusX * MathF.Cos(angle), y = RadiusY * MathF.Sin(angle);
            float cos = MathF.Cos(tilt), sin = MathF.Sin(tilt);
            return (new SKPoint(x * cos - y * sin, x * sin + y * cos), MathF.Sin(angle));
        }

        private static void DrawTrack(SKCanvas canvas, SKPaint paint, Comet comet)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 0.9f;
            paint.Color = Colour(comet.Colour.Lerp(White, 0.3), 0.2f);
            canvas.Save();
            canvas.RotateRadians(comet.Tilt);
            canvas.DrawOval(SKPoint.Empty, new SKSize(RadiusX, RadiusY), paint);
            canvas.Restore();
            paint.Style = SKPaintStyle.Fill;
        }

        /// <summary>The head and its tapering tail, the part of it in the half of the orbit asked for.</summary>
        private static void DrawComet(SKCanvas canvas, SKPaint paint, Comet comet, bool front)
        {
            for (var i = Tail - 1; i >= 0; i--)
            {
                var (at, depth) = OnOrbit(comet.Angle - i * TailStep, comet.Tilt);
                if (depth >= 0 != front) continue;
                var k = i / (float)Tail;
                var near = 0.5f + 0.5f * (depth + 1) / 2;
                var radius = (0.35f + 1.25f * MathF.Pow(1 - k, 0.8f)) * (0.75f + 0.3f * near);
                // Dense, overlapping dots: each faint, so together they make a smooth tail.
                var alpha = 0.32f * MathF.Pow(1 - k, 1.4f) * near;
                paint.Color = Colour(comet.Colour.Lerp(White, 0.15 + 0.3 * (1 - k)), alpha);
                canvas.DrawCircle(at, radius, paint);
            }
            var (head, headDepth) = OnOrbit(comet.Angle, comet.Tilt);
            if (headDepth >= 0 != front) return;
            var headNear = 0.55f + 0.45f * (headDepth + 1) / 2;
            paint.Color = Colour(comet.Colour.Lerp(White, 0.6), headNear);
            canvas.DrawCircle(head, 1.45f * (0.8f + 0.25f * headNear), paint);
        }

        private void DrawCore(SKCanvas canvas)
        {
            var glow = 6.5f + 3f * flare;
            using (var halo = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus })
            {
                halo.Shader = SKShader.CreateRadialGradient(SKPoint.Empty, glow,
                    [Colour(b, 0.55f + 0.3f * flare), Colour(b, 0.16f), Colour(b, 0)], [0f, 0.42f, 1f], SKShaderTileMode.Clamp);
                canvas.DrawCircle(SKPoint.Empty, glow, halo);
            }
            using var core = new SKPaint { IsAntialias = true };
            var r = 2.5f + 0.9f * flare;
            core.Shader = SKShader.CreateRadialGradient(new SKPoint(-0.7f, -0.8f), r * 1.5f,
                [Colour(White, 1), Colour(b.Lerp(White, 0.4), 1), Colour(b, 1)], [0f, 0.4f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawCircle(SKPoint.Empty, r, core);
        }

        /// <summary>A new step: a ring of light leaves the core, as the orb's does when something's done.</summary>
        private void DrawRing(SKCanvas canvas)
        {
            var t = 1 - flare;
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.2f,
                BlendMode = SKBlendMode.Plus,
                Color = Colour(c.Lerp(White, 0.4), 0.75f * flare * flare),
            };
            canvas.DrawCircle(SKPoint.Empty, 3 + 8 * t, paint);
        }

        private static SKColor Colour(OrbControl.Vector3 v, float alpha)
            => new((byte)(v.X * 255), (byte)(v.Y * 255), (byte)(v.Z * 255), (byte)(Math.Clamp(alpha, 0, 1) * 255));
    }
}
