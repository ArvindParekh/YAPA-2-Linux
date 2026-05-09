using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Persistence;

/// <summary>
/// SQLite-backed replacement for the WPF ItemRepository.
/// The database lives in the XDG data directory on Linux (~/.local/share/YAPA2/Yapa.db)
/// and in Documents\YAPA2\Yapa.db on Windows, matching the WPF path so history is preserved.
/// </summary>
public sealed class SqlitePomodoroRepository : IPomodoroRepository, IDisposable
{
    private readonly PomodoroDbContext _context;

    public SqlitePomodoroRepository(IEnvironment environment)
    {
        // Derive the data directory from the plugin directory (DataDir/Plugins → DataDir).
        var dataDir = Path.GetDirectoryName(environment.GetPluginDirectory())!;
        Directory.CreateDirectory(dataDir);

        _context = new PomodoroDbContext(Path.Combine(dataDir, "Yapa.db"));

        // EnsureCreated is a no-op if the database already exists (e.g. opened from WPF).
        // All three WPF migrations are reflected in the entity definition so the schema is
        // equivalent to a fully-migrated WPF database.
        _context.Database.EnsureCreated();
    }

    public int CompletedToday()
    {
        lock (_context)
        {
            var start = DateTime.Now.Date;
            var end = start.AddDays(1).AddSeconds(-1);
            return _context.Pomodoros
                .Where(p => start <= p.DateTime && p.DateTime <= end)
                .Select(_ => _.Count)
                .DefaultIfEmpty(0)
                .Sum();
        }
    }

    public void Add(PomodoroEntity pomo)
    {
        lock (_context)
        {
            _context.Pomodoros.Add(pomo);
            _context.SaveChanges();
        }
    }

    public void Delete(int id)
    {
        lock (_context)
        {
            var existing = _context.Pomodoros.FirstOrDefault(x => x.Id == id);
            if (existing != null)
            {
                _context.Pomodoros.Remove(existing);
                _context.SaveChanges();
            }
        }
    }

    public IEnumerable<PomodoroEntity> After(DateTime date)
    {
        lock (_context)
        {
            return _context.Pomodoros.Where(x => x.DateTime >= date).ToList();
        }
    }

    public void Dispose() => _context.Dispose();
}
