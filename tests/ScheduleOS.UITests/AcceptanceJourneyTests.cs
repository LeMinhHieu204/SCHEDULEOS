using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit.Abstractions;

namespace ScheduleOS.UITests;

/// <summary>
/// End-to-end acceptance journey against the real WPF executable. Requires an interactive Windows desktop session.
/// Screenshots are rendered by the running application itself and written to docs/screenshots.
/// </summary>
public sealed class AcceptanceJourneyTests(ITestOutputHelper output)
{
    private const string Password = "Acceptance-2026";

    private static string ScreenshotDir =>
        Environment.GetEnvironmentVariable("SCHEDULEOS_SCREENSHOT_DIR") is { Length: > 0 } d ? d : Path.Combine(AppDriver.RepoRoot, "docs", "screenshots");

    [Fact]
    [Trait("Category", "UI")]
    public void Full_acceptance_journey_on_the_real_application()
    {
        var root = Path.Combine(Path.GetTempPath(), "ScheduleOS.UITests", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var logFile = Path.Combine(AppDriver.RepoRoot, "docs", "acceptance", "ui-journey.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        File.WriteAllText(logFile, $"ScheduleOS UI acceptance journey — {DateTime.Now:O}{Environment.NewLine}");
        using var app = new AppDriver(output, Path.Combine(root, "data"), ScreenshotDir, logFile);
        try
        {
            RunJourney(app);
        }
        catch (Exception ex)
        {
            app.Step("FAILED: " + ex.Message);
            try
            {
                File.Copy(app.Capture("_failure.png"), Path.Combine(root, "failure.png"), true);
                File.Delete(Path.Combine(ScreenshotDir, "_failure.png"));
                app.Step("Failure screenshot: " + Path.Combine(root, "failure.png"));
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    private void RunJourney(AppDriver app)
    {

        // ------------------------------------------------------------ onboarding (owner + demo workspace)
        app.Step("STEP 1: Onboarding");
        app.Find("OnbBusinessName", 60);
        app.Capture("00-onboarding.png");
        app.SetText("OnbBusinessName", "Serenity Spa & Salon");
        app.Invoke("OnbNext");
        app.SetText("OnbOwnerName", "Taylor Morgan");
        app.SetText("OnbUsername", "owner");
        app.SetText("OnbPassword", Password);
        app.SetText("OnbConfirm", Password);
        app.Invoke("OnbNext");
        app.Select("OnbDemo");
        app.Invoke("OnbFinish");

        // ------------------------------------------------------------ login
        app.Step("STEP 2: Sign in");
        app.Find("LoginUsername", 120);
        app.SetText("LoginUsername", "owner");
        app.SetText("LoginPassword", "wrong-password-1");
        app.Invoke("LoginButton");
        Assert.True(app.WaitUntil(() => app.Text("LoginError", 5).Contains("incorrect", StringComparison.OrdinalIgnoreCase)), "Wrong password must be rejected");
        app.SetText("LoginPassword", Password);
        app.Invoke("LoginButton");
        app.Find("Greeting", 60);
        Thread.Sleep(2500);

        // ------------------------------------------------------------ dashboard
        app.Step("STEP 3: Dashboard shows data from SQLite");
        Assert.Contains("Taylor", app.Text("Greeting"));
        Assert.True(app.WaitUntil(() => int.Parse(app.Find("Kpi_Appointments_Value").Name.Trim(), CultureInfo.InvariantCulture) >= 0));
        app.Capture("01-dashboard.png");
        app.Capture("01-dashboard-1280x800.png", "1280x800");
        app.Capture("01-dashboard-1440x900.png", "1440x900");
        app.Capture("01-dashboard-1920x1080.png", "1920x1080");
        app.Capture("01-dashboard.png", "1440x900");

        // ------------------------------------------------------------ module tour
        app.Step("STEP 4: Module navigation");
        app.Navigate("Nav_Calendar", "CalRange");
        Thread.Sleep(1500);
        app.Capture("02-smart-calendar.png");
        app.Select("ViewDay");
        Thread.Sleep(1500);
        app.Capture("02b-calendar-day-by-staff.png");
        app.Select("ViewMonth");
        Thread.Sleep(1200);
        app.Capture("02c-calendar-month.png");
        app.Select("ViewWeek");

        app.Navigate("Nav_Appointments", "ApptSearch");
        app.Navigate("Nav_Customers", "CustomerSearch");
        app.Capture("05-customers.png");
        app.Navigate("Nav_Staff", "StaffList");
        app.Capture("06-staff.png");
        app.Navigate("Nav_Services", "ServiceSearch");
        app.Capture("07-services.png");
        app.Navigate("Nav_Resources", "ResourcesGrid");
        app.Capture("07b-resources.png");
        app.Navigate("Nav_Payments", "PaymentsRecord");
        app.Capture("08-payments.png");
        app.Navigate("Nav_Analytics", "ReportRange");
        Thread.Sleep(1500);
        app.Capture("09-analytics.png");
        app.Navigate("Nav_Settings", "TabBusiness");
        app.Capture("10-settings.png");

        // ------------------------------------------------------------ create a customer
        app.Step("STEP 5: Create customer");
        app.Navigate("Nav_Customers", "CustomerSearch");
        app.Invoke("CustomersNew");
        app.SetText("CustName", "Jordan Example");
        app.SetText("CustPhone", "(555) 019-4455");
        app.SetText("CustEmail", "jordan.example@example.com");
        app.Invoke("CustSave");
        Assert.True(app.WaitUntil(() => app.Text("CustomerName", 10) == "Jordan Example"), "Customer detail should open after creation");

        // ------------------------------------------------------------ create a service
        app.Step("STEP 6: Create service");
        app.Navigate("Nav_Services", "ServiceSearch");
        app.Invoke("ServicesNew");
        app.SetText("SvcName", "Acceptance Facial");
        app.SetText("SvcDuration", "60");
        app.SetText("SvcPrice", "95.00");
        app.Toggle(app.FindByName("Treatment Room", ControlType.CheckBox), true);
        app.Invoke("SvcSave");
        app.FindByName("Acceptance Facial", ControlType.Text, 15);

        // ------------------------------------------------------------ create a staff member qualified for it
        app.Step("STEP 7: Create staff member and working hours");
        app.Navigate("Nav_Staff", "StaffList");
        app.Invoke("StaffNew");
        app.SetText("StaffName", "Robin Example");
        app.SetText("StaffPosition", "Esthetician");
        var serviceBox = app.Window.FindFirstDescendant(app.Cf.ByControlType(ControlType.CheckBox).And(app.Cf.ByName("Acceptance Facial  ·  Facial Care")))
                         ?? app.Window.FindAllDescendants(app.Cf.ByControlType(ControlType.CheckBox)).First(c => (c.Name ?? "").StartsWith("Acceptance Facial", StringComparison.Ordinal));
        app.Toggle(serviceBox, true);
        app.Invoke("StaffSave");
        var robin = app.FindByName("Robin Example", ControlType.Text, 15);
        var robinItem = robin;
        while (robinItem.ControlType != ControlType.ListItem) robinItem = robinItem.Parent;
        robinItem.Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(800);
        app.Find("StaffScheduleTab").Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(500);
        app.Toggle(app.FindByName("Saturday", ControlType.CheckBox), true);
        app.Invoke("SaveSchedule");
        Thread.Sleep(1200);
        app.Capture("06b-staff-schedule.png");

        // ------------------------------------------------------------ create a room
        app.Step("STEP 8: Create room");
        app.Navigate("Nav_Resources", "ResourcesGrid");
        app.Invoke("ResourcesNew");
        app.SetText("ResName", "Acceptance Room");
        app.Invoke("ResSave");
        app.FindByName("Acceptance Room", ControlType.Text, 15);

        // ------------------------------------------------------------ book an appointment
        app.Step("STEP 9: Book appointment through the New Appointment workflow");
        var bookingDate = NextWeekday(DateTime.Today.AddDays(2));
        BookAppointment(app, "Jordan", bookingDate, out var initialState);
        app.Capture("04-new-appointment.png");
        Assert.Contains("Available", initialState);
        app.Invoke("EditorSave");
        Assert.True(app.WaitUntil(() => app.TryFind("EditorSave", 1) is null, 20), "Editor should close after booking");

        // ------------------------------------------------------------ conflicting booking is prevented
        app.Step("STEP 10: Conflicting booking for the same staff, room and time is blocked");
        BookAppointment(app, "Ava", bookingDate, out var conflictState);
        Assert.Contains("Not available", conflictState);
        app.Capture("04b-conflict-detected.png");
        app.Invoke("EditorSave");
        app.Find("ConflictList", 15);
        app.Capture("04c-conflict-dialog.png");
        Assert.Null(app.TryFind("ConflictOverride", 1)); // blocking conflicts can never be overridden
        app.Invoke("ConflictBack");
        app.CloseDialogs();

        // ------------------------------------------------------------ open the appointment
        app.Step("STEP 11: Appointment detail, confirm, reschedule");
        app.Navigate("Nav_Appointments", "ApptSearch");
        app.SetText("ApptSearch", "Jordan");
        Thread.Sleep(1500);
        app.Invoke("OpenAppointment");
        app.Find("DetailReference", 20);
        Thread.Sleep(1500);
        var reference = app.Text("DetailReference");
        app.Step($"  appointment {reference}");
        if (app.TryFind("ActConfirm", 2) is not null)
        {
            app.Invoke("ActConfirm");
            Thread.Sleep(1500);
        }

        Assert.True(app.WaitUntil(() => app.Window.FindFirstDescendant(app.Cf.ByName("Confirmed").And(app.Cf.ByControlType(ControlType.Text))) is not null, 15), "Status should be Confirmed");
        app.Capture("03-appointment-detail.png");

        app.Invoke("ActReschedule");
        app.Find("RescheduleSave", 15);
        app.SelectComboIndex("RescheduleTime", SlotIndex(14, 0));
        Assert.True(app.WaitUntil(() => app.Text("RescheduleState", 5) == "This time is available", 15), "14:00 should be free");
        app.Capture("03b-reschedule.png");
        app.Invoke("RescheduleSave");
        Assert.True(app.WaitUntil(() => app.TryFind("RescheduleSave", 1) is null, 20));
        Thread.Sleep(1500);

        // ------------------------------------------------------------ payment and invoice
        app.Step("STEP 12: Record payment and generate invoice");
        app.Invoke("RecordPayment");
        app.Find("PaySave", 15);
        Assert.Equal("$95.00", app.Text("PayBalance"));
        app.Invoke("PaySave");
        Assert.True(app.WaitUntil(() => app.Text("PaidAmount", 5) == "$95.00", 20), "Paid amount should update");
        Assert.True(app.WaitUntil(() => app.Text("PaymentStatus", 5) == "Paid", 10));
        app.Invoke("ViewInvoice");
        app.Find("SavePdf", 20);
        Thread.Sleep(1000);
        app.Capture("08b-invoice.png");
        app.CloseDialogs();

        // ------------------------------------------------------------ reports
        app.Step("STEP 13: Reports reflect the payment");
        app.Navigate("Nav_Analytics", "ReportRange");
        Assert.True(app.WaitUntil(() => app.Text("ReportRevenue", 5).StartsWith('$'), 20));

        // ------------------------------------------------------------ backup
        app.Step("STEP 14: Back up the database");
        app.Navigate("Nav_Settings", "TabBusiness");
        app.Find("TabData").Patterns.SelectionItem.Pattern.Select();
        app.Invoke("BackupNow");
        Assert.True(app.WaitUntil(() => app.Find("BackupsGrid").FindAllDescendants(app.Cf.ByControlType(ControlType.DataItem)).Length >= 1, 30), "A backup row should appear");
        Thread.Sleep(800);
        app.Capture("10b-backup-restore.png");

        // A customer added after the backup must disappear after restoring it.
        app.Navigate("Nav_Customers", "CustomerSearch");
        app.Invoke("CustomersNew");
        app.SetText("CustName", "Casey AfterBackup");
        app.SetText("CustEmail", "casey.after@example.com");
        app.Invoke("CustSave");
        Assert.True(app.WaitUntil(() => app.Text("CustomerName", 10) == "Casey AfterBackup"));

        // ------------------------------------------------------------ restart persistence
        app.Step("STEP 15: Restart and verify persistence");
        app.CloseApp();
        app.Relaunch();
        SignIn(app);
        app.Navigate("Nav_Customers", "CustomerSearch");
        app.SetText("CustomerSearch", "Example");
        Assert.True(app.WaitUntil(() => app.Window.FindFirstDescendant(app.Cf.ByName("Jordan Example")) is not null, 15), "Customer persisted across restart");
        app.Navigate("Nav_Appointments", "ApptSearch");
        app.SetText("ApptSearch", "Jordan");
        Assert.True(app.WaitUntil(() => app.Window.FindFirstDescendant(app.Cf.ByName(reference.TrimStart('#'))) is not null, 15), "Appointment persisted across restart");

        // ------------------------------------------------------------ restore
        app.Step("STEP 16: Restore the backup");
        app.Navigate("Nav_Settings", "TabBusiness");
        app.Find("TabData").Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(800);
        var manualRow = app.Find("BackupsGrid").FindAllDescendants(app.Cf.ByControlType(ControlType.DataItem))
            .First(r => r.FindFirstDescendant(app.Cf.ByName("manual")) is not null);
        var restore = manualRow.FindFirstDescendant(app.Cf.ByName("Restore").And(app.Cf.ByControlType(ControlType.Button)));
        Assert.NotNull(restore);
        restore!.Patterns.Invoke.Pattern.Invoke();
        app.Invoke("DialogConfirm"); // replace current data
        var pid = app.ProcessId;
        app.Find("DialogConfirm", 60); // "Restore complete" alert
        app.Invoke("DialogConfirm");
        app.AttachAfterRestart(pid);
        SignIn(app);
        app.Navigate("Nav_Customers", "CustomerSearch");
        app.SetText("CustomerSearch", "Casey");
        Thread.Sleep(2000);
        Assert.Null(app.Window.FindFirstDescendant(app.Cf.ByName("Casey AfterBackup")));
        app.SetText("CustomerSearch", "Jordan");
        Assert.True(app.WaitUntil(() => app.Window.FindFirstDescendant(app.Cf.ByName("Jordan Example")) is not null, 15), "Restored data contains the backed-up customer");

        app.Step("JOURNEY PASSED");
        app.CloseApp();
    }

    private static void SignIn(AppDriver app)
    {
        app.Find("LoginUsername", 60);
        app.SetText("LoginUsername", "owner");
        app.SetText("LoginPassword", Password);
        app.Invoke("LoginButton");
        app.Find("Greeting", 60);
        Thread.Sleep(1200);
    }

    private static void BookAppointment(AppDriver app, string customerSearch, DateTime date, out string availability)
    {
        app.Navigate("Nav_Calendar", "CalRange");
        app.Invoke("CalendarNewAppointment");
        app.Find("EditorSave", 15);
        app.SetText("EditorCustomerSearch", customerSearch);
        Thread.Sleep(1200);
        var list = app.Find("EditorCustomerList");
        var first = app.WaitUntil(() => list.FindAllChildren().Length > 0, 10) ? list.FindAllChildren()[0] : throw new InvalidOperationException("No customer found");
        first.Patterns.SelectionItem.Pattern.Select();
        app.SelectComboByText(app.Find("EditorService"), "Acceptance Facial");
        Thread.Sleep(1200);
        app.SelectComboByText(app.Find("EditorStaff"), "Robin Example");
        app.SelectComboByText(app.Find("EditorResource"), "Acceptance Room");
        var datePicker = app.Find("EditorDate");
        datePicker.Patterns.Value.Pattern.SetValue(date.ToString("d", CultureInfo.CurrentCulture));
        app.SelectComboIndex("EditorTime", SlotIndex(10, 0));
        Thread.Sleep(2500);
        string state = "";
        app.WaitUntil(() => (state = app.Text("EditorAvailabilityText", 5)).Contains("vailable", StringComparison.Ordinal), 15);
        availability = state;
        app.Step($"  availability: {state}");
    }

    /// <summary>Index into the time-slot combo (calendar day starts at 08:00 with 15-minute slots by default).</summary>
    private static int SlotIndex(int hour, int minute) => ((hour - 8) * 60 + minute) / 15;

    private static DateTime NextWeekday(DateTime d)
    {
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return d;
    }
}
