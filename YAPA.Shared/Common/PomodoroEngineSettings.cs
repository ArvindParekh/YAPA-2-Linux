using System.Collections.Generic;
using System.Linq;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Common
{
    public class PomodoroProfile
    {
        public int WorkTime { get; set; }
        public int BreakTime { get; set; }
        public int LongBreakTime { get; set; }
        public bool AutoStartBreak { get; set; }
        public bool AutoStartWork { get; set; }
        public int PomodorosBeforeLongBreak { get; set; }

        public PomodoroProfile Clone() => (PomodoroProfile)MemberwiseClone();

        public static PomodoroProfile Default() => new PomodoroProfile
        {
            WorkTime = 25 * 60,
            BreakTime = 5 * 60,
            LongBreakTime = 15 * 60,
            PomodorosBeforeLongBreak = 4,
        };
    }

    public class PomodoroEngineSettings : IPluginSettings
    {
        public const string DefaultProfileName = "Pomodoro";

        private readonly ISettingsForComponent _settings;

        public PomodoroEngineSettings(ISettings settings)
        {
            _settings = settings.GetSettingsForComponent(nameof(PomodoroEngine));
        }

        /// <summary>
        /// Always returns a fresh dictionary; mutate it and assign it back to persist.
        /// (Handing out a shared default instance let callers mutate the defaults.)
        /// </summary>
        public Dictionary<string, PomodoroProfile> Profiles
        {
            get
            {
                var stored = _settings.Get<Dictionary<string, PomodoroProfile>>(nameof(Profiles), null);
                return stored != null && stored.Count > 0
                    ? stored
                    : new Dictionary<string, PomodoroProfile> { [DefaultProfileName] = PomodoroProfile.Default() };
            }
            set => _settings.Update(nameof(Profiles), value);
        }

        public string ActiveProfile
        {
            get
            {
                var name = _settings.Get(nameof(ActiveProfile), DefaultProfileName);
                var profiles = Profiles;
                // A settings file can name a profile that no longer exists; don't crash on it.
                return profiles.ContainsKey(name) ? name : profiles.Keys.First();
            }
            set => _settings.Update(nameof(ActiveProfile), value);
        }

        private PomodoroProfile Current
        {
            get
            {
                var profiles = Profiles;
                return profiles.TryGetValue(ActiveProfileName(profiles), out var p) ? p : PomodoroProfile.Default();
            }
        }

        private string ActiveProfileName(Dictionary<string, PomodoroProfile> profiles)
        {
            var name = _settings.Get(nameof(ActiveProfile), DefaultProfileName);
            return profiles.ContainsKey(name) ? name : profiles.Keys.First();
        }

        private void UpdateCurrent(System.Action<PomodoroProfile> change)
        {
            var profiles = Profiles;
            var name = ActiveProfileName(profiles);
            var profile = profiles.TryGetValue(name, out var p) ? p.Clone() : PomodoroProfile.Default();
            change(profile);
            profiles[name] = profile;
            Profiles = profiles;
        }

        public int WorkTime
        {
            get => Current.WorkTime;
            set => UpdateCurrent(p => p.WorkTime = value);
        }

        public int BreakTime
        {
            get => Current.BreakTime;
            set => UpdateCurrent(p => p.BreakTime = value);
        }

        public int LongBreakTime
        {
            get => Current.LongBreakTime;
            set => UpdateCurrent(p => p.LongBreakTime = value);
        }

        public bool AutoStartBreak
        {
            get => Current.AutoStartBreak;
            set => UpdateCurrent(p => p.AutoStartBreak = value);
        }

        public bool AutoStartWork
        {
            get => Current.AutoStartWork;
            set => UpdateCurrent(p => p.AutoStartWork = value);
        }

        public int PomodorosBeforeLongBreak
        {
            get => Current.PomodorosBeforeLongBreak;
            set => UpdateCurrent(p => p.PomodorosBeforeLongBreak = value);
        }

        public bool CountBackwards
        {
            get => _settings.Get(nameof(CountBackwards), false);
            set => _settings.Update(nameof(CountBackwards), value);
        }

        public bool DisableSoundNotifications
        {
            get => _settings.Get(nameof(DisableSoundNotifications), false);
            set => _settings.Update(nameof(DisableSoundNotifications), value);
        }

        public double Volume
        {
            get => _settings.Get(nameof(Volume), 0.5);
            set => _settings.Update(nameof(Volume), value);
        }

        public CounterEnum Counter
        {
            get => _settings.Get(nameof(Counter), CounterEnum.PomodoroIndex);
            set => _settings.Update(nameof(Counter), value);
        }

        public void ResetToDefaults()
        {
            var defaults = PomodoroProfile.Default();
            UpdateCurrent(p =>
            {
                p.WorkTime = defaults.WorkTime;
                p.BreakTime = defaults.BreakTime;
                p.LongBreakTime = defaults.LongBreakTime;
                p.PomodorosBeforeLongBreak = defaults.PomodorosBeforeLongBreak;
                p.AutoStartBreak = false;
                p.AutoStartWork = false;
            });
            CountBackwards = false;
            Counter = CounterEnum.PomodoroIndex;
        }

        public void DeferChanges() => _settings.DeferChanges();
    }
}
