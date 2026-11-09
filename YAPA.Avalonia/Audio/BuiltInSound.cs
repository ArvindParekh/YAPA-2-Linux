using System;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Sounds that ship with YAPA. The two classic ones are the original YAPA WAV files;
/// the rest are synthesized at runtime (see <see cref="SoundSynthesizer"/>).
/// </summary>
public enum BuiltInSound
{
    ClassicTick,
    ClassicDing,
    ChimeUp,
    ChimeDown,
    SoftTap,
    Bell,
    BrightBell,
}

internal static class BuiltInSoundNames
{
    // Spelled out rather than derived from the enum member names so that renaming a member
    // can never silently change what is stored in users' settings files.
    public static string ToKebab(BuiltInSound sound) => sound switch
    {
        BuiltInSound.ClassicTick => "classic-tick",
        BuiltInSound.ClassicDing => "classic-ding",
        BuiltInSound.ChimeUp => "chime-up",
        BuiltInSound.ChimeDown => "chime-down",
        BuiltInSound.SoftTap => "soft-tap",
        BuiltInSound.Bell => "bell",
        BuiltInSound.BrightBell => "bright-bell",
        _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, null),
    };

    public static string DisplayName(BuiltInSound sound) => sound switch
    {
        BuiltInSound.ClassicTick => "Classic tick",
        BuiltInSound.ClassicDing => "Classic ding",
        BuiltInSound.ChimeUp => "Chime up",
        BuiltInSound.ChimeDown => "Chime down",
        BuiltInSound.SoftTap => "Soft tap",
        BuiltInSound.Bell => "Bell",
        BuiltInSound.BrightBell => "Bright bell",
        _ => sound.ToString(),
    };

    public static bool TryParseKebab(string name, out BuiltInSound sound)
    {
        foreach (var candidate in Enum.GetValues<BuiltInSound>())
        {
            if (string.Equals(ToKebab(candidate), name, StringComparison.OrdinalIgnoreCase))
            {
                sound = candidate;
                return true;
            }
        }

        sound = default;
        return false;
    }
}
