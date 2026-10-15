namespace ScheduleOS.Infrastructure;

/// <summary>
/// Per-user storage locations. Writable data never lives next to the executable.
/// Default: %LOCALAPPDATA%\ScheduleOS\{Data,Backups,Logs}. Override with SCHEDULEOS_DATA_DIR (tests, portable use).
/// </summary>
public sealed class AppPaths
{
    public const string DataDirEnvironmentVariable = "SCHEDULEOS_DATA_DIR";

    public AppPaths(string rootDirectory)
    {
        Root = Path.GetFullPath(rootDirectory);
        DataDirectory = Path.Combine(Root, "Data");
        BackupDirectory = Path.Combine(Root, "Backups");
        LogDirectory = Path.Combine(Root, "Logs");
        DatabasePath = Path.Combine(DataDirectory, "scheduleos.db");
    }

    public string Root { get; }
    public string DataDirectory { get; }
    public string BackupDirectory { get; }
    public string LogDirectory { get; }
    public string DatabasePath { get; }

    public static AppPaths Resolve(string? overrideRoot = null)
    {
        var root = overrideRoot;
        if (string.IsNullOrWhiteSpace(root)) root = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ScheduleOS");
        return new AppPaths(root);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string ConnectionString(string? databasePath = null) =>
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();
}
