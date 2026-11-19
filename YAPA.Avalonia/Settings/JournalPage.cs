using CommunityToolkit.Mvvm.Input;
using YAPA.Avalonia.Journal;
using YAPA.Avalonia.Platform.Linux.X11;
using YAPA.Avalonia.Services;

namespace YAPA.Avalonia.Settings;

/// <summary>
/// What the day journal reads, said plainly, each source with its own switch; the ignore
/// list; and a way to forget everything it recorded.
/// </summary>
public sealed class JournalPageViewModel : SettingsPageViewModel
{
    private readonly JournalSettings _settings;
    private readonly JournalService _journal;
    private bool _confirmingForget;

    public JournalPageViewModel(JournalSettings settings, JournalService journal)
    {
        _settings = settings;
        _journal = journal;
        OpenDayCommand = new RelayCommand(() => App.Bootstrapper?.Resolve<AppNavigator>().ShowDay());
        ForgetCommand = new RelayCommand(() =>
        {
            // Two clicks: it can't be undone.
            if (!_confirmingForget)
            {
                _confirmingForget = true;
                Changed(nameof(ForgetLabel));
                return;
            }
            _journal.Forget();
            _confirmingForget = false;
            Forgotten = true;
            Changed(nameof(ForgetLabel));
            Changed(nameof(Forgotten));
        });
    }

    public override string Title => "Journal";
    public override string Subtitle => "What you did, pomodoro by pomodoro: put together after the fact, so there's nothing to type as you go.";
    public override SettingsPage Page => SettingsPage.Journal;

    public bool ClaudeCode { get => _settings.ClaudeCode; set { _settings.ClaudeCode = value; Changed(); } }
    public bool Git { get => _settings.Git; set { _settings.Git = value; Changed(); } }
    public bool Browsers { get => _settings.Browsers; set { _settings.Browsers = value; Changed(); } }
    public bool RecordWindows { get => _settings.RecordWindows; set { _settings.RecordWindows = value; Changed(); } }
    public bool Summaries { get => _settings.Summaries; set { _settings.Summaries = value; Changed(); } }

    public string Ignored { get => _settings.Ignored; set { _settings.Ignored = value; Changed(); } }

    public string WindowsDetail => X11Session.IsX11Desktop
        ? "The app and window title in front, every few seconds while a pomodoro runs. Never during breaks, pauses or time away."
        : "Needs an X11 session; on Wayland the desktop keeps this to itself.";

    public bool Forgotten { get; private set; }

    public string ForgetLabel => _confirmingForget ? "Forget for good" : Forgotten ? "Forgotten" : "Forget…";

    public IRelayCommand OpenDayCommand { get; }
    public IRelayCommand ForgetCommand { get; }

    protected override void ResetToDefaults() => _settings.ResetToDefaults();
}
