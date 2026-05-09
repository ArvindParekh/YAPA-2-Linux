using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;

namespace YAPA.Avalonia.Settings.Pages;

public partial class SoundSettingsPage : UserControl, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly PomodoroEngineSettings _engine;
    private readonly AvaloniaSoundNotificationsSettings _notif;
    private readonly AvaloniaMusicPlayerSettings _music;

    public SoundSettingsPage() : this(
        App.Bootstrapper!.Resolve<PomodoroEngineSettings>(),
        App.Bootstrapper!.Resolve<AvaloniaSoundNotificationsSettings>(),
        App.Bootstrapper!.Resolve<AvaloniaMusicPlayerSettings>()) { }

    public SoundSettingsPage(
        PomodoroEngineSettings engine,
        AvaloniaSoundNotificationsSettings notif,
        AvaloniaMusicPlayerSettings music)
    {
        _engine = engine;
        _notif  = notif;
        _music  = music;
        InitializeComponent();
        DataContext = this;
    }

    // ── Global ────────────────────────────────────────────────────────────────

    public bool DisableSoundNotifications
    {
        get => _engine.DisableSoundNotifications;
        set { _engine.DisableSoundNotifications = value; Notify(); }
    }

    public double Volume
    {
        get => _engine.Volume;
        set { _engine.Volume = value; Notify(); }
    }

    // ── Notification sounds ───────────────────────────────────────────────────

    public string PeriodStartSound
    {
        get => _notif.PeriodStartSound;
        set { _notif.PeriodStartSound = value; Notify(); }
    }

    public string PeriodEndSound
    {
        get => _notif.PeriodEndSound;
        set { _notif.PeriodEndSound = value; Notify(); }
    }

    // ── Background music ──────────────────────────────────────────────────────

    // Return empty string instead of null so TextBox bindings don't throw
    public string WorkSong
    {
        get => _music.WorkSong ?? string.Empty;
        set { _music.WorkSong = string.IsNullOrEmpty(value) ? null : value; Notify(); }
    }

    public bool RepeatWorkSong
    {
        get => _music.RepeatWorkSong;
        set { _music.RepeatWorkSong = value; Notify(); }
    }

    public string BreakSong
    {
        get => _music.BreakSong ?? string.Empty;
        set { _music.BreakSong = string.IsNullOrEmpty(value) ? null : value; Notify(); }
    }

    public bool RepeatBreakSong
    {
        get => _music.RepeatBreakSong;
        set { _music.RepeatBreakSong = value; Notify(); }
    }

    public string SessionBreakSong
    {
        get => _music.SessionBreakSong ?? string.Empty;
        set { _music.SessionBreakSong = string.IsNullOrEmpty(value) ? null : value; Notify(); }
    }

    public bool RepeatSessionBreakSong
    {
        get => _music.RepeatSessionBreakSong;
        set { _music.RepeatSessionBreakSong = value; Notify(); }
    }
}
