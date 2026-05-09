using Microsoft.EntityFrameworkCore;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Persistence;

public sealed class PomodoroDbContext : DbContext
{
    private readonly string _dbPath;

    public PomodoroDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Filename={_dbPath}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PomodoroEntity>().HasKey(x => x.Id);
        base.OnModelCreating(modelBuilder);
    }

    public DbSet<PomodoroEntity> Pomodoros { get; set; } = null!;
}
