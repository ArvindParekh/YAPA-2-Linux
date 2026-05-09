using System;
using System.Collections.Generic;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

// Placeholder until the real SQLite-backed repository is wired up in Step 3.
public class NullPomodoroRepository : IPomodoroRepository
{
    public int CompletedToday() => 0;
    public void Add(PomodoroEntity pomo) { }
    public void Delete(int id) { }
    public IEnumerable<PomodoroEntity> After(DateTime date) => Array.Empty<PomodoroEntity>();
}
