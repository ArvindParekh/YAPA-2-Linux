using System;
using System.Collections.Generic;
using System.Linq;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// What the assistant changed, one checkpoint per request, so a request can be undone as
/// a whole: "Undo" puts the timer, the history and the settings back the way they were.
///
/// Only the assistant's own changes are recorded. History edits are caught while one of
/// its tools runs (tools run synchronously on the UI thread, so nothing else can slip in),
/// which also covers rows the engine writes on its behalf, such as a pomodoro marked done.
/// </summary>
public sealed class ChangeJournal
{
    private const int MaxCheckpoints = 20;

    private readonly IPomodoroEngine _engine;
    private readonly IPomodoroRepository _repository;
    private readonly IClock _clock;
    private readonly LinkedList<Checkpoint> _done = new();
    private Checkpoint? _open;

    public ChangeJournal(IPomodoroEngine engine, IPomodoroRepository repository, IClock clock)
    {
        _engine = engine;
        _repository = repository;
        _clock = clock;
    }

    /// <summary>Raised when <see cref="CanUndo"/> may have changed.</summary>
    public event Action? Changed;

    public bool CanUndo => _done.Count > 0;

    /// <summary>What the last undoable request did, for the Undo button's tooltip.</summary>
    public string? LastDescription => _done.Last?.Value.Description;

    public void BeginTurn() => _open = new Checkpoint();

    /// <returns>True when the request changed anything.</returns>
    public bool EndTurn()
    {
        var finished = _open;
        _open = null;
        if (finished == null || finished.IsEmpty) return false;

        _done.AddLast(finished);
        while (_done.Count > MaxCheckpoints) _done.RemoveFirst();
        Changed?.Invoke();
        return true;
    }

    private Checkpoint Current => _open ??= new Checkpoint();

    /// <summary>Call before a tool changes the timer: the first call in a request snapshots it.</summary>
    public void BeforeEngineChange()
    {
        var checkpoint = Current;
        if (checkpoint.HasEngine) return;
        checkpoint.HasEngine = true;

        var snapshot = _engine.GetSnapshot();
        var takenAt = _clock.Monotonic;
        checkpoint.Steps.Add(new ActionStep(() =>
        {
            // A running interval carries on: the time since the change is not given back.
            var since = (int)Math.Floor((_clock.Monotonic - takenAt).TotalSeconds);
            if (snapshot.Phase is PomodoroPhase.Work or PomodoroPhase.Break) snapshot.PausedTime += since;
            if (snapshot.OverrunSeconds is { } overrun) snapshot.OverrunSeconds = overrun + since;
            _engine.LoadSnapshot(snapshot);
        }));
    }

    /// <summary>Records <paramref name="undo"/>, which puts back what the caller is about to change.</summary>
    public void Record(Action undo) => Current.Steps.Add(new ActionStep(undo));

    /// <summary>Adds a line to what the current request did.</summary>
    public void Describe(string done) => Current.Done.Add(done);

    /// <summary>Records history changes until disposed.</summary>
    public IDisposable Track(string tool)
    {
        var checkpoint = Current;
        void OnChanged(HistoryChange change) => checkpoint.Steps.Add(new HistoryStep(change));
        _repository.Changed += OnChanged;
        return new Unsubscribe(() => _repository.Changed -= OnChanged);
    }

    /// <summary>Undoes the most recent request that changed something, finished or under way.</summary>
    /// <returns>What was undone, or null when there was nothing.</returns>
    public string? Undo()
    {
        Checkpoint checkpoint;
        if (_done.Last is { } last)
        {
            checkpoint = last.Value;
            _done.RemoveLast();
        }
        else
        {
            return null;
        }

        Revert(checkpoint);
        Changed?.Invoke();
        return checkpoint.Description;
    }

    private void Revert(Checkpoint checkpoint)
    {
        // A row deleted and put back gets a new id; later steps that name the old one follow it.
        var ids = new Dictionary<int, int>();
        int Id(int original) => ids.TryGetValue(original, out var now) ? now : original;

        for (var i = checkpoint.Steps.Count - 1; i >= 0; i--)
        {
            switch (checkpoint.Steps[i])
            {
                case ActionStep a:
                    a.Undo();
                    break;
                case HistoryStep { Change.Kind: HistoryChangeKind.Added } h:
                    _repository.Delete(Id(h.Change.After.Id));
                    break;
                case HistoryStep { Change.Kind: HistoryChangeKind.Deleted } h:
                    var restored = h.Change.Before.Clone();
                    restored.Id = 0;
                    _repository.Add(restored);
                    ids[h.Change.Before.Id] = restored.Id;
                    break;
                case HistoryStep { Change.Kind: HistoryChangeKind.Updated } h:
                    var previous = h.Change.Before.Clone();
                    previous.Id = Id(previous.Id);
                    _repository.Update(previous);
                    break;
            }
        }
    }

    private abstract record Step;

    private sealed record ActionStep(Action Undo) : Step;

    private sealed record HistoryStep(HistoryChange Change) : Step;

    private sealed class Checkpoint
    {
        public List<Step> Steps { get; } = [];
        public List<string> Done { get; } = [];
        public bool HasEngine { get; set; }

        // Every tool that changes something says so when it succeeds. Without that, only rows
        // written by a tool that then failed are worth taking back; an engine snapshot or a
        // settings record on its own belongs to a tool that refused before changing anything.
        public bool IsEmpty => Done.Count == 0 && Steps.All(s => s is ActionStep);

        public string Description => Done.Count == 0 ? "the last change" : string.Join("; ", Done);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
