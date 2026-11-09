using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;

namespace YAPA.Avalonia.Settings;

/// <summary>Lets pages ask the hosting window for a file without knowing about windows.</summary>
public interface IFilePicker
{
    Task<string?> PickAudioFileAsync(string title);
}

public sealed record SoundOption(string Label, SoundRef Sound, bool IsCustomPrompt = false)
{
    public override string ToString() => Label;
}

/// <summary>One "event → sound" line on the Sounds page.</summary>
public sealed partial class CueRowViewModel : ObservableObject
{
    private static readonly SoundOption CustomPrompt = new("Choose a file…", SoundRef.None, IsCustomPrompt: true);

    private readonly SoundSettings _settings;
    private readonly SoundEffects _effects;
    private readonly Func<IFilePicker?> _picker;
    private SoundOption? _selected;
    private bool _updating;

    public CueRowViewModel(SoundCue cue, SoundSettings settings, SoundEffects effects, Func<IFilePicker?> picker)
    {
        Cue = cue;
        _settings = settings;
        _effects = effects;
        _picker = picker;
        Load();
    }

    public SoundCue Cue { get; }
    public string Label => SoundSettings.Label(Cue);
    public ObservableCollection<SoundOption> Options { get; } = [];

    public SoundOption? Selected
    {
        get => _selected;
        set
        {
            if (_updating || value == null || ReferenceEquals(value, _selected)) return;
            if (value.IsCustomPrompt)
            {
                _ = ChooseFileAsync();
                return;
            }
            _selected = value;
            _settings.Set(Cue, value.Sound);
            OnPropertyChanged();
            _effects.Preview(value.Sound);
        }
    }

    public void Load()
    {
        _updating = true;
        try
        {
            var current = _settings.Get(Cue);
            Options.Clear();
            Options.Add(new SoundOption("None", SoundRef.None));
            foreach (var builtIn in SoundRef.BuiltIns) Options.Add(new SoundOption(builtIn.DisplayName, builtIn));
            if (current.Kind == SoundRefKind.File)
                Options.Add(new SoundOption(Path.GetFileName(current.FilePath!), current));
            Options.Add(CustomPrompt);

            _selected = Options.FirstOrDefault(o => !o.IsCustomPrompt && o.Sound == current) ?? Options[0];
        }
        finally
        {
            _updating = false;
        }
        OnPropertyChanged(nameof(Selected));
    }

    [RelayCommand]
    private void Preview()
    {
        if (_selected is { } s) _effects.Preview(s.Sound);
    }

    private async Task ChooseFileAsync()
    {
        var path = _picker() is { } picker ? await picker.PickAudioFileAsync($"Sound for “{Label}”") : null;
        if (path != null)
        {
            _settings.Set(Cue, SoundRef.File(path));
            _effects.Preview(SoundRef.File(path));
        }
        // Rebuild either way: the combo box must not be left showing "Choose a file…".
        Load();
    }
}

/// <summary>Background music for one phase.</summary>
public sealed partial class MusicRowViewModel(string label, Func<string?> get, Action<string?> set,
    Func<bool> getRepeat, Action<bool> setRepeat, Func<IFilePicker?> picker) : ObservableObject
{
    public string Label { get; } = label;

    public string FileName => get() is { Length: > 0 } p ? Path.GetFileName(p) : "None";
    public bool HasFile => !string.IsNullOrEmpty(get());

    public bool Repeat
    {
        get => getRepeat();
        set { setRepeat(value); OnPropertyChanged(); }
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (picker() is not { } p) return;
        var path = await p.PickAudioFileAsync($"Music while “{Label}”");
        if (path == null) return;
        set(path);
        Refresh();
    }

    [RelayCommand]
    private void Clear()
    {
        set(null);
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(Repeat));
    }
}

public sealed class SoundsPageViewModel : SettingsPageViewModel
{
    private readonly PomodoroEngineSettings _engine;
    private readonly SoundSettings _sounds;
    private readonly AvaloniaMusicPlayerSettings _music;
    private readonly SoundEffects _effects;

    public SoundsPageViewModel(PomodoroEngineSettings engine, SoundSettings sounds, AvaloniaMusicPlayerSettings music, SoundEffects effects)
    {
        _engine = engine;
        _sounds = sounds;
        _music = music;
        _effects = effects;

        Cues = SoundSettings.All.Select(c => new CueRowViewModel(c, sounds, effects, () => FilePicker)).ToList();
        Music =
        [
            new MusicRowViewModel("During pomodoros", () => music.WorkSong, v => music.WorkSong = v,
                () => music.RepeatWorkSong, v => music.RepeatWorkSong = v, () => FilePicker),
            new MusicRowViewModel("During breaks", () => music.BreakSong, v => music.BreakSong = v,
                () => music.RepeatBreakSong, v => music.RepeatBreakSong = v, () => FilePicker),
            new MusicRowViewModel("During the long break", () => music.SessionBreakSong, v => music.SessionBreakSong = v,
                () => music.RepeatSessionBreakSong, v => music.RepeatSessionBreakSong = v, () => FilePicker),
        ];
    }

    public override string Title => "Sounds";
    public override SettingsPage Page => SettingsPage.Sounds;

    /// <summary>Set by the hosting window.</summary>
    public IFilePicker? FilePicker { get; set; }

    public IReadOnlyList<CueRowViewModel> Cues { get; }
    public IReadOnlyList<MusicRowViewModel> Music { get; }

    public bool SoundEnabled
    {
        get => !_engine.DisableSoundNotifications;
        set
        {
            _engine.DisableSoundNotifications = !value;
            if (!value) _effects.Music.Stop();
            Changed();
        }
    }

    public double Volume
    {
        get => _engine.Volume;
        set { _engine.Volume = Math.Round(value, 2); Changed(); Changed(nameof(VolumeText)); }
    }

    public string VolumeText => $"{Volume:P0}";

    /// <summary>Plays the completion bell at the current volume, so the slider can be tuned by ear.</summary>
    public void PreviewVolume() => _effects.Preview(_sounds.Get(SoundCue.WorkCompleted));

    public string Backend => $"Playing through {_effects.Cues.BackendDescription}";

    protected override void ResetToDefaults()
    {
        _sounds.ResetToDefaults();
        _music.ResetToDefaults();
        _engine.DisableSoundNotifications = false;
        _engine.Volume = 0.5;
        foreach (var c in Cues) c.Load();
        foreach (var m in Music) m.Refresh();
    }
}

public sealed class SoundsPageFactory(PomodoroEngineSettings engine, SoundSettings sounds, AvaloniaMusicPlayerSettings music, SoundEffects effects)
{
    public SoundsPageViewModel Create() => new(engine, sounds, music, effects);
}
