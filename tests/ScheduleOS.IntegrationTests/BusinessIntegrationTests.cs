using Microsoft.Data.Sqlite;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Catalog;
using ScheduleOS.Application.Customers;
using ScheduleOS.Application.Notifications;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Reporting;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Application.Staff;
using ScheduleOS.Domain;
using ScheduleOS.Infrastructure.Backup;

namespace ScheduleOS.IntegrationTests;

public class DatabaseTests
{
    [Fact]
    public async Task Migrations_triggers_wal_and_foreign_keys_are_in_place()
    {
        await using var h = await TestHost.CreateAsync(onboard: false);
        var schema = await h.Get<IDatabaseInfo>().GetSchemaVersionAsync();
        Assert.EndsWith("_IntegrityTriggers", schema);
        Assert.Equal(8L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'trg_%'"));
        Assert.Equal("wal", await h.SqlScalarAsync<string>("PRAGMA journal_mode"));
        Assert.Equal("ok", await h.Get<IDatabaseInfo>().RunIntegrityCheckAsync());
        var ex = await Assert.ThrowsAsync<SqliteException>(() => h.SqlExecAsync(
            "INSERT INTO StaffShifts (StaffMemberId, DayOfWeek, StartTime, EndTime) VALUES (9999, 1, '09:00:00', '17:00:00')"));
        Assert.Contains("FOREIGN KEY", ex.Message);
        var check = await Assert.ThrowsAsync<SqliteException>(() => h.SqlExecAsync(
            "INSERT INTO Resources (Name, Type, Capacity, IsActive, IsArchived, IsDemo, Version) VALUES ('X', 0, 0, 1, 0, 0, 1)"));
        Assert.Contains("CHECK", check.Message);
    }

    [Fact]
    public async Task Onboarding_creates_owner_once_and_login_is_protected()
    {
        await using var h = await TestHost.CreateAsync();
        var auth = h.Get<AuthService>();
        await Assert.ThrowsAsync<DomainException>(() => auth.CompleteOnboardingAsync(new OnboardingRequest("X", "Y", "second", "Another-pass-1", "USD", 2, false)));
        auth.Logout();
        for (var i = 0; i < 5; i++) Assert.Equal(LoginOutcome.InvalidCredentials, (await auth.LoginAsync(TestHost.OwnerUser, "wrong-password-1")).Outcome);
        Assert.Equal(LoginOutcome.LockedOut, (await auth.LoginAsync(TestHost.OwnerUser, h.OwnerPassword)).Outcome);
        h.Clock.Now = h.Clock.Now.AddMinutes(6);
        Assert.Equal(LoginOutcome.Success, (await auth.LoginAsync(TestHost.OwnerUser, h.OwnerPassword)).Outcome);
        Assert.Equal(0L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Users WHERE PasswordHash LIKE '%" + h.OwnerPassword + "%'"));
    }

    [Fact]
    public async Task Settings_persist_across_instances()
    {
        await using var h = await TestHost.CreateAsync();
        var settings = h.Get<SettingsService>();
        var s = settings.Current.Clone();
        s.SlotIntervalMinutes = 30;
        s.TaxRateBasisPoints = 825;
        s.CancellationPolicy = "48 hours notice please";
        await settings.SaveAsync(s);
        await using var peer = await h.CreatePeerAsync();
        var loaded = peer.Get<SettingsService>().Current;
        Assert.Equal(30, loaded.SlotIntervalMinutes);
        Assert.Equal(825, loaded.TaxRateBasisPoints);
        Assert.Equal("48 hours notice please", loaded.CancellationPolicy);
        s.SlotIntervalMinutes = 7;
        await Assert.ThrowsAsync<ValidationException>(() => settings.SaveAsync(s));
    }
}

public class CustomerStaffServiceTests
{
    [Fact]
    public async Task Customer_crud_duplicates_archive_and_anonymise()
    {
        await using var h = await TestHost.CreateAsync();
        var customers = h.Get<CustomerService>();
        var id = await customers.CreateAsync(new CustomerEditDto { FullName = "Jamie Example", Phone = "(555) 010-7777", Email = "Jamie@Example.com", Tags = ["VIP", "Prefers morning"] }, false);

        var dup = await Assert.ThrowsAsync<DuplicateCustomerException>(() =>
            customers.CreateAsync(new CustomerEditDto { FullName = "Jamie E.", Phone = "555 010 7777" }, false));
        Assert.Single(dup.Matches);
        var second = await customers.CreateAsync(new CustomerEditDto { FullName = "Jamie E.", Phone = "555 010 7777" }, ignoreDuplicates: true);
        Assert.NotEqual(id, second);

        var detail = await customers.GetAsync(id);
        Assert.Contains("VIP", detail.Segments);
        detail.Profile.Notes = "Likes tea";
        await Assert.ThrowsAsync<DuplicateCustomerException>(() => customers.UpdateAsync(id, detail.Version, detail.Profile, false));
        await customers.UpdateAsync(id, detail.Version, detail.Profile, ignoreDuplicates: true);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => customers.UpdateAsync(id, detail.Version, detail.Profile, true));

        var page = await customers.SearchAsync(new CustomerQuery { Search = "010-7777" });
        Assert.Equal(2, page.TotalCount);

        await customers.SetArchivedAsync(second, true);
        Assert.Equal(1, (await customers.SearchAsync(new CustomerQuery { Search = "Jamie" })).TotalCount);

        await customers.AnonymizeAsync(id);
        var anon = await customers.GetAsync(id);
        Assert.True(anon.IsAnonymized);
        Assert.Null(anon.Profile.Email);
        Assert.Empty(anon.Profile.Tags);
        var json = await customers.ExportPersonalDataJsonAsync(second);
        Assert.Contains("Jamie E.", json);
    }

    [Fact]
    public async Task Customer_search_prioritizes_name_matches_over_generic_email_domains()
    {
        await using var h = await TestHost.CreateAsync();
        var customers = h.Get<CustomerService>();
        await customers.CreateAsync(new CustomerEditDto { FullName = "Alpha Person", Email = "alpha@example.com" }, false);
        await customers.CreateAsync(new CustomerEditDto { FullName = "Jordan Example", Email = "jordan@example.com" }, false);

        var result = await customers.SearchAsync(new CustomerQuery { Search = "Example" });

        Assert.Equal("Jordan Example", result.Items[0].FullName);
    }

    [Fact]
    public async Task Staff_schedule_skills_and_service_history_are_preserved()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var staff = h.Get<StaffService>();
        await Assert.ThrowsAsync<ValidationException>(() => staff.SaveShiftsAsync(f.Alex,
            [new ShiftDto(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(13, 0), null, null), new ShiftDto(DayOfWeek.Monday, new TimeOnly(12, 0), new TimeOnly(18, 0), null, null)]));

        var appts = h.Get<AppointmentService>();
        var mon10 = TestHost.DefaultNow.Date.AddHours(10);
        var r = await appts.CreateAsync(f.Booking(mon10, f.Alex, f.RoomA));

        var affected = await staff.SaveShiftsAsync(f.Alex, [new ShiftDto(DayOfWeek.Monday, new TimeOnly(13, 0), new TimeOnly(18, 0), null, null)]);
        Assert.Single(affected);
        var detail = await staff.GetAsync(f.Alex);
        Assert.Single(detail.Shifts);

        // Changing or archiving a catalog service must not rewrite the appointment's agreed name or price.
        var catalog = h.Get<CatalogService>();
        var (dto, version, _) = await catalog.GetAsync(f.Massage);
        dto.Name = "Signature Massage";
        dto.PriceMinor = 20_000;
        await catalog.UpdateAsync(f.Massage, version, dto);
        await catalog.SetArchivedAsync(f.Massage, true);
        var d = await appts.GetDetailAsync(r.Id);
        Assert.Equal("Massage", d.Segments[0].ServiceName);
        Assert.Equal(12_000, d.TotalMinor);
        Assert.DoesNotContain(await catalog.GetBookableAsync(), s => s.Id == f.Massage);

        await Assert.ThrowsAsync<ValidationException>(() => staff.SetArchivedAsync(f.Alex, true));
    }
}

public class PaymentTests
{
    private static DateTime Mon(int h, int m = 0) => TestHost.DefaultNow.Date.AddHours(h).AddMinutes(m);

    [Fact]
    public async Task Payments_refunds_and_reversals_keep_an_exact_immutable_ledger()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var pay = h.Get<PaymentService>();
        var r = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));

        await pay.RecordAsync(new RecordPaymentRequest { AppointmentId = r.Id, Kind = PaymentKind.Deposit, Method = PaymentMethod.CardExternal, AmountMinor = 3_000 });
        Assert.Equal("Paid Deposit", (await appts.GetDetailAsync(r.Id)).PaymentStatus);
        var over = await Assert.ThrowsAsync<ValidationException>(() => pay.RecordAsync(new RecordPaymentRequest { AppointmentId = r.Id, Method = PaymentMethod.Cash, AmountMinor = 9_001 }));
        Assert.Contains("exceeds", over.Message);
        await pay.RecordAsync(new RecordPaymentRequest { AppointmentId = r.Id, Method = PaymentMethod.Cash, AmountMinor = 9_000 });

        var d = await appts.GetDetailAsync(r.Id);
        Assert.Equal(12_000, d.PaidMinor);
        Assert.Equal(0, d.BalanceMinor);
        Assert.Equal("Paid", d.PaymentStatus);

        var cash = d.Payments.Single(p => p.Kind == PaymentKind.Payment);
        await Assert.ThrowsAsync<ValidationException>(() => pay.RefundAsync(cash.Id, 9_001, PaymentMethod.Cash, "Too much"));
        await pay.RefundAsync(cash.Id, 4_000, PaymentMethod.Cash, "Service shortened");
        var deposit = d.Payments.Single(p => p.Kind == PaymentKind.Deposit);
        await pay.ReverseAsync(deposit.Id, "Recorded on wrong appointment");
        await Assert.ThrowsAsync<ValidationException>(() => pay.ReverseAsync(deposit.Id, "Again"));

        d = await appts.GetDetailAsync(r.Id);
        Assert.Equal(12_000 - 4_000 - 3_000, d.PaidMinor);
        Assert.Equal(4, d.Payments.Count);
        Assert.Contains(d.History, e => e.Type == AppointmentEventType.RefundRecorded);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => h.SqlExecAsync("UPDATE Payments SET AmountMinor = 1"));
        Assert.Contains("SCHEDULEOS_PAYMENT_IMMUTABLE", ex.Message);
        await Assert.ThrowsAsync<SqliteException>(() => h.SqlExecAsync("DELETE FROM Payments"));
    }

    [Fact]
    public async Task Invoice_is_issued_tracks_status_and_is_voided_when_services_change()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var invoices = h.Get<InvoiceService>();
        var r = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        var id1 = await invoices.IssueAsync(r.Id);
        Assert.Equal(id1, await invoices.IssueAsync(r.Id)); // idempotent while unchanged
        var doc = await invoices.GetDocumentAsync(id1);
        Assert.Equal(12_000, doc.TotalMinor);
        Assert.Equal(InvoiceStatus.Issued, doc.Status);

        await h.Get<PaymentService>().RecordAsync(new RecordPaymentRequest { AppointmentId = r.Id, Method = PaymentMethod.Cash, AmountMinor = 5_000 });
        Assert.Equal(InvoiceStatus.PartiallyPaid, (await invoices.GetDocumentAsync(id1)).Status);

        var d = await appts.GetDetailAsync(r.Id);
        await appts.UpdateServicesAsync(new UpdateServicesRequest
        {
            AppointmentId = r.Id, ExpectedVersion = d.Version,
            Services = [new ServiceLineRequest(f.Massage, f.Alex, f.RoomA), new ServiceLineRequest(f.Consult, f.Alex, null)],
        });
        var id2 = await invoices.IssueAsync(r.Id);
        Assert.NotEqual(id1, id2);
        Assert.Equal(InvoiceStatus.Void, (await invoices.GetDocumentAsync(id1)).Status);
        Assert.Equal(16_000, (await invoices.GetDocumentAsync(id2)).TotalMinor);

        var pdf = Path.Combine(h.Root, "invoice.pdf");
        await h.Get<IDocumentPdfWriter>().WriteInvoiceAsync(pdf, await invoices.GetDocumentAsync(id2));
        Assert.True(new FileInfo(pdf).Length > 1000);
        Assert.StartsWith("%PDF", File.ReadAllText(pdf)[..4]);
    }

    [Fact]
    public async Task Revenue_is_aggregated_from_recorded_payments_consistently()
    {
        await using var h = await TestHost.CreateAsync(TestHost.DefaultNow.Date.AddHours(17));
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var pay = h.Get<PaymentService>();
        h.Clock.Now = TestHost.DefaultNow; // book in the future relative to "now"
        var a = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        var b = await appts.CreateAsync(f.Booking(Mon(10), f.Blair, f.RoomB, customer: f.Customer2));
        await appts.CreateAsync(f.Booking(Mon(14), f.Blair, f.RoomB, customer: f.Customer2)); // never paid
        h.Clock.Now = Mon(17);
        await pay.RecordAsync(new RecordPaymentRequest { AppointmentId = a.Id, Method = PaymentMethod.Cash, AmountMinor = 12_000 });
        var receipt = await pay.RecordAsync(new RecordPaymentRequest { AppointmentId = b.Id, Method = PaymentMethod.BankTransfer, AmountMinor = 6_000 });
        var bPayment = (await appts.GetDetailAsync(b.Id)).Payments.Single();
        await pay.RefundAsync(bPayment.Id, 1_000, PaymentMethod.BankTransfer, "Goodwill");

        var dashboard = await h.Get<DashboardService>().GetAsync(DateOnly.FromDateTime(Mon(0)), null);
        Assert.Equal(17_000, dashboard.RevenueToday.Value);
        Assert.Equal(3, dashboard.TodaysAppointments.Value);
        Assert.Equal(17_000, dashboard.WeeklyRevenue.Single(w => w.Date == DateOnly.FromDateTime(Mon(0))).AmountMinor);

        var report = await h.Get<AnalyticsService>().GetReportAsync(Mon(0), Mon(0).AddDays(1), null);
        Assert.Equal(17_000, report.RevenueMinor);
        Assert.Equal(18_000, report.InflowMinor);
        Assert.Equal(1_000, report.RefundsMinor);
        Assert.Equal(3, report.TotalAppointments);
        Assert.Equal(2, report.UniqueCustomers);
        var (page, totals) = await pay.ListAsync(new PaymentQuery { From = Mon(0), To = Mon(24) });
        Assert.Equal(17_000, totals.NetMinor);
        Assert.Equal(3, page.TotalCount);
        Assert.StartsWith("RCT-", receipt);

        // Exports produce the same totals as on screen.
        var tables = h.Get<AnalyticsService>().ToTables(report);
        var csv = Path.Combine(h.Root, "report.csv");
        await h.Get<IReportExporter>().ExportCsvAsync(csv, tables);
        Assert.Contains("Net revenue (USD),170.00", await File.ReadAllTextAsync(csv));
        var xlsx = Path.Combine(h.Root, "report.xlsx");
        await h.Get<IReportExporter>().ExportXlsxAsync(xlsx, tables);
        Assert.True(new FileInfo(xlsx).Length > 3000);
        var pdf = Path.Combine(h.Root, "report.pdf");
        await h.Get<IReportExporter>().ExportPdfAsync(pdf, "Report", "Test", tables);
        Assert.True(new FileInfo(pdf).Length > 1000);
    }

    [Fact]
    public async Task Permissions_are_enforced_in_services()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var auth = h.Get<AuthService>();
        await auth.CreateUserAsync("frontdesk", "Front Desk", UserRole.Receptionist, null, "Reception-123");
        await auth.CreateUserAsync("alex", "Alex", UserRole.Staff, f.Alex, "Therapist-123");
        var appts = h.Get<AppointmentService>();
        var mine = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        var other = await appts.CreateAsync(f.Booking(Mon(10), f.Blair, f.RoomB, customer: f.Customer2));
        await h.Get<PaymentService>().RecordAsync(new RecordPaymentRequest { AppointmentId = mine.Id, Method = PaymentMethod.Cash, AmountMinor = 1_000 });
        var paymentId = (await appts.GetDetailAsync(mine.Id)).Payments[0].Id;

        auth.Logout();
        Assert.Equal(LoginOutcome.Success, (await auth.LoginAsync("frontdesk", "Reception-123")).Outcome);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => h.Get<PaymentService>().RefundAsync(paymentId, 500, PaymentMethod.Cash, "x"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => h.Get<SettingsService>().SaveAsync(new AppSettings()));
        await h.Get<PaymentService>().RecordAsync(new RecordPaymentRequest { AppointmentId = mine.Id, Method = PaymentMethod.Cash, AmountMinor = 1_000 });

        auth.Logout();
        Assert.Equal(LoginOutcome.Success, (await auth.LoginAsync("alex", "Therapist-123")).Outcome);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => appts.CreateAsync(f.Booking(Mon(15), f.Alex, f.RoomA)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => appts.GetDetailAsync(other.Id));
        var list = await appts.QueryAsync(new AppointmentQuery());
        Assert.Equal(mine.Id, Assert.Single(list.Items).Id);
        var detail = await appts.GetDetailAsync(mine.Id);
        Assert.True(detail.Customer.ContactHidden);
        Assert.DoesNotContain("example.com", detail.Customer.Email ?? "");
        await appts.ChangeStatusAsync(mine.Id, detail.Version, AppointmentStatus.CheckedIn); // own appointment, same day
    }
}

public class ReminderTests
{
    private static DateTime Tue(int h) => TestHost.DefaultNow.Date.AddDays(1).AddHours(h);

    [Fact]
    public async Task Reminders_are_queued_processed_honestly_and_never_duplicated()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var reminders = h.Get<ReminderService>();
        var r = await appts.CreateAsync(f.Booking(Tue(15), f.Alex, f.RoomA, email: true));
        var d = await appts.GetDetailAsync(r.Id);
        Assert.Contains(d.Reminders, x => x.Type == ReminderType.Confirmation && x.Channel == ReminderChannel.Email);
        Assert.Contains(d.Reminders, x => x.Type == ReminderType.Upcoming && x.Channel == ReminderChannel.Email && x.DueAt == Tue(15).AddHours(-24));
        Assert.Contains(d.Reminders, x => x.Type == ReminderType.Upcoming && x.Channel == ReminderChannel.InApp);

        Assert.Equal(1, await reminders.ProcessDueAsync()); // confirmation is due now
        Assert.Equal(0, await reminders.ProcessDueAsync());
        d = await appts.GetDetailAsync(r.Id);
        var confirmation = d.Reminders.Single(x => x.Type == ReminderType.Confirmation);
        Assert.Equal(ReminderStatus.NotSent, confirmation.Status); // SMTP not configured → never reported as sent
        Assert.Contains(d.History, e => e.Type == AppointmentEventType.ReminderNotSent);
        Assert.DoesNotContain(d.History, e => e.Type == AppointmentEventType.ReminderSent);

        // Rescheduling supersedes the pending reminders and queues new ones.
        await appts.RescheduleAsync(new RescheduleRequest { AppointmentId = r.Id, ExpectedVersion = d.Version, NewStart = Tue(16) });
        d = await appts.GetDetailAsync(r.Id);
        Assert.Contains(d.Reminders, x => x.Type == ReminderType.Upcoming && x.Status == ReminderStatus.Cancelled);
        Assert.Contains(d.Reminders, x => x.Type == ReminderType.Upcoming && x.Status == ReminderStatus.Scheduled && x.DueAt == Tue(16).AddHours(-24));

        // In-app reminder fires 30 minutes before and creates a notification exactly once.
        h.Clock.Now = Tue(15).AddMinutes(31);
        await reminders.ProcessDueAsync();
        await reminders.ProcessDueAsync();
        var notes = await h.Get<NotificationService>().ListAsync();
        Assert.Single(notes, n => n.AppointmentId == r.Id && n.Title.StartsWith("Upcoming"));
        Assert.Equal(0L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM (SELECT DedupKey FROM Reminders GROUP BY DedupKey HAVING COUNT(*) > 1)"));
    }
}

public class BackupAndDemoTests
{
    [Fact]
    public async Task Backup_and_restore_round_trip()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var backups = h.Get<BackupService>();
        var info = await backups.CreateBackupAsync(null, "manual");
        Assert.True(File.Exists(info.FilePath));
        Assert.True(File.Exists(info.FilePath + ".json"));
        Assert.True((await backups.ValidateBackupAsync(info.FilePath)).IsValid);

        var later = await h.Get<CustomerService>().CreateAsync(new CustomerEditDto { FullName = "Added After Backup", Email = "after@example.com" }, false);
        Assert.True(later > f.Customer2);

        var result = await backups.RestoreAsync(info.FilePath);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.SafetyBackupPath);
        Assert.Equal(0L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Customers WHERE FullName = 'Added After Backup'"));
        Assert.Equal(2L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Customers"));
        Assert.Equal("ok", await h.Get<IDatabaseInfo>().RunIntegrityCheckAsync());

        // The application keeps working after restore.
        await h.Get<CustomerService>().CreateAsync(new CustomerEditDto { FullName = "After Restore", Email = "restore@example.com" }, false);

        var junk = Path.Combine(h.Root, "junk.sosbak");
        await File.WriteAllTextAsync(junk, "not a database");
        Assert.False((await backups.ValidateBackupAsync(junk)).IsValid);
        Assert.False((await backups.RestoreAsync(junk)).Success);
        Assert.True((await backups.ListBackupsAsync()).Count >= 2);
    }

    [Fact]
    public async Task Demo_seed_is_idempotent_conflict_free_and_resettable()
    {
        await using var h = await TestHost.CreateAsync(DateTime.Today.AddHours(11), demo: true);
        var demo = h.Get<IDemoDataService>();
        await demo.SeedAsync();
        var appointments = await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Appointments");
        Assert.Equal(8L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Staff"));
        Assert.Equal(25L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Services"));
        Assert.Equal(10L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Resources"));
        Assert.Equal(50L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Customers"));
        Assert.True(appointments > 200, $"only {appointments} appointments");

        await demo.SeedAsync();
        Assert.Equal(appointments, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Appointments"));

        var conflicts = await h.Get<SchedulingService>().ScanConflictsAsync(DateTime.Today.AddDays(-30), DateTime.Today.AddDays(15), null);
        Assert.DoesNotContain(conflicts.SelectMany(c => c.Conflicts), c => c.Severity == ConflictSeverity.Blocking && c.Type != ConflictType.StaffTimeOff);
        Assert.DoesNotContain(conflicts.SelectMany(c => c.Conflicts), c => c.Type is ConflictType.StaffOverlap or ConflictType.ResourceOverlap);

        var dashboard = await h.Get<DashboardService>().GetAsync(DateOnly.FromDateTime(DateTime.Today), null);
        Assert.True(dashboard.TodaysAppointments.Value > 0);
        Assert.NotEmpty(dashboard.Activity);

        // A real (non-demo) record survives a reset.
        var realCustomer = await h.Get<CustomerService>().CreateAsync(new CustomerEditDto { FullName = "Real Person", Email = "real@example.org" }, false);
        await demo.ResetAsync();
        Assert.Equal(1L, await h.SqlScalarAsync<long>($"SELECT COUNT(*) FROM Customers WHERE Id = {realCustomer}"));
        Assert.Equal(51L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Customers"));
    }
}
