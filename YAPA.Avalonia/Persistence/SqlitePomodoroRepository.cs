using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
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
            // EF Core SQLite formats DateTimeKind.Utc parameters as "yyyy-MM-ddTHH:mm:ssZ"
            // (with T and Z), but stored values use "yyyy-MM-dd HH:mm:ss" (space, no Z).
            // SQLite TEXT comparison is lexicographic: space (0x20) < T (0x54), so every
            // record compares as less-than a UTC parameter, returning 0.
            // Use strftime to extract just the date portion and compare as a plain string.
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            return _context.Database
                .SqlQuery<int>($"SELECT COALESCE(SUM(\"Count\"), 0) AS Value FROM Pomodoros WHERE strftime('%Y-%m-%d', DateTime) = {today}")
                .Single();
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
            var dateStr = date.ToString("yyyy-MM-dd");
            return _context.Pomodoros
                .FromSqlInterpolated($"SELECT * FROM Pomodoros WHERE strftime('%Y-%m-%d', DateTime) >= {dateStr}")
                .AsNoTracking()
                .ToList();
        }
    }

    public void Dispose() => _context.Dispose();
}
