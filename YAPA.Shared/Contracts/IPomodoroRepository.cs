using System;
using System.Collections.Generic;

namespace YAPA.Shared.Contracts
{
    /// <summary>
    /// History of completed pomodoros. Timestamps are UTC instants.
    /// </summary>
    /// <remarks>
    /// Every <see cref="DateTime"/> argument is interpreted by its <see cref="DateTime.Kind"/>:
    /// <c>Utc</c> as is, <c>Local</c> converted from the system zone, and <c>Unspecified</c>
    /// as UTC (that is what the database stores). Returned entities carry <c>Kind = Utc</c>.
    /// </remarks>
    public interface IPomodoroRepository
    {
        void Delete(int id);

        /// <summary>Inserts the record and assigns its <see cref="PomodoroEntity.Id"/>.</summary>
        void Add(PomodoroEntity pomo);

        /// <summary>Sum of <see cref="PomodoroEntity.Count"/> on the current local calendar day.</summary>
        int CompletedToday();

        [Obsolete("Use Between(fromUtc, toUtcExclusive) or All().")]
        IEnumerable<PomodoroEntity> After(DateTime date);

        /// <summary>Records with <paramref name="fromUtc"/> &lt;= DateTime &lt; <paramref name="toUtcExclusive"/>, oldest first.</summary>
        IReadOnlyList<PomodoroEntity> Between(DateTime fromUtc, DateTime toUtcExclusive);

        /// <summary>The whole history, oldest first. Streaks and profile lists need all of it.</summary>
        IReadOnlyList<PomodoroEntity> All();
    }

    public class PomodoroEntity
    {
        public int Id { get; set; }
        public string ProfileName { get; set; }

        public DateTime DateTime { get; set; }
        public int Count { get; set; }
        public int DurationMin { get; set; }
    }

    public class PomodoroGithubDashboardModel
    {
        public DateTime DateTime { get; set; }
        public int Count { get; set; }
        public int DurationMin { get; set; }
    }

    public class PomodoroDashboardModel
    {
        public IEnumerable<PomodoroGithubDashboardModel> DashboardItems { get; set; }
        public IEnumerable<string> Profiles { get; set; }
    }
}
