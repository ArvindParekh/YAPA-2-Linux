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
        _journal.OffRecord.Changed += OnOffRecordChanged;
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
    public override string Subtitle => "What you did all day, pomodoro by pomodoro and in between: put together after the fact, so there's nothing to type as you go.";
    public override SettingsPage Page => SettingsPage.Journal;

    public bool ClaudeCode { get => _settings.ClaudeCode; set { _settings.ClaudeCode = value; Changed(); } }
    public bool Git { get => _settings.Git; set { _settings.Git = value; Changed(); } }
    public bool Browsers { get => _settings.Browsers; set { _settings.Browsers = value; Changed(); } }
    public bool RecordWindows { get => _settings.RecordWindows; set { _settings.RecordWindows = value; Changed(); } }
    public bool WholeDay { get => _settings.WholeDay; set { _settings.WholeDay = value; Changed(); Changed(nameof(WindowsDetail)); } }
    public string PauseFor { get => _settings.PauseFor; set { _settings.PauseFor = value; Changed(); } }
    public bool Summaries { get => _settings.Summaries; set { _settings.Summaries = value; Changed(); } }

    public string Ignored { get => _settings.Ignored; set { _settings.Ignored = value; Changed(); } }

    public bool SkipAdult { get => _settings.SkipAdult; set { _settings.SkipAdult = value; Changed(); } }

    public string AdultDetail =>
        $"About {AdultSites.Count / 100 * 100:N0} adult sites, and pages and windows whose titles name one, are never recorded or sent; the time one is in front is off the record. " +
        "No list is complete: forget what it misses in Your day.";

    /// <summary>On: off the record until turned back on. Off: back on it.</summary>
    public bool OffRecord
    {
        get => _journal.OffRecord.IsActive;
        set
        {
            if (value == _journal.OffRecord.IsActive) return;
            if (value) _journal.OffRecord.Start(null);
            else _journal.OffRecord.Stop();
        }
    }

    public string OffRecordDetail => _journal.OffRecord.Until is { } until && until < OffTheRecord.Indefinitely
        ? $"Until {Platform.ClockFormat.ShortTime(until.ToLocalTime())}: nothing is recorded, and nothing from this time is used or sent. Pomodoros still count."
        : "Nothing is recorded, and nothing from this time is used or sent, until you switch it off. Pomodoros still count. Also in the timer's menu, for a set time.";

    public string WindowsDetail => !X11Session.IsX11Desktop
        ? "Needs an X11 session; on Wayland the desktop keeps this to itself."
        : WholeDay
            ? "The app and window title in front, every few seconds while you're at the computer. A playing video counts as being there; a locked screen doesn't."
            : "The app and window title in front, every few seconds while a pomodoro runs.";

    public bool Forgotten { get; private set; }

    public string ForgetLabel => _confirmingForget ? "Forget for good" : Forgotten ? "Forgotten" : "Forget…";

    public IRelayCommand OpenDayCommand { get; }
    public IRelayCommand ForgetCommand { get; }

    protected override void ResetToDefaults() => _settings.ResetToDefaults();

    // From a timer thread when the time runs out, or from the menu, the tray, the assistant.
    private void OnOffRecordChanged() => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Changed(nameof(OffRecord));
        Changed(nameof(OffRecordDetail));
    });

    /// <summary>The window closed: stop listening to the journal, which outlives it.</summary>
    public void Detach() => _journal.OffRecord.Changed -= OnOffRecordChanged;
}
