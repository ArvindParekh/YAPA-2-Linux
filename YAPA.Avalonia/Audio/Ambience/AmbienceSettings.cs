using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Audio.Ambience;

/// <summary>Background sound while you work.</summary>
public sealed class AmbienceSettings : IPluginSettings, INotifyPropertyChanged
{
    private readonly ISettingsForComponent _s;

    public AmbienceSettings(ISettings settings) => _s = settings.GetSettingsForComponent("Ambience");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(object? value, [CallerMemberName] string name = "")
    {
        _s.Update(name, value!);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// The quick on/off switch (menu, tray, CLI). Separate from <see cref="Sound"/> so
    /// muting for one session doesn't lose the chosen sound.
    /// </summary>
    public bool Enabled
    {
        get => _s.Get(nameof(Enabled), true);
        set => Set(value);
    }

    public AmbientSound Sound
    {
        get => _s.Get(nameof(Sound), AmbientSound.None);
        set => Set(value);
    }

    /// <summary>The user's own recording, for <see cref="AmbientSound.File"/>.</summary>
    public string? FilePath
    {
        get => _s.Get<string?>(nameof(FilePath), null);
        set => Set(value);
    }

    public double Volume
    {
        get => _s.Get(nameof(Volume), 0.45);
        set => Set(value);
    }

    public bool Ticking
    {
        get => _s.Get(nameof(Ticking), false);
        set => Set(value);
    }

    public double TickVolume
    {
        get => _s.Get(nameof(TickVolume), 0.4);
        set => Set(value);
    }

    /// <summary>Keep the ambience going through breaks (the ticking always stops for breaks).</summary>
    public bool DuringBreaks
    {
        get => _s.Get(nameof(DuringBreaks), false);
        set => Set(value);
    }

    public bool HasAnything => Sound != AmbientSound.None || Ticking;

    public AmbienceMix Mix(bool inBreak) => new(
        Sound,
        FilePath,
        Volume,
        // Ticking is a work sound: a ticking clock during a break is the opposite of resting.
        Ticking && !inBreak,
        TickVolume);

    public void ResetToDefaults()
    {
        Enabled = true;
        Sound = AmbientSound.None;
        FilePath = null;
        Volume = 0.45;
        Ticking = false;
        TickVolume = 0.4;
        DuringBreaks = false;
    }

    public void DeferChanges() => _s.DeferChanges();
}
