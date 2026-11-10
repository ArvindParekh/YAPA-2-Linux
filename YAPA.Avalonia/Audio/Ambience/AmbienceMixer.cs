using System;

namespace YAPA.Avalonia.Audio.Ambience;

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

    private readonly int _rate;
    private readonly Func<AmbientSound, IAmbienceGenerator?> _factory;
    private readonly TickGenerator _ticks;
    private readonly object _gate = new();

    // Requested state, written by control methods under _gate.
    private bool _active;
    private AmbientSound _sound;
    private float _bedTarget;
    private float _tickTarget;
    private bool _restartTicks;

    // Render-thread state.
    private AmbientSound _currentSound;
    private IAmbienceGenerator? _bed;
    private IAmbienceGenerator? _outgoing;
    private float _crossfade = 1f; // 0..1, 1 = fully on _bed
    private float _master;
    private float _bedGain;
    private float _tickGain;

    public AmbienceMixer(int sampleRate, Func<AmbientSound, IAmbienceGenerator?>? factory = null)
    {
        _rate = sampleRate;
        _factory = factory ?? (s => AmbienceGenerators.Create(s, sampleRate, (ulong)Environment.TickCount64));
        _ticks = new TickGenerator(sampleRate);
    }

    /// <param name="bedGain">Linear gain for the bed (already through the volume curve).</param>
    /// <param name="tickGain">Linear gain for the ticking; 0 turns it off.</param>
    public void Set(bool active, AmbientSound sound, float bedGain, float tickGain, bool restartTicks = false)
    {
        lock (_gate)
        {
            _active = active;
            _sound = sound;
            _bedTarget = Math.Clamp(bedGain, 0, 1);
            _tickTarget = Math.Clamp(tickGain, 0, 1);
            _restartTicks |= restartTicks;
        }
    }

    /// <summary>True while there is anything to play, including the tail of a fade-out.</summary>
    public bool WantsAudio
    {
        get
        {
            lock (_gate) return _active || _master > 0.0001f;
        }
    }

    public void Render(Span<float> stereo)
    {
        bool active;
        AmbientSound sound;
        float bedTarget, tickTarget;
        lock (_gate)
        {
            active = _active;
            sound = _sound;
            bedTarget = _bedTarget;
            tickTarget = _tickTarget;
            if (_restartTicks)
            {
                _ticks.Restart();
                _restartTicks = false;
            }
        }

        if (sound != _currentSound)
        {
            // Keep the old bed playing underneath while the new one fades in.
            _outgoing = _crossfade > 0.5f ? _bed : _outgoing;
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

        // Equal-power curves for fades, so the level doesn't dip in the middle.
        var masterGain = Smooth(masterEnd);
        if (_bed != null) _bed.MixInto(stereo, bedEnd * masterGain * Smooth(crossEnd));
        if (_outgoing != null)
        {
            if (crossEnd >= 1f) _outgoing = null;
            else _outgoing.MixInto(stereo, bedEnd * masterGain * Smooth(1f - crossEnd));
        }
        if (tickEnd > 0) _ticks.MixInto(stereo, tickEnd * masterGain);

        _master = masterEnd;
        _crossfade = crossEnd;
        _bedGain = bedEnd;
        _tickGain = tickEnd;

        // Gentle safety limiter: rain peaks (a close drop) must never clip.
        for (var i = 0; i < stereo.Length; i++)
        {
            var x = stereo[i];
            if (x > 0.8f || x < -0.8f) stereo[i] = MathF.Tanh(x);
        }
    }

    private static float Glide(float from, float to, float seconds)
    {
        // About 0.4 s to settle, so dragging a volume slider sounds smooth.
        var step = seconds / 0.4f;
        return from < to ? Math.Min(to, from + step) : Math.Max(to, from - step);
    }

    private static float Smooth(float x) => MathF.Sin(Math.Clamp(x, 0f, 1f) * MathF.PI / 2);
}
