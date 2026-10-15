using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Infrastructure.Persistence;

/// <summary>Creates DbContexts for the configured SQLite database file.</summary>
public sealed class SqliteDbContextFactory : IAppDbContextFactory
{
    private readonly DbContextOptions<ScheduleDbContext> _options;

    public SqliteDbContextFactory(string connectionString, ILoggerFactory? loggerFactory = null)
    {
        ConnectionString = connectionString;
        var builder = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseSqlite(connectionString, o => o.CommandTimeout(30))
            .AddInterceptors(new SqlitePragmaInterceptor());
        if (loggerFactory is not null) builder.UseLoggerFactory(loggerFactory);
        _options = builder.Options;
    }

    public string ConnectionString { get; }

    public ScheduleDbContext Create() => new(_options);

    public IAppDbContext CreateReadContext()
    {
        var ctx = Create();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return ctx;
    }
}

/// <summary>
/// Serialises writes: an in-process gate plus a SQLite <c>BEGIN IMMEDIATE</c> transaction, which takes the
/// database write lock up-front so that a competing writer (another connection or process) waits, then
/// re-reads the data after the first writer has committed. Busy errors are retried a bounded number of times.
/// </summary>
public sealed class SqliteWriteExecutor(SqliteDbContextFactory factory, ILogger<SqliteWriteExecutor> logger) : IWriteExecutor
{
    private const int MaxAttempts = 4;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised to stop all writes during a restore.</summary>
    public bool IsSuspended { get; private set; }

    public async Task<T> ExecuteAsync<T>(Func<IAppDbContext, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsSuspended) throw new DomainException("The database is being restored. Please wait.");
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await RunOnceAsync(work, cancellationToken);
                }
                catch (Exception ex) when (attempt < MaxAttempts &&
                                           (ex is SqliteException s1 && IsBusy(s1) || ex is DbUpdateException { InnerException: SqliteException s2 } && IsBusy(s2)))
                {
                    logger.LogWarning("SQLite busy on attempt {Attempt}; retrying", attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt * attempt), cancellationToken);
                }
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("The record was changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sx)
        {
            throw Translate(sx) ?? new DomainException("The change could not be saved because it violates a data rule.");
        }
        catch (SqliteException ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            throw new DomainException("The database is busy. Please try again in a moment.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> RunOnceAsync<T>(Func<IAppDbContext, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await using var ctx = factory.Create();
        await ctx.Database.OpenConnectionAsync(ct);
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        // deferred: false => BEGIN IMMEDIATE (reserve the write lock before reading).
        await using var tx = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await ctx.Database.UseTransactionAsync(tx, ct);
        var result = await work(ctx, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task SuspendAsync(Func<Task> exclusiveWork)
    {
        await _gate.WaitAsync();
        IsSuspended = true;
        try
        {
            await exclusiveWork();
        }
        finally
        {
            IsSuspended = false;
            _gate.Release();
        }
    }

    private static bool IsBusy(SqliteException ex) => ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY, SQLITE_LOCKED

    internal static Exception? Translate(SqliteException ex)
    {
        var msg = ex.Message;
        if (msg.Contains("SCHEDULEOS_STAFF_OVERLAP", StringComparison.Ordinal))
            return new SchedulingConflictException([new ScheduleConflict(ConflictType.StaffOverlap, ConflictSeverity.Blocking,
                "The staff member was booked by another user at the same time. Refresh and choose another slot.")]);
        if (msg.Contains("SCHEDULEOS_RESOURCE_OVERLAP", StringComparison.Ordinal))
            return new SchedulingConflictException([new ScheduleConflict(ConflictType.ResourceOverlap, ConflictSeverity.Blocking,
                "The room/resource was booked by another user at the same time. Refresh and choose another slot.")]);
        if (msg.Contains("SCHEDULEOS_PAYMENT_IMMUTABLE", StringComparison.Ordinal))
            return new DomainException("Payment records are permanent. Use a refund or reversal instead.");
        if (msg.Contains("SCHEDULEOS_AUDIT_IMMUTABLE", StringComparison.Ordinal))
            return new DomainException("Audit records cannot be changed.");
        if (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            if (msg.Contains("SubmissionKey", StringComparison.Ordinal)) return new DomainException("This form was already submitted.");
            if (msg.Contains("UNIQUE", StringComparison.Ordinal)) return new ValidationException("A record with the same unique value already exists.");
            if (msg.Contains("FOREIGN KEY", StringComparison.Ordinal)) return new ValidationException("This record is still referenced by other data and cannot be removed.");
            if (msg.Contains("CHECK", StringComparison.Ordinal)) return new ValidationException("A value is outside the allowed range.");
        }

        return null;
    }
}

/// <summary>Used by the dotnet-ef tooling to create migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ScheduleDbContext>
{
    public ScheduleDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ScheduleDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
