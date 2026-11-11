using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.Ambience;
using YAPA.Avalonia.Audio.Library;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;

namespace YAPA.Avalonia.Settings;

/// <summary>Lets pages ask the hosting window for a file without knowing about windows.</summary>
public interface IFilePicker
{
    Task<string?> PickAudioFileAsync(string title);
}

/// <summary>A file picker that fails (no desktop portal, a crashed dialog) behaves like a cancel.</summary>
internal static class SafePick
{
    public static async Task<string?> Run(Func<IFilePicker?> picker, string title)
    {
        try
        {
            return picker() is { } p ? await p.PickAudioFileAsync(title) : null;
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"[settings] file picker failed: {e.Message}");
            return null;
        }
    }
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
        var path = await SafePick.Run(_picker, $"Sound for “{Label}”");
        if (path != null)
        {
            _settings.Set(Cue, SoundRef.File(path));
            _effects.Preview(SoundRef.File(path));
        }
        // Rebuild either way: the combo box must not be left showing "Choose a file…".
        Load();
    }
}

/// <param name="Reference">Stored in settings; null for "None". Ignored for the "Choose a file…" entry.</param>
public sealed record MediaOption(string Label, string? Reference, bool IsChooser = false)
{
    public override string ToString() => Label;
}

/// <summary>
/// Background music for one phase: none, an internet radio station, something
/// from the Sounds folder, or (the fallback) any file.
///
/// The options list is rebuilt only when it really changes (a new file was picked),
/// and the SelectedIndex setter ignores echoes and rebuild noise, so this can't fall
/// into the binding feedback loop that crashed the ambience picker.
/// </summary>
public sealed partial class MusicRowViewModel : ObservableObject
{
    private readonly Func<string?> _get;
    private readonly Action<string?> _set;
    private readonly Func<bool> _getRepeat;
    private readonly Action<bool> _setRepeat;
    private readonly Func<IFilePicker?> _picker;
    private readonly SoundEffects _effects;
    private bool _rebuilding;

    private readonly MusicSlot _slot;

    public MusicRowViewModel(MusicSlot slot, string label, Func<string?> get, Action<string?> set, Func<bool> getRepeat, Action<bool> setRepeat,
        Func<IFilePicker?> picker, SoundEffects effects)
    {
        _slot = slot;
        Label = label;
        _get = get;
        _set = set;
        _getRepeat = getRepeat;
        _setRepeat = setRepeat;
        _picker = picker;
        _effects = effects;
        Options = BuildOptions();
        _effects.MusicChanged += OnMusicChanged;
    }

    public string Label { get; }

    /// <summary>This row's music is what the timer is playing right now, so there's nothing to audition.</summary>
    public bool IsLive => _effects.LiveSlot == _slot && !_effects.IsPreviewingMusic;

    private void OnMusicChanged()
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(PreviewLabel));
        OnPropertyChanged(nameof(CanPreview));
    }

    /// <summary>The page is going away; the effects outlive it.</summary>
    public void Detach() => _effects.MusicChanged -= OnMusicChanged;

    public IReadOnlyList<MediaOption> Options { get; private set; }

    public int SelectedIndex
    {
        get
        {
            var current = _get();
            if (string.IsNullOrEmpty(current)) return 0;
            for (var i = 0; i < Options.Count; i++)
                if (!Options[i].IsChooser && Options[i].Reference == current) return i;
            return 0;
        }
        set
        {
            if (_rebuilding || value < 0 || value >= Options.Count || value == SelectedIndex) return;
            var option = Options[value];
            if (option.IsChooser)
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedIndex)));
                _ = ChooseFileAsync();
                return;
            }
            _set(option.Reference);
            // Recordings are usually ambience (rain, a café), which should keep going for
            // the whole phase; untick Loop for a track that should play once.
            if (IsFile && !_getRepeat()) { _setRepeat(true); OnPropertyChanged(nameof(Repeat)); }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFile));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(CanPreview));
            if (_previewingThis)
            {
                if (option.Reference == null) StopPreview();
                else Preview(); // keep auditioning, now the new choice
            }
        }
    }

    /// <summary>Loop only makes sense for files; a radio station just keeps playing.</summary>
    public bool IsFile => MediaLibrary.Resolve(_get()) is { Kind: not MediaKind.Radio };

    public string? Description => MediaLibrary.Resolve(_get()) switch
    {
        { Kind: MediaKind.Radio } r => r.Description,
        { Kind: MediaKind.Library } => "From your Sounds folder",
        { } f => f.Location,
        null => string.IsNullOrEmpty(_get()) ? null : "File not found",
    };

    public bool Repeat
    {
        get => _getRepeat();
        set { _setRepeat(value); OnPropertyChanged(); }
    }

    public bool CanPreview => _previewingThis || (SelectedIndex > 0 && !IsLive);
    public string PreviewLabel => _previewingThis ? "■ Stop" : IsLive ? "Playing now" : "▶ Listen";
    private bool _previewingThis;

    [RelayCommand]
    private void TogglePreview()
    {
        if (_effects.IsPreviewingMusic && _previewingThis) StopPreview();
        else Preview();
    }

    private void Preview()
    {
        if (MediaLibrary.Resolve(_get()) is not { } item) return;
        PreviewStarted?.Invoke(this);
        _effects.PreviewMusic(item);
        _previewingThis = true;
        OnPropertyChanged(nameof(PreviewLabel));
    }

    public void StopPreview()
    {
        if (_previewingThis) _effects.StopMusicPreview();
        _previewingThis = false;
        OnPropertyChanged(nameof(PreviewLabel));
    }

    /// <summary>One audition at a time: the page tells the other rows they've been replaced.</summary>
    public event Action<MusicRowViewModel>? PreviewStarted;

    /// <summary>Another row took over the player; just update the button.</summary>
    public void PreviewReplaced()
    {
        _previewingThis = false;
        OnPropertyChanged(nameof(PreviewLabel));
    }

    private IReadOnlyList<MediaOption> BuildOptions()
    {
        var options = new List<MediaOption> { new("None", null) };
        options.AddRange(MediaLibrary.Radio.Select(r => new MediaOption($"Radio · {r.Name}", r.Reference)));
        var library = MediaLibrary.LibraryFiles();
        options.AddRange(library.Select(f => new MediaOption($"Sounds · {f.Name}", f.Reference)));

        // A file chosen from elsewhere keeps its own entry so the dropdown can show it.
        var current = _get();
        if (!string.IsNullOrEmpty(current) && options.All(o => o.Reference != current)
            && MediaLibrary.Resolve(current) is { } file)
            options.Add(new MediaOption($"File · {file.Name}", current));

        options.Add(new MediaOption("Choose a file…", null, IsChooser: true));
        return options;
    }

    /// <summary>Replaces the list only if it changed, so a rescan doesn't disturb an open dropdown.</summary>
    public void Rebuild()
    {
        var options = BuildOptions();
        if (!options.SequenceEqual(Options))
        {
            _rebuilding = true;
            try
            {
                Options = options;
                OnPropertyChanged(nameof(Options));
            }
            finally
            {
                _rebuilding = false;
            }
        }
        Refresh();
    }

    private async Task ChooseFileAsync()
    {
        var path = await SafePick.Run(_picker, $"Music while “{Label}”");
        if (path == null) return;
        _set(path);
        if (!_getRepeat()) _setRepeat(true); // a single track should usually keep going
        Rebuild();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(IsFile));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Repeat));
        OnPropertyChanged(nameof(CanPreview));
    }
}

public sealed class SoundsPageViewModel : SettingsPageViewModel
{
    private readonly PomodoroEngineSettings _engine;
    private readonly SoundSettings _sounds;
    private readonly AvaloniaMusicPlayerSettings _music;
    private readonly SoundEffects _effects;

    private readonly AmbienceSettings _ambience;

    public SoundsPageViewModel(PomodoroEngineSettings engine, SoundSettings sounds, AvaloniaMusicPlayerSettings music, SoundEffects effects,
        AmbienceSettings ambience, AmbienceEffects ambienceEffects)
    {
        _engine = engine;
        _sounds = sounds;
        _music = music;
        _effects = effects;
        _ambience = ambience;
        Ambience = new AmbienceSectionViewModel(ambience, ambienceEffects, () => FilePicker);

        Cues = SoundSettings.All.Select(c => new CueRowViewModel(c, sounds, effects, () => FilePicker)).ToList();
        Music =
        [
            new MusicRowViewModel(MusicSlot.Work, "During pomodoros", () => music.WorkSong, v => music.WorkSong = v,
                () => music.RepeatWorkSong, v => music.RepeatWorkSong = v, () => FilePicker, effects),
            new MusicRowViewModel(MusicSlot.Break, "During breaks", () => music.BreakSong, v => music.BreakSong = v,
                () => music.RepeatBreakSong, v => music.RepeatBreakSong = v, () => FilePicker, effects),
            new MusicRowViewModel(MusicSlot.LongBreak, "During the long break", () => music.SessionBreakSong, v => music.SessionBreakSong = v,
                () => music.RepeatSessionBreakSong, v => music.RepeatSessionBreakSong = v, () => FilePicker, effects),
        ];
        foreach (var row in Music)
            row.PreviewStarted += started => { foreach (var other in Music) if (other != started) other.PreviewReplaced(); };
    }

    public override string Title => "Sounds";
    public override SettingsPage Page => SettingsPage.Sounds;

    /// <summary>Set by the hosting window.</summary>
    public IFilePicker? FilePicker { get; set; }

    public AmbienceSectionViewModel Ambience { get; }

    public IReadOnlyList<CueRowViewModel> Cues { get; }
    public IReadOnlyList<MusicRowViewModel> Music { get; }

    public bool SoundEnabled
    {
        get => !_engine.DisableSoundNotifications;
        set
        {
            _engine.DisableSoundNotifications = !value; // SoundEffects stops or restarts the music
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

    public string LibraryFolder
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folder = MediaLibrary.Folder;
            return home.Length > 0 && folder.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + folder[home.Length..] : folder;
        }
    }

    /// <summary>
    /// The way to add your own sounds without a file dialog: drop files in this folder and
    /// they appear in every picker by name.
    /// </summary>
    public CommunityToolkit.Mvvm.Input.IRelayCommand OpenLibraryFolderCommand => _openFolder ??= new RelayCommand(OpenLibraryFolder);
    private RelayCommand? _openFolder;

    private static void OpenLibraryFolder()
    {
        MediaLibrary.LibraryFiles(); // creates the folder if needed
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { MediaLibrary.Folder }, UseShellExecute = false });
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"[library] can't open {MediaLibrary.Folder}: {e.Message}");
        }
    }

    /// <summary>Picks up files dropped into the Sounds folder since the page was built.</summary>
    public void RescanLibrary()
    {
        foreach (var m in Music) m.Rebuild();
        Ambience.Rebuild();
    }

    /// <summary>Called when the settings window closes: nothing auditioned keeps playing.</summary>
    public void EndPreviews()
    {
        foreach (var m in Music) m.StopPreview();
        Ambience.EndPreview();
    }

    /// <summary>The window closed: stop listening to the long-lived effects.</summary>
    public void Detach()
    {
        foreach (var m in Music) m.Detach();
        Ambience.Detach();
    }

    protected override void ResetToDefaults()
    {
        _sounds.ResetToDefaults();
        _music.ResetToDefaults();
        _engine.DisableSoundNotifications = false;
        _engine.Volume = 0.5;
        foreach (var c in Cues) c.Load();
        foreach (var m in Music) { m.StopPreview(); m.Rebuild(); }
        Ambience.EndPreview();
        _ambience.ResetToDefaults();
        Ambience.Refresh();
    }
}

public sealed class SoundsPageFactory(PomodoroEngineSettings engine, SoundSettings sounds, AvaloniaMusicPlayerSettings music, SoundEffects effects,
    AmbienceSettings ambience, AmbienceEffects ambienceEffects)
{
    public SoundsPageViewModel Create() => new(engine, sounds, music, effects, ambience, ambienceEffects);
}

/// <param name="FilePath">For a recording: which file. Null for generated sounds and the chooser.</param>
public sealed record AmbienceOption(string Label, AmbientSound Sound, string? FilePath = null, bool IsChooser = false)
{
    public override string ToString() => Label;
}

/// <summary>The "Ambience while you work" section of the Sounds page.</summary>
public sealed partial class AmbienceSectionViewModel : ObservableObject
{
    private static readonly AmbientSound[] Generated =
        [AmbientSound.None, AmbientSound.SoftRain, AmbientSound.SteadyRain, AmbientSound.Downpour, AmbientSound.BrownNoise];

    private readonly AmbienceSettings _s;
    private readonly AmbienceEffects _effects;
    private readonly Func<IFilePicker?> _picker;
    private bool _rebuilding;

    public AmbienceSectionViewModel(AmbienceSettings settings, AmbienceEffects effects, Func<IFilePicker?> picker)
    {
        _s = settings;
        _effects = effects;
        _picker = picker;
        Options = BuildOptions();
        _effects.Changed += OnEffectsChanged;
    }

    private void OnEffectsChanged()
    {
        OnPropertyChanged(nameof(PreviewLabel));
        OnPropertyChanged(nameof(CanPreview));
    }

    /// <summary>The page is going away; the effects outlive it.</summary>
    public void Detach() => _effects.Changed -= OnEffectsChanged;

    /// <summary>
    /// The same instance on every read, replaced only by <see cref="Rebuild"/>. A fresh list
    /// per read makes the ComboBox reset its selection, which re-enters SelectedIndex, which
    /// used to refresh the list again, until the stack overflowed and took the app down.
    /// </summary>
    public IReadOnlyList<AmbienceOption> Options { get; private set; }

    private IReadOnlyList<AmbienceOption> BuildOptions()
    {
        var options = Generated.Select(g => new AmbienceOption(AmbienceGenerators.DisplayName(g), g)).ToList();
        options.AddRange(MediaLibrary.LibraryFiles().Select(f => new AmbienceOption($"Sounds · {f.Name}", AmbientSound.File, f.Location)));
        if (!string.IsNullOrEmpty(_s.FilePath) && options.All(o => o.FilePath != _s.FilePath))
            options.Add(new AmbienceOption($"File · {MediaLibrary.NameFor(_s.FilePath)}", AmbientSound.File, _s.FilePath));
        options.Add(new AmbienceOption("Choose a file…", AmbientSound.File, IsChooser: true));
        return options;
    }

    public int SelectedIndex
    {
        get
        {
            for (var i = 0; i < Options.Count; i++)
            {
                var o = Options[i];
                if (o.IsChooser || o.Sound != _s.Sound) continue;
                if (o.Sound != AmbientSound.File || o.FilePath == _s.FilePath) return i;
            }
            return 0;
        }
        set
        {
            // Re-entrancy guard: controls echo values back while they update, and clear
            // their selection while the list is being replaced.
            if (_rebuilding || value < 0 || value >= Options.Count || value == SelectedIndex) return;
            var choice = Options[value];
            if (choice.IsChooser)
            {
                // Show the real current sound again right away; it only changes once a file
                // is actually picked. Posted, so it runs after the ComboBox has finished its
                // own selection change.
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedIndex)));
                _ = ChooseFileAsync();
                return;
            }
            if (choice.FilePath != null) _s.FilePath = choice.FilePath;
            _s.Sound = choice.Sound;
            OnPropertyChanged();
            OnSoundChanged();
        }
    }

    public bool HasSound => _s.Sound != AmbientSound.None;

    /// <summary>A recording, as opposed to a generated sound.</summary>
    public bool IsFile => _s.Sound == AmbientSound.File;
    public string FileName => MediaLibrary.Resolve(_s.FilePath) switch
    {
        { Kind: MediaKind.Library } => "From your Sounds folder",
        { } f => f.Location,
        null => string.IsNullOrEmpty(_s.FilePath) ? "No file chosen" : "File not found: " + _s.FilePath,
    };

    public double Volume { get => _s.Volume; set { _s.Volume = Math.Round(value, 2); OnPropertyChanged(); } }
    public bool Ticking
    {
        get => _s.Ticking;
        set
        {
            if (value == _s.Ticking) return;
            _s.Ticking = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPreview));
        }
    }
    public double TickVolume { get => _s.TickVolume; set { _s.TickVolume = Math.Round(value, 2); OnPropertyChanged(); } }
    public bool DuringBreaks { get => _s.DuringBreaks; set { _s.DuringBreaks = value; OnPropertyChanged(); } }
    public bool WindDownBeforeEnd { get => _s.WindDownBeforeEnd; set { _s.WindDownBeforeEnd = value; OnPropertyChanged(); } }

    /// <summary>
    /// During a pomodoro the ambience is already playing and follows every change here,
    /// so auditioning it would add nothing.
    /// </summary>
    public string PreviewLabel => _effects.IsPreviewing ? "■ Stop" : _effects.IsLive ? "Playing now" : "▶ Try it";
    public bool CanPreview => _effects.IsPreviewing || (_s.HasAnything && !_effects.IsLive);

    [RelayCommand]
    private void TogglePreview()
    {
        if (_effects.IsPreviewing) _effects.StopPreview();
        else _effects.Preview();
        OnPropertyChanged(nameof(PreviewLabel));
    }

    /// <summary>The settings window closing must not leave a preview playing.</summary>
    public void EndPreview()
    {
        if (_effects.IsPreviewing) _effects.StopPreview();
        OnPropertyChanged(nameof(PreviewLabel));
    }

    private async Task ChooseFileAsync()
    {
        var path = await SafePick.Run(_picker, "Ambience recording (it loops)");
        if (path != null)
        {
            _s.FilePath = path;
            _s.Sound = AmbientSound.File;
            Rebuild();
        }
        // Either way the ComboBox may be showing "Choose a file…": re-sync it.
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(FileName));
        OnSoundChanged();
    }

    /// <summary>Picks up files added to the Sounds folder, or a newly chosen file.</summary>
    public void Rebuild()
    {
        var options = BuildOptions();
        if (options.SequenceEqual(Options)) return;
        _rebuilding = true;
        try
        {
            Options = options;
            OnPropertyChanged(nameof(Options));
        }
        finally
        {
            _rebuilding = false;
        }
        OnPropertyChanged(nameof(SelectedIndex));
    }

    /// <summary>After a reset: every value may have changed.</summary>
    public void Refresh()
    {
        Rebuild();
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(Ticking));
        OnPropertyChanged(nameof(TickVolume));
        OnPropertyChanged(nameof(DuringBreaks));
        OnPropertyChanged(nameof(WindDownBeforeEnd));
        OnPropertyChanged(nameof(PreviewLabel));
        OnSoundChanged();
    }

    private void OnSoundChanged()
    {
        OnPropertyChanged(nameof(HasSound));
        OnPropertyChanged(nameof(IsFile));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(CanPreview));
    }
}
