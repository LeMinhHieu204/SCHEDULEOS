using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Infrastructure.Backup;

namespace ScheduleOS.Infrastructure.Persistence;

public sealed record InitializationResult(bool Success, string Message, bool CreatedNew, IReadOnlyList<string> AppliedMigrations, string? PreMigrationBackup);

/// <summary>Creates or upgrades the database. Existing data is backed up before any schema migration and never reset.</summary>
public sealed class DatabaseInitializer(AppPaths paths, SqliteDbContextFactory factory, BackupService backups, ILogger<DatabaseInitializer> logger)
{
    public async Task<InitializationResult> InitializeAsync(CancellationToken ct = default)
    {
        paths.EnsureCreated();
        var existed = File.Exists(paths.DatabasePath) && new FileInfo(paths.DatabasePath).Length > 0;
        string? backupPath = null;

        await using (var ctx = factory.Create())
        {
            var pending = (await ctx.Database.GetPendingMigrationsAsync(ct)).ToList();
            var applied = existed ? (await ctx.Database.GetAppliedMigrationsAsync(ct)).ToList() : [];

            if (existed && applied.Count > 0 && pending.Count > 0)
            {
                var info = await backups.CreateBackupAsync(null, "pre-migration", ct);
                backupPath = info.FilePath;
                logger.LogInformation("Backed up database before applying {Count} migration(s)", pending.Count);
            }

            if (pending.Count > 0) await ctx.Database.MigrateAsync(ct);
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct);

            var check = await QuickCheckAsync(ctx, ct);
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Database integrity check failed: {Result}", check);
                return new InitializationResult(false, "The database failed its integrity check: " + check, !existed, pending, backupPath);
            }

            return new InitializationResult(true, "Ready", !existed, pending, backupPath);
        }
    }

    internal static async Task<string> QuickCheckAsync(DbContext ctx, CancellationToken ct)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        return (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "unknown";
    }
}

public sealed class DatabaseInfo(AppPaths paths, SqliteDbContextFactory factory) : IDatabaseInfo
{
    public string DatabasePath => paths.DatabasePath;
    public string DataDirectory => paths.DataDirectory;
    public string LogDirectory => paths.LogDirectory;

    public async Task<string> GetSchemaVersionAsync(CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var applied = await ctx.Database.GetAppliedMigrationsAsync(ct);
        return applied.LastOrDefault() ?? "none";
    }

    public async Task<string> RunIntegrityCheckAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(factory.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) lines.Add(reader.GetString(0));
        return string.Join("; ", lines);
    }
}
