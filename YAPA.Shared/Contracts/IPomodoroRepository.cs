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
        /// <summary>
        /// Raised after the history changed (a record added, updated or deleted), on the thread
        /// that changed it. Anything showing counts listens, so edits made away from the timer
        /// (the assistant) show at once.
        /// </summary>
        event Action<HistoryChange> Changed;

        void Delete(int id);

        /// <summary>Inserts the record and assigns its <see cref="PomodoroEntity.Id"/>.</summary>
        void Add(PomodoroEntity pomo);

        /// <summary>Overwrites the record with the same <see cref="PomodoroEntity.Id"/>; false when there is none.</summary>
        bool Update(PomodoroEntity pomo);

        /// <summary>The record, or null.</summary>
        PomodoroEntity Find(int id);

        /// <summary>Sum of <see cref="PomodoroEntity.Count"/> on the current local calendar day.</summary>
        int CompletedToday();

        [Obsolete("Use Between(fromUtc, toUtcExclusive) or All().")]
        IEnumerable<PomodoroEntity> After(DateTime date);

        /// <summary>Records with <paramref name="fromUtc"/> &lt;= DateTime &lt; <paramref name="toUtcExclusive"/>, oldest first.</summary>
        IReadOnlyList<PomodoroEntity> Between(DateTime fromUtc, DateTime toUtcExclusive);

        /// <summary>The whole history, oldest first. Streaks and profile lists need all of it.</summary>
        IReadOnlyList<PomodoroEntity> All();
    }

    public enum HistoryChangeKind
    {
        Added,
        Updated,
        Deleted,
    }

    /// <param name="Before">The record before the change; null for <see cref="HistoryChangeKind.Added"/>.</param>
    /// <param name="After">The record after it; null for <see cref="HistoryChangeKind.Deleted"/>.</param>
    public sealed record HistoryChange(HistoryChangeKind Kind, PomodoroEntity Before, PomodoroEntity After);

    public class PomodoroEntity
    {
        public PomodoroEntity Clone() => (PomodoroEntity)MemberwiseClone();

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
