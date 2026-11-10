using System;
using System.Collections.Generic;

namespace YAPA.Avalonia.Audio.Ambience;

/// <summary>Built-in background sounds, generated live (nothing is shipped or downloaded).</summary>
public enum AmbientSound
{
    None,
    SoftRain,
    SteadyRain,
    Downpour,
    BrownNoise,

    /// <summary>The user's own recording, looped by the media player.</summary>
    File,
}

/// <summary>Something that fills stereo frames, sample by sample, forever.</summary>
internal interface IAmbienceGenerator
{
    /// <summary>Adds (not overwrites) <paramref name="gain"/>-scaled output into interleaved stereo.</summary>
    void MixInto(Span<float> stereo, float gain);
}

/// <summary>xorshift64*: fast, decent, and seedable so renders are reproducible in tests.</summary>
internal struct FastRandom
{
    private ulong _state;

    public FastRandom(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    private ulong Next()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return _state * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float Unit() => (Next() >> 40) * (1f / (1 << 24));

    /// <summary>Uniform in [-1, 1).</summary>
    public float Bipolar() => Unit() * 2f - 1f;

    /// <summary>Exponentially distributed waiting time for a Poisson process of the given rate.</summary>
    public double Exponential(double ratePerSecond) => -Math.Log(1 - Unit() + 1e-12) / ratePerSecond;

    /// <summary>Roughly normal (sum of uniforms), mean 0, sd ≈ 1.</summary>
    public float Gaussian() => (Unit() + Unit() + Unit() + Unit() - 2f) * 1.732f;
}

/// <summary>Pink (1/f) noise via Paul Kellet's refined filter: equal energy per octave, the "natural" noise colour.</summary>
internal struct PinkNoise
{
    private float _b0, _b1, _b2, _b3, _b4, _b5, _b6;

    public float Next(float white)
    {
        _b0 = 0.99886f * _b0 + white * 0.0555179f;
        _b1 = 0.99332f * _b1 + white * 0.0750759f;
        _b2 = 0.96900f * _b2 + white * 0.1538520f;
        _b3 = 0.86650f * _b3 + white * 0.3104856f;
        _b4 = 0.55000f * _b4 + white * 0.5329522f;
        _b5 = -0.7616f * _b5 - white * 0.0168980f;
        var pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362f;
        _b6 = white * 0.115926f;
        return pink * 0.11f; // about unity RMS for unit white noise
    }
}

/// <summary>One-pole low-pass. Gentle (6 dB/oct), which is what natural absorption sounds like.</summary>
internal struct OnePole
{
    private readonly float _a;
    private float _y;

    public OnePole(double cutoffHz, int sampleRate) => _a = (float)Math.Exp(-2 * Math.PI * cutoffHz / sampleRate);

    public float LowPass(float x) => _y = x + _a * (_y - x);

    /// <summary>Input minus its low-passed self.</summary>
    public float HighPass(float x) => x - LowPass(x);
}

/// <summary>
/// Rain, built from what a microphone near a window actually hears:
/// <list type="bullet">
/// <item>a broadband "shhh" bed (pink noise, its top end rolled off: thousands of distant drops blur into noise),</item>
/// <item>a low body (brown noise), more of it the heavier the rain,</item>
/// <item>slow gusts: the bed swells and settles over several seconds,</item>
/// <item>individual near drops: short bright noise ticks, randomly loud, randomly placed in the stereo field,</item>
/// <item>occasional "plinks": drops on water or glass, a damped tone that rises slightly in pitch,
///   the signature of a bubble being trapped.</item>
/// </list>
/// Each channel draws its own randomness, which is what makes it sound wide rather than inside your head.
/// </summary>
internal sealed class RainGenerator : IAmbienceGenerator
{
    private readonly int _rate;
    private readonly RainPreset _p;
    private FastRandom _rng;
    private readonly Channel _left;
    private readonly Channel _right;

    private float _gust = 1f;
    private float _gustTarget = 1f;
    private double _untilGustChange;
    private readonly float _gustSmoothing;

    private readonly List<Drop> _drops = new(64);
    private readonly List<Plink> _plinks = new(16);
    private double _untilDrop;
    private double _untilPlink;

    public RainGenerator(RainPreset preset, int sampleRate, ulong seed = 0x5EED)
    {
        _rate = sampleRate;
        _p = preset;
        _rng = new FastRandom(seed);
        _left = new Channel(preset, sampleRate);
        _right = new Channel(preset, sampleRate);
        _gustSmoothing = (float)(1 - Math.Exp(-1.0 / (preset.GustSeconds * sampleRate)));
        _untilDrop = _rng.Exponential(preset.DropsPerSecond);
        _untilPlink = _rng.Exponential(preset.PlinksPerSecond);
    }

    public void MixInto(Span<float> stereo, float gain)
    {
        var dt = 1.0 / _rate;
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            // Gusts: a new random target every few seconds, approached smoothly.
            _untilGustChange -= dt;
            if (_untilGustChange <= 0)
            {
                _gustTarget = 1f + _p.GustDepth * _rng.Bipolar();
                _untilGustChange = _p.GustSeconds * (0.6 + _rng.Unit());
            }
            _gust += (_gustTarget - _gust) * _gustSmoothing;

            _untilDrop -= dt;
            while (_untilDrop <= 0)
            {
                SpawnDrop();
                _untilDrop += _rng.Exponential(_p.DropsPerSecond * _gust);
            }

            _untilPlink -= dt;
            while (_untilPlink <= 0)
            {
                SpawnPlink();
                _untilPlink += _rng.Exponential(_p.PlinksPerSecond);
            }

            var l = _left.Bed(ref _rng) * _gust;
            var r = _right.Bed(ref _rng) * _gust;

            for (var d = _drops.Count - 1; d >= 0; d--)
            {
                var drop = _drops[d];
                var s = drop.Next(ref _rng);
                l += s * drop.LeftGain;
                r += s * drop.RightGain;
                if (drop.Done) _drops.RemoveAt(d);
                else _drops[d] = drop;
            }

            for (var k = _plinks.Count - 1; k >= 0; k--)
            {
                var plink = _plinks[k];
                var s = plink.Next(_rate);
                l += s * plink.LeftGain;
                r += s * plink.RightGain;
                if (plink.Done) _plinks.RemoveAt(k);
                else _plinks[k] = plink;
            }

            stereo[i] += l * gain;
            stereo[i + 1] += r * gain;
        }
    }

    private void SpawnDrop()
    {
        if (_drops.Count >= 64) return; // heavy rain saturates long before this matters
        // Log-normal loudness: most drops are faint, a few land close.
        var amplitude = _p.DropLevel * (float)Math.Exp(0.7 * _rng.Gaussian());
        var pan = _rng.Bipolar() * 0.9f;
        _drops.Add(new Drop(
            amplitude: Math.Min(amplitude, _p.DropLevel * 5),
            decaySamples: (float)(_rate * (0.0012 + 0.0045 * _rng.Unit())),
            brightnessHz: 2500 + 6000 * _rng.Unit(),
            sampleRate: _rate,
            pan: pan));
    }

    private void SpawnPlink()
    {
        if (_plinks.Count >= 16) return;
        var amplitude = _p.PlinkLevel * (0.4f + 0.6f * _rng.Unit());
        _plinks.Add(new Plink(
            amplitude,
            startHz: 1100 + 2200 * _rng.Unit(),
            riseRatio: 1.0f + 0.25f * _rng.Unit(),
            decaySeconds: 0.012 + 0.03 * _rng.Unit(),
            pan: _rng.Bipolar() * 0.8f,
            sampleRate: _rate));
    }

    private sealed class Channel(RainPreset p, int rate)
    {
        private PinkNoise _pink;
        private OnePole _bedTone = new(p.BedCutoffHz, rate);
        private OnePole _bedLowCut = new(300, rate);
        private float _brown;
        private OnePole _bodyTone = new(p.BodyCutoffHz, rate);

        public float Bed(ref FastRandom rng)
        {
            var white = rng.Bipolar();
            var bed = _bedLowCut.HighPass(_bedTone.LowPass(_pink.Next(white))) * p.BedLevel;
            // Brown noise: integrated white with a leak, so it can't wander off.
            _brown = 0.997f * _brown + 0.06f * rng.Bipolar();
            var body = _bodyTone.LowPass(_brown) * p.BodyLevel;
            return bed + body;
        }
    }

    private struct Drop
    {
        private float _amplitude;
        private readonly float _decay;
        private OnePole _tone;
        private float _previous;
        public readonly float LeftGain;
        public readonly float RightGain;

        public Drop(float amplitude, float decaySamples, double brightnessHz, int sampleRate, float pan)
        {
            _amplitude = amplitude;
            _decay = (float)Math.Exp(-1.0 / decaySamples);
            _tone = new OnePole(brightnessHz, sampleRate);
            _previous = 0;
            (LeftGain, RightGain) = EqualPower(pan);
        }

        public readonly bool Done => _amplitude < 1e-5f;

        public float Next(ref FastRandom rng)
        {
            // A differentiated noise burst is a click with a bright, wet edge; the low-pass
            // then sets how close the drop sounds.
            var white = rng.Bipolar();
            var edge = white - _previous;
            _previous = white;
            var s = _tone.LowPass(edge) * _amplitude;
            _amplitude *= _decay;
            return s;
        }
    }

    private struct Plink
    {
        private float _amplitude;
        private double _phase;
        private double _frequency;
        private readonly double _glide;
        private readonly float _decay;
        public readonly float LeftGain;
        public readonly float RightGain;
        private int _age;

        public Plink(float amplitude, double startHz, float riseRatio, double decaySeconds, float pan, int sampleRate)
        {
            _amplitude = amplitude;
            _phase = 0;
            _frequency = startHz;
            // Reach riseRatio × start over the drop's lifetime.
            _glide = Math.Pow(riseRatio, 1.0 / (decaySeconds * 4 * sampleRate));
            _decay = (float)Math.Exp(-1.0 / (decaySeconds * sampleRate));
            _age = 0;
            (LeftGain, RightGain) = EqualPower(pan);
        }

        public readonly bool Done => _amplitude < 1e-5f;

        public float Next(int sampleRate)
        {
            // 2 ms raised-cosine attack: a plink that starts at full amplitude clicks.
            var attackSamples = sampleRate / 500f;
            var attack = _age < attackSamples ? 0.5f - 0.5f * MathF.Cos(MathF.PI * _age / attackSamples) : 1f;
            _age++;
            _phase += 2 * Math.PI * _frequency / sampleRate;
            _frequency *= _glide;
            var s = (float)Math.Sin(_phase) * _amplitude * attack;
            _amplitude *= _decay;
            return s;
        }
    }

    internal static (float Left, float Right) EqualPower(float pan)
    {
        var angle = (pan + 1) * MathF.PI / 4;
        return (MathF.Cos(angle), MathF.Sin(angle));
    }
}

/// <param name="BedLevel">Level of the broadband "shhh".</param>
/// <param name="BedCutoffHz">Where the bed's top end rolls off: lower sounds heavier and further away.</param>
/// <param name="BodyLevel">Low-frequency body; heavy rain has more.</param>
/// <param name="DropsPerSecond">Rate of individually audible near drops.</param>
/// <param name="PlinksPerSecond">Rate of tonal drops on water or glass.</param>
/// <param name="GustDepth">How much the intensity swells (fraction of the level).</param>
/// <param name="GustSeconds">Typical time between swells.</param>
internal sealed record RainPreset(
    float BedLevel, double BedCutoffHz, float BodyLevel, double BodyCutoffHz,
    double DropsPerSecond, float DropLevel, double PlinksPerSecond, float PlinkLevel,
    float GustDepth, double GustSeconds)
{
    // Levels calibrated by measurement (see AmbienceTests): about -26/-23/-20 dBFS RMS
    // at full volume, with roughly a third of the energy above 1.5 kHz, which is
    // where rain's "hiss" lives.
    public static readonly RainPreset Soft = new(
        BedLevel: 0.26f, BedCutoffHz: 9000, BodyLevel: 0.025f, BodyCutoffHz: 350,
        DropsPerSecond: 30, DropLevel: 0.16f, PlinksPerSecond: 3.5, PlinkLevel: 0.05f,
        GustDepth: 0.12f, GustSeconds: 7);

    public static readonly RainPreset Steady = new(
        BedLevel: 0.34f, BedCutoffHz: 7500, BodyLevel: 0.04f, BodyCutoffHz: 380,
        DropsPerSecond: 100, DropLevel: 0.12f, PlinksPerSecond: 5, PlinkLevel: 0.045f,
        GustDepth: 0.18f, GustSeconds: 6);

    public static readonly RainPreset Downpour = new(
        BedLevel: 0.44f, BedCutoffHz: 6000, BodyLevel: 0.07f, BodyCutoffHz: 420,
        DropsPerSecond: 260, DropLevel: 0.09f, PlinksPerSecond: 3, PlinkLevel: 0.035f,
        GustDepth: 0.25f, GustSeconds: 5);
}

/// <summary>Brown noise: deep and soft, the noise colour people pick for focus.</summary>
internal sealed class BrownNoiseGenerator(int sampleRate, ulong seed = 0xB120) : IAmbienceGenerator
{
    private FastRandom _rng = new(seed);
    private float _left;
    private float _right;
    // Removes the sub-audible drift a random walk always has; it would only waste headroom.
    private OnePole _dcLeft = new(20, sampleRate);
    private OnePole _dcRight = new(20, sampleRate);

    public void MixInto(Span<float> stereo, float gain)
    {
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            // Step size sets the level: about -24 dBFS RMS after the DC filter.
            _left = 0.998f * _left + 0.0095f * _rng.Bipolar();
            _right = 0.998f * _right + 0.0095f * _rng.Bipolar();
            stereo[i] += _dcLeft.HighPass(_left) * gain;
            stereo[i + 1] += _dcRight.HighPass(_right) * gain;
        }
    }
}

/// <summary>
/// A mechanical clock: alternating tick and tock, exactly one per second.
/// Each is a short burst of high, quickly damped resonances (the escapement
/// striking the pallet), the tock a little lower so the pair has a rhythm.
/// </summary>
internal sealed class TickGenerator : IAmbienceGenerator
{
    private readonly float[] _tick;
    private readonly float[] _tock;
    private readonly int _period;
    private long _position;

    public TickGenerator(int sampleRate, ulong seed = 0x71C)
    {
        _period = sampleRate;
        _tick = Strike(sampleRate, [3150, 5350, 7900], seed);
        _tock = Strike(sampleRate, [2450, 4150, 6300], seed + 1);
    }

    /// <summary>Restarts the beat now, so ticks line up with a timer that just started.</summary>
    public void Restart() => _position = 0;

    public void MixInto(Span<float> stereo, float gain)
    {
        var frames = stereo.Length / 2;
        for (var f = 0; f < frames; f++, _position++)
        {
            var beat = _position / _period;
            var offset = (int)(_position % _period);
            var strike = beat % 2 == 0 ? _tick : _tock;
            if (offset >= strike.Length) continue;
            var s = strike[offset] * gain;
            stereo[f * 2] += s;
            stereo[f * 2 + 1] += s;
        }
    }

    private static float[] Strike(int rate, double[] modes, ulong seed)
    {
        var rng = new FastRandom(seed);
        var length = (int)(rate * 0.045);
        var buffer = new float[length];
        for (var n = 0; n < length; n++)
        {
            var t = n / (double)rate;
            double s = 0;
            for (var m = 0; m < modes.Length; m++)
            {
                // Higher modes are quieter and die faster.
                s += Math.Sin(2 * Math.PI * modes[m] * t) * Math.Exp(-t / (0.006 / (m + 1))) / (m + 1);
            }
            // A sub-millisecond noise transient gives the "click" of metal on metal.
            s += rng.Bipolar() * Math.Exp(-t / 0.0004) * 0.6;
            // 0.3 ms fade-in so even the transient doesn't produce a digital click.
            var fadeIn = Math.Min(1, n / (rate * 0.0003));
            buffer[n] = (float)(s * fadeIn * 0.35);
        }
        return buffer;
    }
}

internal static class AmbienceGenerators
{
    public static IAmbienceGenerator? Create(AmbientSound sound, int sampleRate, ulong seed = 0x5EED) => sound switch
    {
        AmbientSound.SoftRain => new RainGenerator(RainPreset.Soft, sampleRate, seed),
        AmbientSound.SteadyRain => new RainGenerator(RainPreset.Steady, sampleRate, seed),
        AmbientSound.Downpour => new RainGenerator(RainPreset.Downpour, sampleRate, seed),
        AmbientSound.BrownNoise => new BrownNoiseGenerator(sampleRate, seed),
        _ => null,
    };

    public static string DisplayName(AmbientSound sound) => sound switch
    {
        AmbientSound.None => "None",
        AmbientSound.SoftRain => "Soft rain",
        AmbientSound.SteadyRain => "Steady rain",
        AmbientSound.Downpour => "Downpour",
        AmbientSound.BrownNoise => "Brown noise",
        AmbientSound.File => "Your own file",
        _ => sound.ToString(),
    };
}
