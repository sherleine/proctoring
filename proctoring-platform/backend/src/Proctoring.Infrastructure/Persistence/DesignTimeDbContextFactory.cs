using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Proctoring.Infrastructure.Persistence;

/// <summary>
/// Used only by the `dotnet ef` tool (migrations add / database update) so it does not need to start
/// the web app. DATABASE_PATH selects the file; the default matches local development.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ProctoringDbContext>
{
    public ProctoringDbContext CreateDbContext(string[] args)
    {
        var path = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "data/db/proctoring.db";
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var options = new DbContextOptionsBuilder<ProctoringDbContext>()
            .UseSqlite($"Data Source={full}")
            .Options;
        return new ProctoringDbContext(options);
    }
}
