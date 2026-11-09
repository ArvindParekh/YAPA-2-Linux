using System;
using System.Linq;
using System.Threading.Tasks;
using YAPA.Shared.Contracts;
using YAPA.Shared.Statistics;

namespace YAPA.Shared.Common
{
    public class Dashboard : IPlugin
    {
        private readonly IPomodoroRepository _itemRepository;
        private readonly PomodoroEngineSettings _engineSettings;
        private readonly IPomodoroEngine _engine;

        public Dashboard(IPomodoroEngine engine, IPomodoroRepository itemRepository, PomodoroEngineSettings engineSettings)
        {
            _itemRepository = itemRepository;
            _engineSettings = engineSettings;
            _engine = engine;

            _engine.OnPomodoroCompleted += _engine_OnPomodoroCompleted;
        }

        public PomodoroDashboardModel GetPomodoros(int numberOfMonths, string profileFilter)
        {
            var timeZone = TimeZoneInfo.Local;
            var nowUtc = DateTime.UtcNow;
            var today = LocalCalendar.DateOf(nowUtc, timeZone);
            var fromDate = LocalCalendar.StartOfMonth(today.AddMonths(-numberOfMonths));

            // One day of slack covers any UTC offset; DailyTotals then buckets by exact local date.
            var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-1);
            var captured = _itemRepository.Between(fromUtc, nowUtc.AddDays(1));
            var daily = DailyTotals.From(captured, timeZone, profileFilter);

            return new PomodoroDashboardModel
            {
                DashboardItems = daily.Range(fromDate, today)
                    .Select(d => new PomodoroGithubDashboardModel
                    {
                        DateTime = d.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local),
                        Count = d.Count,
                        DurationMin = d.FocusMinutes
                    })
                    .ToList(),
                Profiles = captured.Select(_ => _.ProfileName).Distinct().ToList()
            };
        }

        private void _engine_OnPomodoroCompleted()
        {
            _itemRepository.Add(new PomodoroEntity
            {
                Count = 1,
                DateTime = DateTime.UtcNow,
                DurationMin = _engine.WorkTime / 60,
                ProfileName = _engineSettings.ActiveProfile
            });
        }
    }

    public class DashboardSettings : IPluginSettings
    {
        private readonly ISettingsForComponent _settings;

        public DashboardSettings(ISettings settings)
        {
            _settings = settings.GetSettingsForComponent(nameof(Dashboard));
        }

        public int NumberOfMonths
        {
            get => _settings.Get(nameof(NumberOfMonths), 6);
            set => _settings.Update(nameof(NumberOfMonths), value);
        }

        public string ProfileFilter
        {
            get => _settings.Get<string>(nameof(ProfileFilter), null);
            set => _settings.Update(nameof(ProfileFilter), value);
        }

        public void DeferChanges()
        {
            _settings.DeferChanges();
        }
    }
}
