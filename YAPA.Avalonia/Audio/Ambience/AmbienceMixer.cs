using System;

namespace YAPA.Avalonia.Audio.Ambience;

/// <summary>What the bed plays: a generated sound, or a recording (<see cref="AmbientSound.File"/> + path).</summary>
internal readonly record struct AmbientBed(AmbientSound Sound, string? FilePath = null)
{
    public static implicit operator AmbientBed(AmbientSound sound) => new(sound);
}

/// <summary>
/// The interval the ambience is playing for ends at <see cref="EndsAt"/> (monotonic, the
/// timeline blocks are heard on). Over the last <see cref="Length"/> before it the whole
/// ambience thins to <see cref="Floor"/>, so the end arrives as something you hear coming
/// rather than as a bell out of nowhere. It never goes silent ahead of time: the bell and
/// the usual fade-out still mark the actual end.
/// </summary>
public readonly record struct WindDown(TimeSpan EndsAt, TimeSpan Length)
{
    /// <summary>Linear gain at the very end: about -10 dB, clearly thinner but still there.</summary>
    public const float Floor = 0.3f;

    /// <summary>The longest wind-down; short intervals get a quarter of their length.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(60);

    public static WindDown ForInterval(TimeSpan endsAt, TimeSpan intervalLength)
        => new(endsAt, intervalLength / 4 < Longest ? intervalLength / 4 : Longest);

    public float GainAt(TimeSpan heardAt)
    {
        var left = EndsAt - heardAt;
        if (left >= Length || Length <= TimeSpan.Zero) return 1f;
        if (left <= TimeSpan.Zero) return Floor;
        // Smoothstep: no corner where the ramp starts, and it settles gently onto the floor.
        var x = (float)(left / Length);
        return Floor + (1f - Floor) * x * x * (3f - 2f * x);
    }
}

/// <summary>
/// Mixes the background bed (rain, noise) with the ticking clock, with every
/// change smoothed: the session start fades in, the end fades out, switching
/// from soft to heavy rain crossfades, and volume moves glide. Abrupt gain
/// steps are what make background audio feel cheap (and they click).
///
/// Control methods may be called from any thread; <see cref="Render"/> runs on the audio thread.
/// </summary>
internal sealed class AmbienceMixer
{
    public static readonly TimeSpan FadeIn = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan FadeOut = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan Crossfade = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Below this the fade-out counts as finished. Float steps can leave the level a hair
    /// above zero, so rendering stopping and beds being released must use the same line.
    /// </summary>
    private const float Inaudible = 0.0001f;

    private readonly int _rate;
    private readonly Func<AmbientBed, IAmbienceGenerator?> _factory;
    private readonly TickGenerator _ticks;
    private readonly object _gate = new();

    // Requested state, written by control methods under _gate.
    private bool _active;
    private AmbientBed _sound;
    private float _bedTarget;
    private float _tickTarget;
    private TickAnchor _anchor;
    private WindDown? _windDown;

    // Render-thread state.
    private AmbientBed _currentSound;
    private IAmbienceGenerator? _bed;
    private IAmbienceGenerator? _outgoing;
    private float _crossfade = 1f; // 0..1, 1 = fully on _bed
    private float _master;
    private float _bedGain;
    private float _tickGain;
    private float _windGain = 1f;

    public AmbienceMixer(int sampleRate, Func<AmbientBed, IAmbienceGenerator?>? factory = null)
    {
        _rate = sampleRate;
        _factory = factory ?? (b => AmbienceGenerators.Create(b.Sound, sampleRate, (ulong)Environment.TickCount64));
        _ticks = new TickGenerator(sampleRate);
    }

    /// <param name="bedGain">Linear gain for the bed (already through the volume curve).</param>
    /// <param name="tickGain">Linear gain for the ticking; 0 turns it off.</param>
    /// <param name="anchor">Where the timer's seconds fall; ticks are placed on them. Null keeps the current one.</param>
    /// <param name="windDown">Thin out towards the end of the interval; null plays at full level throughout.</param>
    public void Set(bool active, AmbientBed sound, float bedGain, float tickGain, TickAnchor? anchor = null, WindDown? windDown = null)
    {
        lock (_gate)
        {
            _active = active;
            _sound = sound;
            _bedTarget = Math.Clamp(bedGain, 0, 1);
            _tickTarget = Math.Clamp(tickGain, 0, 1);
            if (anchor is { } a) _anchor = a;
            _windDown = windDown;
        }
    }

    /// <summary>
    /// Fades everything out but keeps the bed, so resuming within the fade carries on
    /// where it was instead of starting the bed again (for a recording: a new decode).
    /// </summary>
    public void Deactivate()
    {
        lock (_gate) _active = false;
    }

    /// <summary>True while there is anything to play, including the tail of a fade-out.</summary>
    public bool WantsAudio
    {
        get
        {
            lock (_gate) return _active || _master > Inaudible;
        }
    }

    /// <param name="heardAt">Monotonic time at which this block's first frame will be heard.</param>
    public void Render(Span<float> stereo, TimeSpan heardAt)
    {
        bool active;
        AmbientBed sound;
        float bedTarget, tickTarget;
        TickAnchor anchor;
        WindDown? windDown;
        lock (_gate)
        {
            active = _active;
            sound = _sound;
            bedTarget = _bedTarget;
            tickTarget = _tickTarget;
            anchor = _anchor;
            windDown = _windDown;
        }

        if (sound != _currentSound)
        {
            // Keep the old bed playing underneath while the new one fades in; whichever
            // of the two isn't kept is finished with.
            if (_crossfade > 0.5f)
            {
                Release(_outgoing);
                _outgoing = _bed;
            }
            else
            {
                Release(_bed);
            }
            _bed = _factory(sound);
            _currentSound = sound;
            _crossfade = 0f;
        }

        stereo.Clear();
        var frames = stereo.Length / 2;
        if (frames == 0) return;

        // Gains are ramped per block (10-20 ms): far finer than any audible step.
        var blockSeconds = frames / (float)_rate;
        var masterStep = blockSeconds / (float)(active ? FadeIn : FadeOut).TotalSeconds;
        var masterEnd = active ? Math.Min(1f, _master + masterStep) : Math.Max(0f, _master - masterStep);
        var crossEnd = Math.Min(1f, _crossfade + blockSeconds / (float)Crossfade.TotalSeconds);
        var bedEnd = Glide(_bedGain, bedTarget, blockSeconds);
        var tickEnd = Glide(_tickGain, tickTarget, blockSeconds * 4);

        // Equal-power curves for fades, so the level doesn't dip in the middle. The wind-down
        // is read at the block's middle; a 20 ms block is a tiny slice of a minute-long ramp.
        // Glided like the volumes: the ramp itself is far slower than the glide, so it's followed
        // exactly, but a wind-down that's replaced or switched off returns to full level smoothly.
        var windEnd = Glide(_windGain, windDown?.GainAt(heardAt + TimeSpan.FromSeconds(blockSeconds / 2)) ?? 1f, blockSeconds);
        var masterGain = Smooth(masterEnd) * windEnd;
        if (_bed != null) _bed.MixInto(stereo, bedEnd * masterGain * Smooth(crossEnd));
        if (_outgoing != null)
        {
            if (crossEnd >= 1f) { Release(_outgoing); _outgoing = null; }
            else _outgoing.MixInto(stereo, bedEnd * masterGain * Smooth(1f - crossEnd));
        }
        if (tickEnd > 0) _ticks.MixInto(stereo, tickEnd * masterGain, heardAt, anchor);

        _master = masterEnd;
        _crossfade = crossEnd;
        _bedGain = bedEnd;
        _tickGain = tickEnd;
        _windGain = windEnd;

        if (!active && masterEnd <= Inaudible)
        {
            _master = 0f;
            // Faded out completely: let go of the beds (a recording holds a decoder
            // process). The next session starts them afresh.
            Release(_bed);
            Release(_outgoing);
            _bed = _outgoing = null;
            _currentSound = default;
            _crossfade = 1f;
        }

        // Gentle safety limiter: rain peaks (a close drop) must never clip.
        for (var i = 0; i < stereo.Length; i++)
        {
            var x = stereo[i];
            if (x > 0.8f || x < -0.8f) stereo[i] = MathF.Tanh(x);
        }
    }

    private static void Release(IAmbienceGenerator? generator) => (generator as IDisposable)?.Dispose();

    private static float Glide(float from, float to, float seconds)
    {
        // About 0.4 s to settle, so dragging a volume slider sounds smooth.
        var step = seconds / 0.4f;
        return from < to ? Math.Min(to, from + step) : Math.Max(to, from - step);
    }

    private static float Smooth(float x) => MathF.Sin(Math.Clamp(x, 0f, 1f) * MathF.PI / 2);
}
