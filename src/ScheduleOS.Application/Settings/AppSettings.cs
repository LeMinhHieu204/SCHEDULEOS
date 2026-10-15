namespace ScheduleOS.Application.Settings;

public enum ColorTheme { Light, Dark }

public enum DensityMode { Comfortable, Compact }

/// <summary>Typed business settings persisted as key/value rows in SQLite.</summary>
public sealed class AppSettings
{
    // Appointments
    public int SlotIntervalMinutes { get; set; } = 15;
    public int CalendarStartHour { get; set; } = 8;
    public int CalendarEndHour { get; set; } = 20;
    public int DefaultPrepMinutes { get; set; }
    public int DefaultCleanupMinutes { get; set; } = 10;
    public int MinimumLeadMinutes { get; set; }
    public int CancellationNoticeHours { get; set; } = 24;
    public string CancellationPolicy { get; set; } = "Please give at least 24 hours notice when cancelling.";
    public int NoShowGraceMinutes { get; set; } = 15;
    public string NoShowPolicy { get; set; } = "Appointments may be marked as no-show 15 minutes after the start time.";
    public bool AllowOvertimeOverride { get; set; } = true;
    public bool NewBookingsConfirmed { get; set; }

    // Pricing
    public int TaxRateBasisPoints { get; set; }
    public string TaxLabel { get; set; } = "Tax";
    public string InvoiceFooter { get; set; } = "Thank you for your visit!";

    // Reminders & notifications
    public bool SendConfirmationMessages { get; set; } = true;
    public int ReminderLeadHours { get; set; } = 24;
    public bool DefaultRemindByEmail { get; set; } = true;
    public bool DefaultRemindBySms { get; set; }
    public int InAppReminderMinutesBefore { get; set; } = 30;
    public bool LocalNotificationsEnabled { get; set; } = true;
    public int FollowUpDaysAfterVisit { get; set; } = 30;

    // SMTP (password is stored DPAPI-protected)
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    public string SmtpPasswordProtected { get; set; } = "";
    public string SmtpFromAddress { get; set; } = "";
    public string SmtpFromName { get; set; } = "";

    // Customer segmentation
    public int NewCustomerDays { get; set; } = 30;
    public int ReturningMinVisits { get; set; } = 2;
    public int VipMinVisits { get; set; } = 10;
    public long VipMinSpentMinor { get; set; } = 100_000;
    public int InactiveDays { get; set; } = 90;
    public int FrequentCancellationCount { get; set; } = 3;

    // Security
    public int SessionLockMinutes { get; set; } = 15;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 5;

    // Data
    public bool AutoBackupEnabled { get; set; } = true;
    public int AutoBackupIntervalHours { get; set; } = 24;
    public int BackupRetentionCount { get; set; } = 10;
    public string BackupDirectory { get; set; } = "";
    public string LastAutoBackupAt { get; set; } = "";

    // Appearance
    public ColorTheme Theme { get; set; } = ColorTheme.Light;
    public DensityMode Density { get; set; } = DensityMode.Comfortable;
    public bool SidebarCollapsed { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public sealed record OpeningHoursDto(DayOfWeek Day, bool IsOpen, TimeOnly Open, TimeOnly Close);

public sealed record HolidayDto(int Id, DateOnly Date, string Name);

public sealed class BusinessProfileDto
{
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? LogoPath { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public int CurrencyDecimals { get; set; } = 2;
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool IsDemoWorkspace { get; set; }
}
