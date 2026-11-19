using System;
using System.Linq;
using System.Threading;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// Time that isn't on record. Going off the record (for a while, or until turned back on)
/// stops the recorder and keeps out whatever any source finds in that time; forgetting does
/// the same for time already past. Both are kept as ranges in the journal's store, so they
/// hold for sources YAPA can't delete from (the browsers' own history, Claude Code's
/// transcripts) and survive a restart.
/// </summary>
public sealed class OffTheRecord : IDisposable
{
    /// <summary>The end of time off the record "until I turn it back on".</summary>
    public static readonly DateTime Indefinitely = new(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // System.Threading.Timer takes at most about 49 days; a longer wait is re-armed when it fires.
    private static readonly TimeSpan LongestWait = TimeSpan.FromDays(30);

    private readonly JournalStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly Timer _expiry;
    private readonly object _gate = new();
    private OffRecordRange? _current;

    public OffTheRecord(JournalStore store, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _expiry = new Timer(_ => Expire(), null, Timeout.Infinite, Timeout.Infinite);
        // Off the record when YAPA last quit or crashed, and not over yet: it still is.
        var now = _utcNow();
        _current = _store.OffRecord(now, now).LastOrDefault(r => r.Kind == OffRecordRange.Live && r.EndUtc > now);
        Arm();
    }

    /// <summary>Gone off the record, back on it, or the time ran out. Raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>Time was forgotten. Raised on the caller's thread, before anything is deleted.</summary>
    public event Action<DateTime, DateTime>? Forgotten;

    public bool IsActive => Until != null;

    /// <summary>When it ends; <see cref="Indefinitely"/> until turned back on; null when on the record.</summary>
    public DateTime? Until
    {
        get
        {
            lock (_gate) return _current is { } c && c.EndUtc > _utcNow() ? c.EndUtc : null;
        }
    }

    /// <summary>Goes off the record now, for <paramref name="length"/> or until turned back on; if already off, the end moves.</summary>
    public void Start(TimeSpan? length)
    {
        lock (_gate)
        {
            var now = _utcNow();
            var end = length is { } l ? now + l : Indefinitely;
            if (_current is { } c && c.EndUtc > now)
            {
                _store.SetOffRecordEnd(c.Id, end);
                _current = c with { EndUtc = end };
            }
            else
            {
                _current = new OffRecordRange(_store.AddOffRecord(now, end, OffRecordRange.Live), now, end, OffRecordRange.Live);
            }
            Arm();
        }
        Changed?.Invoke();
    }

    /// <summary>Back on the record now. The time off it stays off it.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            var now = _utcNow();
            if (_current is not { } c || c.EndUtc <= now) return;
            _store.SetOffRecordEnd(c.Id, now);
            _current = null;
            Arm();
        }
        Changed?.Invoke();
    }

    /// <summary>Puts it back the way <see cref="Until"/> said it was (for Undo).</summary>
    public void Restore(DateTime? until)
    {
        var now = _utcNow();
        if (until is { } u && u > now) Start(u >= Indefinitely ? null : u - now);
        else Stop();
    }

    /// <summary>
    /// Takes [fromUtc, toUtc] off the record for good. Only the range is kept here; the
    /// journal deletes what it recorded in it.
    /// </summary>
    public void Forget(DateTime fromUtc, DateTime toUtc)
    {
        _store.AddOffRecord(fromUtc, toUtc, OffRecordRange.Forgotten);
        Forgotten?.Invoke(fromUtc, toUtc);
    }

    private void Arm()
    {
        var wait = _current is { } c && c.EndUtc < Indefinitely ? c.EndUtc - _utcNow() : Timeout.InfiniteTimeSpan;
        if (wait != Timeout.InfiniteTimeSpan)
            wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > LongestWait ? LongestWait : wait;
        _expiry.Change(wait, Timeout.InfiniteTimeSpan);
    }

    private void Expire()
    {
        bool ended;
        lock (_gate)
        {
            ended = _current is { } c && c.EndUtc <= _utcNow();
            if (ended) _current = null;
            else Arm();
        }
        if (ended) Changed?.Invoke();
    }

    public void Dispose() => _expiry.Dispose();
}
