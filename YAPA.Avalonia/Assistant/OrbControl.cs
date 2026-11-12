using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace YAPA.Avalonia.Assistant;

public enum OrbMood
{
    /// <summary>Waiting for the user.</summary>
    Idle,

    /// <summary>Waiting for an answer.</summary>
    Thinking,

    /// <summary>A tool is changing something.</summary>
    Acting,

    /// <summary>The answer is streaming in.</summary>
    Speaking,

    Error,
}

/// <summary>The colours the orb is made of.</summary>
public readonly record struct OrbPalette(Color A, Color B, Color C)
{
    /// <summary>Nothing running: cool and quiet.</summary>
    public static readonly OrbPalette Calm = new(Color.Parse("#7B61FF"), Color.Parse("#3E9BFF"), Color.Parse("#43E0E8"));

    /// <summary>During a pomodoro: the timer's tomato, warmed up.</summary>
    public static readonly OrbPalette Focus = new(Color.Parse("#FF5A3C"), Color.Parse("#FFA63D"), Color.Parse("#FF4F8B"));

    /// <summary>During a break: the progress line's sea green.</summary>
    public static readonly OrbPalette Rest = new(Color.Parse("#2FB874"), Color.Parse("#2CD4C0"), Color.Parse("#B4E85A"));

    public static readonly OrbPalette Trouble = new(Color.Parse("#C2423A"), Color.Parse("#7D3A3A"), Color.Parse("#E07A5F"));

    /// <summary>Thinking: a cool shimmer, whatever the phase, so a request in flight is unmistakable.</summary>
    public static readonly OrbPalette Thinking = new(Color.Parse("#8B5CFF"), Color.Parse("#36C8F0"), Color.Parse("#FF5FB0"));

    /// <summary>Changing something: warm and bright.</summary>
    public static readonly OrbPalette Acting = new(Color.Parse("#FFB347"), Color.Parse("#FF5F6D"), Color.Parse("#FFE08A"));

    /// <summary>The same colours, lit up: speaking.</summary>
    public OrbPalette Brighter() => new(Lift(A), Lift(B), Lift(C));

    private static Color Lift(Color c) => Color.FromRgb((byte)(c.R + (255 - c.R) * 0.25), (byte)(c.G + (255 - c.G) * 0.25), (byte)(c.B + (255 - c.B) * 0.25));
}

/// <summary>
/// A living sphere of flowing colour: the assistant's face. Its motion follows <see cref="Mood"/>
/// (slow breathing while idle, a quicker swirl while thinking, a pulse when something is
/// done) and <see cref="Nudge"/> (each keystroke), and its colours follow the timer's phase.
///
/// Drawn by an SkSL shader through Avalonia's Skia lease, which costs next to nothing on the
/// GPU and stays cheap on the CPU at this size. Animates only while visible.
/// </summary>
public sealed class OrbControl : Control
{
    public static readonly StyledProperty<OrbMood> MoodProperty =
        AvaloniaProperty.Register<OrbControl, OrbMood>(nameof(Mood));

    public static readonly StyledProperty<OrbPalette> PaletteProperty =
        AvaloniaProperty.Register<OrbControl, OrbPalette>(nameof(Palette), OrbPalette.Calm);

    public OrbMood Mood
    {
        get => GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    public OrbPalette Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame;
    private double _phase;        // shader time: advances faster with energy, so speed changes never jump
    private double _energy = 0.2; // 0 calm … 1 busy, eased toward the mood's level
    private double _impulse;      // short bursts (keystrokes, a finished action), decaying
    private double _ring;         // 0..1 progress of the "done" ripple; 1 = none
    private double _breath;       // the outline's slow swell, as a fraction of the radius
    private Vector3 _a, _b, _c;   // current colours, eased toward the palette
    private bool _animating;
    private double _lastStirred; // last keystroke, answer text or action: the orb was being watched

    /// <summary>Busy: smooth. Idle: the slow drift reads the same at half the rate. Untouched for a while: a quiet breath.</summary>
    private double FrameInterval(double now)
    {
        if (Mood is OrbMood.Thinking or OrbMood.Acting or OrbMood.Speaking || _impulse > 0.05 || _ring < 1) return 1.0 / 60;
        return now - _lastStirred > 20 ? 1.0 / 15 : 1.0 / 30;
    }

    public OrbControl()
    {
        _ring = 1;
        SetColors(Palette, immediate: true);
    }

    /// <summary>A small jolt of energy: the orb reacts to typing.</summary>
    public void Nudge(double amount = 0.12)
    {
        _impulse = Math.Min(1.0, _impulse + amount);
        _lastStirred = _clock.Elapsed.TotalSeconds;
    }

    /// <summary>A ripple and a lift: something was done.</summary>
    public void Pulse()
    {
        _lastStirred = _clock.Elapsed.TotalSeconds;
        _ring = 0;
        _impulse = Math.Min(1.0, _impulse + 0.5);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) StartAnimating();
        if (change.Property == MoodProperty) _lastStirred = _clock.Elapsed.TotalSeconds;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartAnimating();
    }

    /// <summary>
    /// Starts the motion again after the window was hidden: hiding a window stops the frames,
    /// and showing it again changes nothing the orb itself can see.
    /// </summary>
    public void Resume() => StartAnimating();

    private void StartAnimating()
    {
        if (_animating || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is null) return;
        _animating = true;
        _lastFrame = _clock.Elapsed.TotalSeconds;
        _frames ??= CreateFrameTimer();
        _frames.Interval = TimeSpan.FromSeconds(1.0 / 60);
        _frames.Start();
    }

    // A timer at the frame rate the motion needs, not RequestAnimationFrame: that one runs the
    // renderer every vsync even when nothing is drawn, which in software rendering cost more
    // than the orb itself.
    private DispatcherTimer? _frames;

    private DispatcherTimer CreateFrameTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render);
        timer.Tick += (_, _) => OnFrame();
        return timer;
    }

    private void OnFrame()
    {
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { IsVisible: true })
        {
            _frames?.Stop();
            _animating = false;
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;

        var target = Mood switch
        {
            OrbMood.Thinking => 0.78,
            OrbMood.Acting => 1.0,
            OrbMood.Speaking => 0.55,
            OrbMood.Error => 0.08,
            _ => 0.32,
        };
        // Exponential easing: frame-rate independent, quick to rise and gentle to settle.
        _energy += (target - _energy) * (1 - Math.Exp(-dt * (target > _energy ? 5.0 : 2.2)));
        _impulse *= Math.Exp(-dt * 3.0);
        if (_ring < 1) _ring = Math.Min(1, _ring + dt / 0.9);
        var energy = Math.Min(1.2, _energy + _impulse);
        _phase += dt * (0.38 + 1.0 * energy);

        // Each mood wears its own colours; the eased blend makes every change a visible shift.
        var palette = Mood switch
        {
            OrbMood.Error => OrbPalette.Trouble,
            OrbMood.Thinking => OrbPalette.Thinking,
            OrbMood.Acting => OrbPalette.Acting,
            OrbMood.Speaking => Palette.Brighter(),
            _ => Palette,
        };
        EaseColors(palette, dt);

        // The breath: slow and deep when idle, quick when busy, swelling with each word spoken.
        var t = now;
        _breath = Mood switch
        {
            OrbMood.Thinking => 0.030 * Math.Sin(t * 4.0),
            OrbMood.Acting => 0.040 * Math.Sin(t * 7.0),
            OrbMood.Speaking => 0.012 * Math.Sin(t * 2.4) + 0.06 * _impulse,
            OrbMood.Error => 0.006 * Math.Sin(t * 1.0),
            _ => 0.022 * Math.Sin(t * 1.5),
        };

        // Frames cost CPU wherever the GPU doesn't draw them: only as many as the motion needs.
        InvalidateVisual();
        var interval = TimeSpan.FromSeconds(FrameInterval(now));
        if (_frames!.Interval != interval) _frames.Interval = interval;
    }

    private void SetColors(OrbPalette p, bool immediate)
    {
        if (!immediate) return;
        _a = Vector3.From(p.A);
        _b = Vector3.From(p.B);
        _c = Vector3.From(p.C);
    }

    private void EaseColors(OrbPalette p, double dt)
    {
        var k = 1 - Math.Exp(-dt * 2.5);
        _a = _a.Lerp(Vector3.From(p.A), k);
        _b = _b.Lerp(Vector3.From(p.B), k);
        _c = _c.Lerp(Vector3.From(p.C), k);
    }

    public override void Render(DrawingContext context)
    {
        var energy = (float)Math.Min(1.2, _energy + _impulse);
        context.Custom(new OrbDraw(new Rect(Bounds.Size), (float)_phase, energy, (float)_ring, (float)_breath, _a, _b, _c));
    }

    internal readonly record struct Vector3(float X, float Y, float Z)
    {
        public static Vector3 From(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        public Vector3 Lerp(Vector3 to, double k)
            => new((float)(X + (to.X - X) * k), (float)(Y + (to.Y - Y) * k), (float)(Z + (to.Z - Z) * k));
    }

    private sealed class OrbDraw(Rect bounds, float time, float energy, float ring, float breath, Vector3 a, Vector3 b, Vector3 c) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var size = (float)Math.Min(bounds.Width, bounds.Height);
            if (size <= 0) return;

            if (OrbShader.Effect is { } effect)
            {
                using var uniforms = new SKRuntimeEffectUniforms(effect)
                {
                    ["size"] = size,
                    ["time"] = time,
                    ["energy"] = energy,
                    ["ring"] = ring,
                    ["breath"] = breath,
                    ["colA"] = new[] { a.X, a.Y, a.Z },
                    ["colB"] = new[] { b.X, b.Y, b.Z },
                    ["colC"] = new[] { c.X, c.Y, c.Z },
                };
                using var shader = effect.ToShader(uniforms);
                using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                canvas.Save();
                canvas.Translate((float)(bounds.Width - size) / 2, (float)(bounds.Height - size) / 2);
                canvas.DrawRect(0, 0, size, size, paint);
                canvas.Restore();
            }
            else
            {
                DrawFallback(canvas, size);
            }
        }

        /// <summary>Without runtime shaders: a lit gradient sphere, still breathing with the energy.</summary>
        private void DrawFallback(SKCanvas canvas, float size)
        {
            var center = new SKPoint((float)bounds.Width / 2, (float)bounds.Height / 2);
            var radius = size * 0.36f * (1 + 0.03f * energy * MathF.Sin(time * 3));
            SKColor C(Vector3 v, byte alpha = 255) => new((byte)(v.X * 255), (byte)(v.Y * 255), (byte)(v.Z * 255), alpha);

            using (var glow = new SKPaint { IsAntialias = true })
            {
                glow.Shader = SKShader.CreateRadialGradient(center, radius * 1.45f,
                    [C(b, (byte)(70 + 60 * Math.Min(1, energy))), C(b, 0)], [0.55f, 1f], SKShaderTileMode.Clamp);
                canvas.DrawCircle(center, radius * 1.45f, glow);
            }
            using var body = new SKPaint { IsAntialias = true };
            var lightAt = new SKPoint(center.X - radius * 0.35f + radius * 0.2f * MathF.Cos(time), center.Y - radius * 0.4f);
            body.Shader = SKShader.CreateRadialGradient(lightAt, radius * 1.6f, [C(c), C(a), C(b)], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawCircle(center, radius, body);
        }
    }
}

/// <summary>The orb's shader, compiled once.</summary>
internal static class OrbShader
{
    private static readonly Lazy<SKRuntimeEffect?> Compiled = new(Compile);

    public static SKRuntimeEffect? Effect => Compiled.Value;

    // Coordinates are normalised so the orb's centre is 0 and the square's edge ±1; the sphere
    // fills radius ~0.72 and the rest is room for its glow and the ripple.
    private const string Source = """
        uniform float size;
        uniform float time;
        uniform float energy;
        uniform float ring;
        uniform float breath;
        uniform float3 colA;
        uniform float3 colB;
        uniform float3 colC;

        float hash(float2 p) {
            p = fract(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return fract(p.x * p.y);
        }

        float noise(float2 p) {
            float2 i = floor(p);
            float2 f = fract(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + float2(1, 0)), u.x),
                       mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
        }

        float fbm(float2 p) {
            float v = 0.0;
            float a = 0.5;
            for (int i = 0; i < 3; i++) {
                v += a * noise(p);
                p = p * 2.03 + float2(17.0, 9.0);
                a *= 0.5;
            }
            return v;
        }

        half4 main(float2 coord) {
            float2 uv = (coord / size) * 2.0 - 1.0;
            float r = length(uv);
            float ang = atan(uv.y, uv.x);

            // The outline breathes, and turns blobby the busier it is: low lobes that drift
            // round, and a finer ripple on top once there's energy to spare.
            float wobble = (0.010 + 0.045 * energy) * (sin(ang * 3.0 + time * 1.7) + 0.6 * sin(ang * 5.0 - time * 2.3))
                         + 0.03 * energy * energy * sin(ang * 7.0 + time * 3.1);
            float radius = 0.64 + breath + wobble;
            float aa = 2.4 / size;
            float inside = 1.0 - smoothstep(radius - aa, radius + aa, r);
            float rr = clamp(r / radius, 0.0, 1.0);

            // Ribbons of colour swirling around the centre: the inside turns faster than the rim,
            // and noise warped by noise keeps it from ever repeating.
            float twist = time * 0.55 + (1.0 - rr) * 2.2;
            float2 q = float2(uv.x * cos(twist) - uv.y * sin(twist), uv.x * sin(twist) + uv.y * cos(twist));
            float n = fbm(q * 1.5 + float2(time * 0.35, -time * 0.25));
            // A second ribbon from the twisted angle itself: cheap, and it keeps the colours moving apart.
            float band = 0.5 + 0.5 * sin(atan(q.y, q.x) * 2.0 + n * 4.0 - time * 0.8);
            float3 col = mix(colA, colB, smoothstep(0.30, 0.70, n));
            col = mix(col, colC, smoothstep(0.55, 0.95, band) * 0.85);

            // Lit like a glass bead: an inner glow, a darker body towards the rim, a bright thin
            // rim, and a small specular highlight top-left.
            col += 0.30 * (1.0 - rr) * (1.0 - rr) * mix(colC, float3(1.0), 0.35);
            col *= 0.70 + 0.30 * (1.0 - rr * rr);
            col += 0.55 * pow(rr, 7.0) * mix(colB, float3(1.0), 0.5);
            float spec = smoothstep(0.30, 0.0, length(uv - float2(-0.24, -0.30)));
            col += 0.55 * spec * spec;
            col = clamp(col, 0.0, 1.0);

            // Halo outside the sphere, stronger with energy, and exactly nothing by the edge of
            // the square: a faint dark tail there would show as a box on the translucent card.
            float outside = max(r - radius, 0.0);
            float fade = 1.0 - smoothstep(0.86, 0.99, r);
            float halo = exp(-outside * (10.0 - 4.0 * energy)) * (0.30 + 0.45 * energy) * (1.0 - inside) * fade;

            // The "done" ripple: a ring that travels out and fades.
            float ringR = radius + ring * 0.30;
            float ringA = (1.0 - ring) * (1.0 - ring) * smoothstep(0.035, 0.0, abs(r - ringR)) * (1.0 - inside) * fade;

            float3 glowCol = mix(colB, colC, 0.5);
            float alpha = clamp(inside + halo + 0.8 * ringA, 0.0, 1.0);
            float3 rgb = col * inside + glowCol * halo + mix(glowCol, float3(1.0), 0.5) * 0.8 * ringA;
            return half4(half3(rgb), half(alpha));
        }
        """;

    private static SKRuntimeEffect? Compile()
    {
        try
        {
            var effect = SKRuntimeEffect.CreateShader(Source, out var errors);
            if (effect == null) Trace.WriteLine($"[assistant] orb shader failed to compile: {errors}");
            return effect;
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[assistant] runtime shaders unavailable: {e.Message}");
            return null;
        }
    }
}
