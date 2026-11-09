using System;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Renders the built-in cues from code instead of shipping audio files, so they can't go
/// missing from an install and cost nothing in package size.
///
/// Every sound is a struck instrument modelled additively: each strike is a sum of sine
/// partials, each with its own exponential decay. Higher partials die faster, which is what
/// makes a struck bar or bell sound "soft" rather than electronic. Notes come from C major
/// (plus A4) so cues that play back to back never clash.
///
/// The output is fully deterministic: no noise sources, no randomness, so a sound is the
/// same on every machine and the tests can pin it down.
/// </summary>
internal static class SoundSynthesizer
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>-3 dBFS: leaves headroom for resampling overshoot in the sound server.</summary>
    public const float TargetPeak = 0.7079f;

    /// <param name="Ratio">Frequency relative to the strike's fundamental.</param>
    /// <param name="DecayScale">Multiplies the strike's decay time; below 1 for upper partials.</param>
    /// <param name="DetuneHz">Left/right channels are detuned by ±half this, giving a slow beat
    /// between the ears. That shimmer is the difference between "bell" and "sine wave".</param>
    private readonly record struct Partial(double Ratio, double Amplitude, double DecayScale, double DetuneHz);

    /// <param name="Pan">-1 (left) to 1 (right); kept small so cues stay centred on headphones.</param>
    private readonly record struct Strike(double StartSeconds, double FrequencyHz, double Gain, double DecaySeconds, double Pan);

    private sealed record Recipe(double DurationSeconds, double AttackSeconds, double ReleaseSeconds, Partial[] Timbre, Strike[] Strikes);

    // Tuned-bar modes. A marimba bar is undercut so its 2nd mode sits near 4x; the third mode
    // is weak and very short, which gives the woody "tok" rather than a ring.
    private static readonly Partial[] Marimba =
    {
        new(1.00, 1.00, 1.00, 0.0),
        new(3.93, 0.22, 0.30, 0.0),
        new(10.2, 0.04, 0.12, 0.0),
    };

    // Vibraphone-like: same bar modes, longer sustain, and a little detune for warmth.
    private static readonly Partial[] Vibes =
    {
        new(1.00, 1.00, 1.00, 0.7),
        new(4.00, 0.16, 0.28, 1.3),
        new(10.0, 0.035, 0.10, 0.0),
    };

    // Handbell: strong fundamental and twelfth (3x), plus weak inharmonic upper modes
    // that give the metallic colour without the harshness of a church bell's minor third.
    private static readonly Partial[] Handbell =
    {
        new(1.00, 1.00, 1.00, 0.9),
        new(2.00, 0.28, 0.75, 1.4),
        new(3.00, 0.42, 0.55, 1.9),
        new(4.20, 0.12, 0.38, 0.0),
        new(5.43, 0.07, 0.25, 0.0),
    };

    // Glass / celesta: almost pure, with faint harmonics; reads as "bright" without being shrill.
    private static readonly Partial[] Glass =
    {
        new(1.00, 1.00, 1.00, 1.1),
        new(2.00, 0.22, 0.60, 0.0),
        new(3.00, 0.10, 0.40, 0.0),
        new(5.40, 0.035, 0.20, 0.0),
    };

    private const double C5 = 523.25, D5 = 587.33, G5 = 783.99, C6 = 1046.50, E6 = 1318.51, G6 = 1567.98, A4 = 440.00;

    public static bool CanSynthesize(BuiltInSound sound) => RecipeFor(sound) is not null;

    /// <summary>Returns null for the classic sounds, which are files rather than recipes.</summary>
    public static PcmBuffer? Render(BuiltInSound sound)
        => RecipeFor(sound) is { } recipe ? Render(recipe) : null;

    private static Recipe? RecipeFor(BuiltInSound sound) => sound switch
    {
        // Pause: one short, dry marimba tap. Deliberately the least "eventful" sound.
        BuiltInSound.SoftTap => new Recipe(0.20, 0.003, 0.030, Marimba, new[]
        {
            new Strike(0.00, D5, 1.0, 0.055, 0.0),
        }),

        // Start/resume: rising fourth G5 -> C6, landing on the tonic: "off we go".
        BuiltInSound.ChimeUp => new Recipe(0.72, 0.004, 0.080, Vibes, new[]
        {
            new Strike(0.00, G5, 0.85, 0.26, -0.15),
            new Strike(0.12, C6, 1.00, 0.30, 0.15),
        }),

        // Stop: falling fifth G5 -> C5. Resolves downward, so it sounds final, not alarming.
        BuiltInSound.ChimeDown => new Recipe(0.72, 0.004, 0.080, Vibes, new[]
        {
            new Strike(0.00, G5, 0.85, 0.26, 0.15),
            new Strike(0.12, C5, 1.00, 0.34, -0.15),
        }),

        // Work completed: a single warm handbell on A4, the lowest and longest cue.
        BuiltInSound.Bell => new Recipe(0.90, 0.005, 0.150, Handbell, new[]
        {
            new Strike(0.00, A4, 1.0, 0.42, 0.0),
        }),

        // Break completed: quick C-E-G arpeggio on glass, high and light: "back to it".
        BuiltInSound.BrightBell => new Recipe(0.90, 0.003, 0.120, Glass, new[]
        {
            new Strike(0.00, C6, 0.80, 0.28, -0.20),
            new Strike(0.09, E6, 0.75, 0.28, 0.00),
            new Strike(0.18, G6, 0.85, 0.34, 0.20),
        }),

        _ => null,
    };

    private static PcmBuffer Render(Recipe recipe)
    {
        var frames = (int)Math.Round(recipe.DurationSeconds * SampleRate);
        var left = new double[frames];
        var right = new double[frames];

        foreach (var strike in recipe.Strikes)
            foreach (var partial in recipe.Timbre)
                AddPartial(left, right, strike, partial, recipe.AttackSeconds);

        var peak = 0.0;
        for (var i = 0; i < frames; i++)
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        var scale = peak > 0 ? TargetPeak / peak : 0;

        // A raised-cosine release over the tail. The decays have already brought the level
        // down by then; this makes the final sample exactly zero so the cut is inaudible.
        var releaseFrames = Math.Min(frames, (int)Math.Round(recipe.ReleaseSeconds * SampleRate));
        var releaseStart = frames - releaseFrames;

        var interleaved = new float[frames * Channels];
        for (var i = 0; i < frames; i++)
        {
            var g = scale;
            if (i >= releaseStart && releaseFrames > 1)
                g *= 0.5 + 0.5 * Math.Cos(Math.PI * (i - releaseStart) / (releaseFrames - 1));
            interleaved[2 * i] = (float)(left[i] * g);
            interleaved[2 * i + 1] = (float)(right[i] * g);
        }

        return new PcmBuffer(interleaved, SampleRate, Channels);
    }

    private static void AddPartial(double[] left, double[] right, Strike strike, Partial partial, double attackSeconds)
    {
        var baseHz = strike.FrequencyHz * partial.Ratio;
        // Anything near Nyquist would alias into an audible, unrelated pitch.
        if (baseHz + partial.DetuneHz > SampleRate * 0.45)
            return;

        var startFrame = (int)Math.Round(strike.StartSeconds * SampleRate);
        var tau = strike.DecaySeconds * partial.DecayScale;
        var amplitude = strike.Gain * partial.Amplitude;

        // Equal-power pan so a panned strike isn't quieter than a centred one.
        var panAngle = (Math.Clamp(strike.Pan, -1, 1) + 1) * Math.PI / 4;
        var gains = (L: Math.Cos(panAngle) * Math.Sqrt(2), R: Math.Sin(panAngle) * Math.Sqrt(2));
        var hz = (L: baseHz - partial.DetuneHz / 2, R: baseHz + partial.DetuneHz / 2);
        var attackFrames = Math.Max(1, attackSeconds * SampleRate);

        for (var n = startFrame; n < left.Length; n++)
        {
            var k = n - startFrame;
            var t = (double)k / SampleRate;

            // Raised-cosine attack starting from exactly zero: a few milliseconds is enough to
            // avoid a click while still sounding struck.
            var attack = k < attackFrames ? 0.5 - 0.5 * Math.Cos(Math.PI * k / attackFrames) : 1.0;
            var env = amplitude * attack * Math.Exp(-t / tau);
            if (k >= attackFrames && env < 1e-7)
                break; // Below -140 dB: inaudible, and skipping it keeps rendering cheap.

            left[n] += gains.L * env * Math.Sin(2 * Math.PI * hz.L * t);
            right[n] += gains.R * env * Math.Sin(2 * Math.PI * hz.R * t);
        }
    }
}
