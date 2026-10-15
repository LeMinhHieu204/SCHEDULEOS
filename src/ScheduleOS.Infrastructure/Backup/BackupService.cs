using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Infrastructure.Persistence;

namespace ScheduleOS.Infrastructure.Backup;

public sealed record BackupMetadata(
    DateTime CreatedAt,
    string Kind,
    string? AppVersion,
    string? SchemaVersion,
    long SizeBytes,
    string Sha256,
    int Customers,
    int Appointments,
    int Payments,
    string Integrity);

/// <summary>
/// Backups use the SQLite Online Backup API (never a raw copy of a live WAL database) and are verified
/// with PRAGMA integrity_check. Restores validate first, take a safety backup and run with writes suspended.
/// </summary>
public sealed class BackupService(
    AppPaths paths,
    SqliteDbContextFactory factory,
    SqliteWriteExecutor writer,
    SettingsService settings,
    IClock clock,
    ILogger<BackupService> logger) : IBackupService
{
    public const string Extension = ".sosbak";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string DefaultBackupDirectory => paths.BackupDirectory;

    public async Task<BackupInfo> CreateBackupAsync(string? destinationDirectory, string kind, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(destinationDirectory);
        Directory.CreateDirectory(dir);
        var stamp = clock.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var file = Path.Combine(dir, $"ScheduleOS-{stamp}-{Sanitize(kind)}{Extension}");
        for (var i = 2; File.Exists(file); i++) file = Path.Combine(dir, $"ScheduleOS-{stamp}-{Sanitize(kind)}-{i}{Extension}");

        await Task.Run(() => BackupDatabase(factory.ConnectionString, DestinationConnectionString(file)), ct);
        var meta = await BuildMetadataAsync(file, kind, ct);
        if (!string.Equals(meta.Integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(file);
            throw new DomainException("The backup failed verification: " + meta.Integrity);
        }

        await File.WriteAllTextAsync(file + ".json", JsonSerializer.Serialize(meta, Json), ct);
        logger.LogInformation("Backup created ({Kind}, {Bytes} bytes)", kind, meta.SizeBytes);
        return new BackupInfo(file, meta.CreatedAt, meta.SizeBytes, kind, meta.AppVersion, meta.SchemaVersion, true);
    }

    public async Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(string? directory = null, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(directory);
        if (!Directory.Exists(dir)) return [];
        var result = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(dir, "*" + Extension))
        {
            BackupMetadata? meta = null;
            try
            {
                if (File.Exists(file + ".json")) meta = JsonSerializer.Deserialize<BackupMetadata>(await File.ReadAllTextAsync(file + ".json", ct));
            }
            catch (JsonException)
            {
                meta = null;
            }

            var fi = new FileInfo(file);
            result.Add(new BackupInfo(file, meta?.CreatedAt ?? fi.LastWriteTime, fi.Length, meta?.Kind ?? "unknown", meta?.AppVersion, meta?.SchemaVersion,
                meta is null ? null : string.Equals(meta.Integrity, "ok", StringComparison.OrdinalIgnoreCase)));
        }

        return result.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public async Task<BackupValidationResult> ValidateBackupAsync(string backupFile, CancellationToken ct = default)
    {
        if (!File.Exists(backupFile)) return new(false, "The backup file does not exist.", null);
        try
        {
            await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            await conn.OpenAsync(ct);

            var integrity = await ScalarAsync(conn, "PRAGMA integrity_check;", ct);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)) return new(false, "Integrity check failed: " + integrity, null);

            var tables = await ScalarAsync(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('__EFMigrationsHistory','Appointments','Customers','Payments','Users');", ct);
            if (tables != "5") return new(false, "This file is not a ScheduleOS database.", null);

            var schema = await ScalarAsync(conn, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;", ct);
            await using var ctx = factory.Create();
            var known = ctx.Database.GetMigrations().ToList();
            if (schema is null || !known.Contains(schema))
                return new(false, "This backup was created by a newer or unknown version of ScheduleOS and cannot be restored by this version.", schema);

            var users = await ScalarAsync(conn, "SELECT COUNT(*) FROM Users;", ct);
            return new(true, $"Valid backup (schema {schema}, {users} user account(s)).", schema);
        }
        catch (SqliteException ex)
        {
            return new(false, "The file could not be read as a database: " + ex.Message, null);
        }
    }

    public async Task<RestoreResult> RestoreAsync(string backupFile, CancellationToken ct = default)
    {
        var validation = await ValidateBackupAsync(backupFile, ct);
        if (!validation.IsValid) return new(false, validation.Message, null);

        // Copy the backup to a private temp file first so the original backup is never modified.
        var staging = Path.Combine(paths.DataDirectory, $"restore-staging-{Guid.NewGuid():N}.db");
        File.Copy(backupFile, staging);
        string? safety = null;
        try
        {
            safety = (await CreateBackupAsync(null, "pre-restore", ct)).FilePath;
            await writer.SuspendAsync(async () =>
            {
                SqliteConnection.ClearAllPools();
                await Task.Run(() =>
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            BackupDatabase(DestinationConnectionString(staging), factory.ConnectionString);
                            return;
                        }
                        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 10)
                        {
                            Thread.Sleep(300);
                        }
                    }
                }, ct);
                SqliteConnection.ClearAllPools();

                await using var ctx = factory.Create();
                if ((await ctx.Database.GetPendingMigrationsAsync(ct)).Any()) await ctx.Database.MigrateAsync(ct);
                var check = await DatabaseInitializer.QuickCheckAsync(ctx, ct);
                if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new DomainException("The restored database failed its integrity check: " + check);
            });

            await settings.LoadAsync(ct);
            logger.LogInformation("Database restored from backup");
            return new(true, "The backup was restored successfully. ScheduleOS will now restart.", safety);
        }
        catch (Exception ex) when (ex is DomainException or SqliteException or IOException)
        {
            logger.LogError(ex, "Restore failed");
            return new(false, "Restore failed: " + ex.Message + (safety is null ? "" : $" Your previous data was saved to {safety}."), safety);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(staging);
        }
    }

    public async Task<BackupInfo?> RunAutomaticBackupIfDueAsync(CancellationToken ct = default)
    {
        var s = settings.Current;
        if (!s.AutoBackupEnabled) return null;
        var last = DateTime.TryParse(s.LastAutoBackupAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : DateTime.MinValue;
        if (clock.Now - last < TimeSpan.FromHours(Math.Max(1, s.AutoBackupIntervalHours))) return null;
        if (!await HasUserDataAsync(ct)) return null;

        var info = await CreateBackupAsync(string.IsNullOrWhiteSpace(s.BackupDirectory) ? null : s.BackupDirectory, "auto", ct);
        await settings.SaveSystemValueAsync(nameof(AppSettings.LastAutoBackupAt), clock.Now.ToString("O", CultureInfo.InvariantCulture), ct);
        await ApplyRetentionAsync(string.IsNullOrWhiteSpace(s.BackupDirectory) ? null : s.BackupDirectory, s.BackupRetentionCount, ct);
        return info;
    }

    /// <summary>Deletes the oldest automatic backups beyond <paramref name="keep"/>. Manual and safety backups are never deleted.</summary>
    public async Task<int> ApplyRetentionAsync(string? directory, int keep, CancellationToken ct = default)
    {
        var autos = (await ListBackupsAsync(directory, ct)).Where(b => b.Kind == "auto").OrderByDescending(b => b.CreatedAt).Skip(Math.Max(1, keep)).ToList();
        foreach (var b in autos)
        {
            TryDelete(b.FilePath);
            TryDelete(b.FilePath + ".json");
        }

        return autos.Count;
    }

    private async Task<bool> HasUserDataAsync(CancellationToken ct)
    {
        await using var ctx = factory.Create();
        return await ctx.Users.AnyAsync(ct);
    }

    private async Task<BackupMetadata> BuildMetadataAsync(string file, string kind, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(DestinationConnectionString(file));
        await conn.OpenAsync(ct);
        var integrity = await ScalarAsync(conn, "PRAGMA integrity_check;", ct) ?? "unknown";
        var schema = await ScalarAsync(conn, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;", ct);
        int Count(string? v) => int.TryParse(v, out var n) ? n : 0;
        var customers = Count(await ScalarAsync(conn, "SELECT COUNT(*) FROM Customers;", ct));
        var appointments = Count(await ScalarAsync(conn, "SELECT COUNT(*) FROM Appointments;", ct));
        var payments = Count(await ScalarAsync(conn, "SELECT COUNT(*) FROM Payments;", ct));
        await conn.CloseAsync();
        SqliteConnection.ClearPool(conn);

        string hash;
        await using (var fs = File.OpenRead(file)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        return new BackupMetadata(clock.Now, kind, AppVersion(), schema, new FileInfo(file).Length, hash, customers, appointments, payments, integrity);
    }

    private static void BackupDatabase(string sourceConnectionString, string destinationConnectionString)
    {
        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        destination.Close();
        SqliteConnection.ClearPool(destination);
    }

    private static string DestinationConnectionString(string file) =>
        new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();

    private static async Task<string?> ScalarAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return (await cmd.ExecuteScalarAsync(ct))?.ToString();
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private string ResolveDirectory(string? dir) => string.IsNullOrWhiteSpace(dir) ? paths.BackupDirectory : Path.GetFullPath(dir);

    private static string Sanitize(string kind) => new(kind.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

    private static string? AppVersion() =>
        (Assembly.GetEntryAssembly() ?? typeof(BackupService).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
