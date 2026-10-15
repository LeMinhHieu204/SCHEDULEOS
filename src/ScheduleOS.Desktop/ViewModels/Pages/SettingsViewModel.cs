using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Reporting;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Desktop.ViewModels.Dialogs;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.ViewModels.Pages;

public sealed partial class HoursRow(DayOfWeek day) : ObservableObject
{
    public DayOfWeek Day { get; } = day;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _open = "09:00";
    [ObservableProperty] private string _close = "18:00";
}

public sealed partial class SettingsViewModel(
    SettingsService settings,
    AuthService auth,
    LocationService locations,
    IBackupService backups,
    IDatabaseInfo database,
    IDemoDataService demo,
    IEmailSender email,
    ISmsSender sms,
    ISecretProtector secrets,
    AuditService audit,
    UserSession session,
    ThemeService theme,
    DialogService dialogs,
    FileDialogs files) : PageViewModel
{
    public override string Title => "Settings";
    protected override DataArea RefreshOn => DataArea.Users | DataArea.Settings;

    public ObservableCollection<HoursRow> Hours { get; } = [];
    public ObservableCollection<HolidayDto> Holidays { get; } = [];
    public ObservableCollection<LocationDto> Locations { get; } = [];
    public ObservableCollection<UserDto> Users { get; } = [];
    public ObservableCollection<BackupInfo> Backups { get; } = [];
    public ObservableCollection<AuditEntryDto> AuditEntries { get; } = [];
    public IReadOnlyList<int> SlotOptions { get; } = [5, 10, 15, 20, 30, 60];
    public IReadOnlyList<int> HourOptions { get; } = [.. Enumerable.Range(0, 25)];
    public IReadOnlyList<ColorTheme> Themes { get; } = Enum.GetValues<ColorTheme>();
    public IReadOnlyList<DensityMode> Densities { get; } = Enum.GetValues<DensityMode>();
    public IReadOnlyList<CurrencyOption> Currencies => CurrencyOption.All;
    public IReadOnlyList<string> RoleDescriptions { get; } = Enum.GetValues<UserRole>().Select(r => $"{r}: {RolePermissions.Describe(r)}").ToList();

    [ObservableProperty] private AppSettings _s = new();
    [ObservableProperty] private BusinessProfileDto _business = new();
    [ObservableProperty] private CurrencyOption? _currency;
    [ObservableProperty] private string _taxPercent = "0";
    [ObservableProperty] private string _vipSpent = "";
    [ObservableProperty] private string _smtpPassword = "";
    [ObservableProperty] private string _testEmailTo = "";
    [ObservableProperty] private ColorTheme _selectedTheme;
    [ObservableProperty] private DensityMode _density;
    [ObservableProperty] private bool _sidebarCollapsed;
    [ObservableProperty] private string _schemaVersion = "";
    [ObservableProperty] private string _integrity = "Not checked";
    [ObservableProperty] private string _auditSearch = "";
    [ObservableProperty] private bool _isDemo;
    [ObservableProperty] private int _tab;

    public bool CanManageSettings => session.Has(Permission.ManageSettings);
    public bool CanManageUsers => session.Has(Permission.ManageUsers);
    public bool CanBackup => session.Has(Permission.CreateBackups);
    public bool CanRestore => session.Has(Permission.RestoreBackups);
    public bool CanViewAudit => session.Has(Permission.ViewAuditLog);
    public string DatabasePath => database.DatabasePath;
    public string DataDirectory => database.DataDirectory;
    public string LogDirectory => database.LogDirectory;
    public string BackupDirectory => string.IsNullOrWhiteSpace(S.BackupDirectory) ? backups.DefaultBackupDirectory : S.BackupDirectory;
    public string AppVersion => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
    public string RuntimeInfo => $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";
    public string SmtpStatus => email.IsConfigured ? "Configured — messages are delivered through your SMTP server." : "Not configured — e-mails are kept as drafts and marked Not Sent.";
    public string SmsStatus => sms.IsConfigured ? $"Provider: {sms.ProviderName}" : "No SMS provider is installed. SMS reminders are recorded as Not Sent. An ISmsSender integration can be added for a provider such as Twilio.";
    public string UpdateInfo => "ScheduleOS does not update itself automatically. Install new versions with the installer; your data in %LOCALAPPDATA%\\ScheduleOS is preserved and backed up before migrations.";

    public override async Task LoadAsync()
    {
        S = settings.Current.Clone();
        Business = new BusinessProfileDto
        {
            Name = settings.Business.Name, Address = settings.Business.Address, Phone = settings.Business.Phone, Email = settings.Business.Email,
            CurrencyCode = settings.Business.CurrencyCode, CurrencyDecimals = settings.Business.CurrencyDecimals, TimeZoneId = settings.Business.TimeZoneId,
            IsDemoWorkspace = settings.Business.IsDemoWorkspace, LogoPath = settings.Business.LogoPath,
        };
        Currency = CurrencyOption.All.FirstOrDefault(c => c.Code == Business.CurrencyCode) ?? new CurrencyOption(Business.CurrencyCode, Business.CurrencyCode, Business.CurrencyDecimals);
        TaxPercent = (S.TaxRateBasisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture);
        VipSpent = settings.Currency.ToEditString(S.VipMinSpentMinor);
        SelectedTheme = S.Theme;
        Density = S.Density;
        SidebarCollapsed = S.SidebarCollapsed;
        IsDemo = Business.IsDemoWorkspace;
        OnPropertyChanged(nameof(BackupDirectory));

        Hours.Clear();
        foreach (var h in await settings.GetOpeningHoursAsync()) Hours.Add(new HoursRow(h.Day) { IsOpen = h.IsOpen, Open = h.Open.ToString("HH:mm"), Close = h.Close.ToString("HH:mm") });
        Holidays.Clear();
        foreach (var h in await settings.GetHolidaysAsync()) Holidays.Add(h);
        Locations.Clear();
        foreach (var l in await locations.ListAsync(true)) Locations.Add(l);
        if (CanManageUsers)
        {
            Users.Clear();
            foreach (var u in await auth.ListUsersAsync()) Users.Add(u);
        }

        if (CanBackup) await LoadBackupsAsync();
        if (CanViewAudit) await LoadAuditAsync();
        SchemaVersion = await database.GetSchemaVersionAsync();
    }

    private async Task LoadBackupsAsync()
    {
        Backups.Clear();
        foreach (var b in await backups.ListBackupsAsync(string.IsNullOrWhiteSpace(S.BackupDirectory) ? null : S.BackupDirectory)) Backups.Add(b);
    }

    private async Task LoadAuditAsync()
    {
        AuditEntries.Clear();
        foreach (var a in await audit.ListAsync(null, null, AuditSearch, 300)) AuditEntries.Add(a);
    }

    partial void OnAuditSearchChanged(string value) { if (IsLoaded) _ = TryAsync(LoadAuditAsync); }

    // ---------------------------------------------------------------- business

    [RelayCommand]
    private async Task SaveBusinessAsync()
    {
        if (Currency is not null)
        {
            Business.CurrencyCode = Currency.Code;
            Business.CurrencyDecimals = Currency.Decimals;
        }

        await RunAsync(async () =>
        {
            await settings.SaveBusinessProfileAsync(Business);
            await settings.SaveOpeningHoursAsync(Hours.Select(h => new OpeningHoursDto(h.Day, h.IsOpen,
                TimeOnly.TryParse(h.Open, CultureInfo.InvariantCulture, out var o) ? o : new TimeOnly(9, 0),
                TimeOnly.TryParse(h.Close, CultureInfo.InvariantCulture, out var c) ? c : new TimeOnly(18, 0))).ToList());
        }, "Business profile and opening hours saved.");
    }

    [RelayCommand]
    private async Task AddHolidayAsync()
    {
        var vm = new DateRangeDialogViewModel("Add closure", "Mark whole days when the business is closed (public holidays, renovations). Bookings are blocked on these days.",
            "Add closure", showReason: true, allDayDefault: true) { Reason = "Public holiday" };
        if (await dialogs.ShowAsync(vm) is not DateRangeDialogViewModel.Result r) return;
        await RunAsync(async () =>
        {
            for (var d = r.Start.Date; d < r.End; d = d.AddDays(1)) await settings.AddHolidayAsync(DateOnly.FromDateTime(d), r.Reason);
            await LoadAsync();
        }, "Closure added.");
    }

    [RelayCommand]
    private Task RemoveHolidayAsync(HolidayDto h) => RunAsync(async () =>
    {
        await settings.RemoveHolidayAsync(h.Id);
        Holidays.Remove(h);
    });

    [RelayCommand]
    private async Task AddLocationAsync()
    {
        var name = await dialogs.PromptAsync("New location", "Add a branch or location. Staff and rooms can be assigned to it.", "Location name", true, "Add", multiline: false);
        if (name is not null) await RunAsync(async () => { await locations.SaveAsync(null, name, null, null, true); await LoadAsync(); }, "Location added.");
    }

    [RelayCommand]
    private async Task RenameLocationAsync(LocationDto l)
    {
        var name = await dialogs.PromptAsync("Rename location", "", "Location name", true, "Save", l.Name, multiline: false);
        if (name is not null) await RunAsync(async () => { await locations.SaveAsync(l.Id, name, l.Address, l.Phone, l.IsActive); await LoadAsync(); }, "Location saved.");
    }

    [RelayCommand]
    private Task ToggleLocationAsync(LocationDto l) =>
        RunAsync(async () => { await locations.SaveAsync(l.Id, l.Name, l.Address, l.Phone, !l.IsActive); await LoadAsync(); }, l.IsActive ? "Location deactivated." : "Location activated.");

    // ---------------------------------------------------------------- policies & notifications

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!decimal.TryParse(TaxPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var tax))
        {
            Ui.Toasts.Warning("Enter the tax rate as a number, for example 8.25.");
            return;
        }

        if (!settings.Currency.TryParse(VipSpent, out var vip))
        {
            Ui.Toasts.Warning("Enter a valid VIP spending threshold.");
            return;
        }

        var copy = S.Clone();
        copy.TaxRateBasisPoints = (int)Math.Round(tax * 100, MidpointRounding.AwayFromZero);
        copy.VipMinSpentMinor = vip;
        if (!string.IsNullOrEmpty(SmtpPassword)) copy.SmtpPasswordProtected = secrets.Protect(SmtpPassword);
        if (await RunAsync(() => settings.SaveAsync(copy), "Settings saved."))
        {
            SmtpPassword = "";
            S = settings.Current.Clone();
            OnPropertyChanged(nameof(SmtpStatus));
            OnPropertyChanged(nameof(BackupDirectory));
        }
    }

    [RelayCommand]
    private async Task SendTestEmailAsync()
    {
        if (string.IsNullOrWhiteSpace(TestEmailTo))
        {
            Ui.Toasts.Warning("Enter an address to send the test e-mail to.");
            return;
        }

        await RunAsync(async () =>
        {
            var result = await email.SendAsync(new EmailMessage(TestEmailTo.Trim(), "ScheduleOS test e-mail", "This is a test message from ScheduleOS. E-mail delivery is working."));
            if (result.Delivered) Ui.Toasts.Success("Test e-mail sent.");
            else await dialogs.AlertAsync("Test e-mail not delivered", result.Error ?? "Unknown error", isError: true);
        });
    }

    // ---------------------------------------------------------------- users

    [RelayCommand]
    private async Task AddUserAsync()
    {
        var vm = ActivatorUtilities.CreateInstance<UserEditorViewModel>(Ui.Services);
        if (await dialogs.ShowAsync(vm) is true) Ui.Toasts.Success("User account created.");
    }

    [RelayCommand]
    private async Task EditUserAsync(UserDto u)
    {
        var vm = ActivatorUtilities.CreateInstance<UserEditorViewModel>(Ui.Services);
        vm.Existing = u;
        if (await dialogs.ShowAsync(vm) is true) Ui.Toasts.Success("User account updated.");
    }

    [RelayCommand]
    private async Task ResetPasswordAsync(UserDto u)
    {
        var pw = await dialogs.PromptAsync("Reset password", $"Set a new password for {u.DisplayName} ({u.Username}). Share it securely; they can change it after signing in.",
            "New password (min. 8 characters, letters and numbers)", true, "Reset password", multiline: false);
        if (pw is not null) await RunAsync(() => auth.ResetPasswordAsync(u.Id, pw), "Password reset.");
    }

    // ---------------------------------------------------------------- data

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        await RunAsync(async () =>
        {
            var info = await backups.CreateBackupAsync(string.IsNullOrWhiteSpace(S.BackupDirectory) ? null : S.BackupDirectory, "manual");
            await LoadBackupsAsync();
            Ui.Toasts.Success($"Backup created: {Path.GetFileName(info.FilePath)}");
        });
    }

    [RelayCommand]
    private void ChooseBackupFolder()
    {
        var folder = files.PickFolder("Choose backup folder", BackupDirectory);
        if (folder is null) return;
        S.BackupDirectory = folder;
        OnPropertyChanged(nameof(BackupDirectory));
        Ui.Toasts.Info("Press Save to keep this backup folder.");
    }

    [RelayCommand]
    private async Task ValidateBackupAsync(BackupInfo b)
    {
        BackupValidationResult? r = null;
        await RunAsync(async () => r = await backups.ValidateBackupAsync(b.FilePath));
        if (r is not null) await dialogs.AlertAsync(r.IsValid ? "Backup is valid" : "Backup is not valid", r.Message, !r.IsValid);
    }

    [RelayCommand]
    private Task RestoreBackupAsync(BackupInfo b) => RestoreFileAsync(b.FilePath);

    [RelayCommand]
    private async Task RestoreFromFileAsync()
    {
        var path = files.Open("Choose a ScheduleOS backup", "ScheduleOS backup (*.sosbak)|*.sosbak|All files (*.*)|*.*");
        if (path is not null) await RestoreFileAsync(path);
    }

    private async Task RestoreFileAsync(string path)
    {
        var validation = await backups.ValidateBackupAsync(path);
        if (!validation.IsValid)
        {
            await dialogs.AlertAsync("Cannot restore this file", validation.Message, isError: true);
            return;
        }

        if (!await dialogs.ConfirmAsync("Restore backup",
                $"{validation.Message}\n\nRestoring REPLACES all current data with the contents of this backup. A safety backup of the current data is created first. ScheduleOS restarts afterwards.\n\nFile: {path}",
                "Replace current data", danger: true)) return;

        RestoreResult? result = null;
        await RunAsync(async () => result = await backups.RestoreAsync(path));
        if (result is null) return;
        if (!result.Success)
        {
            await dialogs.AlertAsync("Restore failed", result.Message, isError: true);
            return;
        }

        await dialogs.AlertAsync("Restore complete", result.Message + (result.SafetyBackupPath is null ? "" : $"\n\nPrevious data saved to:\n{result.SafetyBackupPath}"));
        App.Restart();
    }

    [RelayCommand]
    private async Task CheckIntegrityAsync()
    {
        Integrity = "Checking…";
        await RunAsync(async () => Integrity = await database.RunIntegrityCheckAsync());
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        var target = path switch
        {
            "data" => DataDirectory,
            "logs" => LogDirectory,
            _ => BackupDirectory,
        };
        Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task ResetDemoAsync()
    {
        if (!await dialogs.ConfirmAsync("Reset demo data",
                "Delete all records flagged as demo data and generate a fresh demo workspace relative to today? Records you created yourself are kept.", "Reset demo data", danger: true))
            return;
        await RunAsync(() => demo.ResetAsync(), "Demo data was reset.");
    }

    // ---------------------------------------------------------------- appearance

    [RelayCommand]
    private async Task SaveAppearanceAsync()
    {
        theme.Apply(SelectedTheme, Density);
        await RunAsync(() => settings.SaveAppearanceAsync(SelectedTheme, Density, SidebarCollapsed), "Appearance saved.");
    }

    [RelayCommand]
    private Task ChangePasswordAsync() => dialogs.ShowAsync(new ChangePasswordDialogViewModel(auth));
}
