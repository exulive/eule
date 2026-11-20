using Eule.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Eule.Server;

/// <summary>
/// Picks the database: PostgreSQL in production (Coolify), SQLite for local development and tests.
/// Database:Provider = "postgres" | "sqlite"; the connection string is ConnectionStrings:Default.
/// </summary>
public static class Database
{
    public static void Configure(DbContextOptionsBuilder o, IConfiguration config)
    {
        string provider = config["Database:Provider"] ?? "postgres";
        string? cs = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("ConnectionStrings:Default eksik (üretimde ConnectionStrings__Default ortam değişkeni).");
        if (provider == "sqlite") o.UseSqlite(cs);
        else o.UseNpgsql(cs);
    }

    /// <summary>Brings the schema up to date on start: migrations on PostgreSQL, create-if-missing on SQLite.</summary>
    public static async Task PrepareAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EuleDbContext>();
        if (db.Database.IsNpgsql()) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
    }
}
