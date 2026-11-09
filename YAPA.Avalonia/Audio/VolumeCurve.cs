using System;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Maps the 0..1 volume slider to a linear amplitude gain.
///
/// A linear slider feels wrong because hearing is roughly logarithmic: the top half of a
/// linear slider changes almost nothing audible and the bottom tenth does everything.
/// Stevens' power law puts perceived loudness at about sound pressure^0.6, so making
/// loudness proportional to the slider position means gain = slider^(1/0.6) = slider^(5/3).
///
/// That puts the halfway point at about -10 dB, the textbook "half as loud", and still
/// reaches -33 dB at 10% and true silence at 0. PulseAudio's own sliders use a cubic
/// (-18 dB at halfway); 5/3 was chosen over it because YAPA's default volume is 0.5 and the
/// cues should still be clearly audible there on laptop speakers.
/// </summary>
public static class VolumeCurve
{
    public const double Exponent = 5.0 / 3.0;

    public static double ToGain(double slider)
    {
        if (double.IsNaN(slider) || slider <= 0)
            return 0;
        return slider >= 1 ? 1 : Math.Pow(slider, Exponent);
    }

    public static double ToDecibels(double slider)
    {
        var gain = ToGain(slider);
        return gain <= 0 ? double.NegativeInfinity : 20 * Math.Log10(gain);
    }
}
