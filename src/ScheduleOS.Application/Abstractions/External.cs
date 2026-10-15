namespace ScheduleOS.Application.Abstractions;

public sealed record EmailMessage(string To, string Subject, string Body);

public sealed record DeliveryResult(bool Delivered, bool NotConfigured, string? Error)
{
    public static DeliveryResult Success() => new(true, false, null);
    public static DeliveryResult Unconfigured(string reason) => new(false, true, reason);
    public static DeliveryResult Failure(string error) => new(false, false, error);
}

public interface IEmailSender
{
    bool IsConfigured { get; }
    Task<DeliveryResult> SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Integration point for SMS providers. No provider ships with the product.</summary>
public interface ISmsSender
{
    bool IsConfigured { get; }
    string ProviderName { get; }
    Task<DeliveryResult> SendAsync(string phone, string text, CancellationToken ct = default);
}

/// <summary>Protects locally stored secrets (Windows DPAPI in production).</summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}

public sealed record BackupInfo(string FilePath, DateTime CreatedAt, long SizeBytes, string Kind, string? AppVersion, string? SchemaVersion, bool? IsValid);

public interface IBackupService
{
    string DefaultBackupDirectory { get; }
    Task<BackupInfo> CreateBackupAsync(string? destinationDirectory, string kind, CancellationToken ct = default);
    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(string? directory = null, CancellationToken ct = default);
    Task<BackupValidationResult> ValidateBackupAsync(string backupFile, CancellationToken ct = default);
    /// <summary>Restores the backup over the live database. A safety backup of the current data is taken first.</summary>
    Task<RestoreResult> RestoreAsync(string backupFile, CancellationToken ct = default);
    Task<BackupInfo?> RunAutomaticBackupIfDueAsync(CancellationToken ct = default);
    Task<int> ApplyRetentionAsync(string? directory, int keep, CancellationToken ct = default);
}

public sealed record BackupValidationResult(bool IsValid, string Message, string? SchemaVersion);

public sealed record RestoreResult(bool Success, string Message, string? SafetyBackupPath);

public interface IDatabaseInfo
{
    string DatabasePath { get; }
    string DataDirectory { get; }
    string LogDirectory { get; }
    Task<string> GetSchemaVersionAsync(CancellationToken ct = default);
    Task<string> RunIntegrityCheckAsync(CancellationToken ct = default);
}

public interface IDemoDataService
{
    Task<bool> IsDemoSeededAsync(CancellationToken ct = default);
    /// <summary>Idempotent: does nothing if demo data already exists. Never modifies non-demo records.</summary>
    Task SeedAsync(CancellationToken ct = default);
    /// <summary>Removes demo records only, then reseeds.</summary>
    Task ResetAsync(CancellationToken ct = default);
}

public sealed record TabularReport(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

public interface IReportExporter
{
    Task ExportCsvAsync(string path, IReadOnlyList<TabularReport> reports, CancellationToken ct = default);
    Task ExportXlsxAsync(string path, IReadOnlyList<TabularReport> reports, CancellationToken ct = default);
    Task ExportPdfAsync(string path, string title, string subtitle, IReadOnlyList<TabularReport> reports, CancellationToken ct = default);
}
