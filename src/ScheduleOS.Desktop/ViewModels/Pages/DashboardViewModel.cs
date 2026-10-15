using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Reporting;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Staff;
using ScheduleOS.Desktop.Controls;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.ViewModels.Pages;

public sealed record KpiCard(string Title, string Value, string? Suffix, string Delta, string DeltaBrushKey, string Caption, string Icon, string IconBrushKey, string IconBgKey, string AutomationId);

public sealed record RankedStaff(int Rank, StaffPerformanceDto Staff);

public sealed record ActivityRow(ActivityItem Item, string When, string Icon);

public sealed record ScheduleRow(ScheduleItem Item, bool CanConfirm, bool CanCancel, bool CanReschedule);

public sealed partial class DashboardViewModel(
    DashboardService dashboard,
    UserSession session,
    AppState state,
    IClock clock,
    Workflows workflows,
    NavigationService navigation) : PageViewModel
{
    public override string Title => "Dashboard";
    protected override DataArea RefreshOn => DataArea.Appointments | DataArea.Payments | DataArea.Customers | DataArea.Staff;

    public ObservableCollection<KpiCard> Kpis { get; } = [];
    public ObservableCollection<ScheduleItem> TodaysSchedule { get; } = [];
    public ObservableCollection<ScheduleRow> Upcoming { get; } = [];
    public ObservableCollection<RankedStaff> TopStaff { get; } = [];
    public ObservableCollection<ActivityRow> Activity { get; } = [];
    public ObservableCollection<ChartSlice> StatusLegend { get; } = [];

    [ObservableProperty] private DateTime _date = DateTime.Today;
    [ObservableProperty] private string _greeting = "";
    [ObservableProperty] private string _dateText = "";
    [ObservableProperty] private IReadOnlyList<ChartPoint> _weeklyRevenue = [];
    [ObservableProperty] private IReadOnlyList<ChartSlice> _statusSlices = [];
    [ObservableProperty] private string _weekRange = "";
    [ObservableProperty] private string _weekTotal = "";
    [ObservableProperty] private double? _weekChange;
    [ObservableProperty] private string _statusTotal = "0";
    [ObservableProperty] private int _conflictsDetected;
    [ObservableProperty] private bool _showRevenue;

    public bool CanViewCustomers => session.Has(Permission.ViewCustomers);

    public override async Task LoadAsync()
    {
        var now = clock.Now;
        var firstName = session.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? session.DisplayName;
        Greeting = (now.Hour < 12 ? "Good morning" : now.Hour < 18 ? "Good afternoon" : "Good evening") + $", {firstName}!";
        DateText = Date.ToString("ddd, MMM d, yyyy", CultureInfo.InvariantCulture);
        ShowRevenue = session.Has(Permission.ViewPayments);

        var d = await dashboard.GetAsync(DateOnly.FromDateTime(Date), state.SelectedLocationId);
        var isToday = Date.Date == now.Date;
        var dayWord = isToday ? "Today's" : $"{Date:MMM d}";

        Kpis.Clear();
        Kpis.Add(new KpiCard($"{dayWord} Appointments", d.TodaysAppointments.Value.ToString(CultureInfo.InvariantCulture), null,
            new ChangeConverter().Convert(d.TodaysAppointments.ChangePercent!, typeof(string), null!, CultureInfo.InvariantCulture) as string ?? "",
            d.TodaysAppointments.ChangePercent is null or >= 0 ? "Brush.Success" : "Brush.Danger", d.TodaysAppointments.Caption, "", "Brush.Info", "Brush.InfoLight", "Kpi_Appointments"));
        Kpis.Add(new KpiCard("Confirmed", d.Confirmed.Value.ToString(CultureInfo.InvariantCulture), null, Percent(d.Confirmed), "Brush.Success",
            d.Confirmed.Caption, "", "Brush.Success", "Brush.SuccessLight", "Kpi_Confirmed"));
        Kpis.Add(new KpiCard("Pending", d.Pending.Value.ToString(CultureInfo.InvariantCulture), null, Percent(d.Pending), "Brush.WarningText",
            d.Pending.Caption, "", "Brush.Warning", "Brush.WarningLight", "Kpi_Pending"));
        Kpis.Add(new KpiCard("Available Staff", d.AvailableStaff.Value.ToString(CultureInfo.InvariantCulture), $" / {d.AvailableStaff.Total}", Percent(d.AvailableStaff),
            "Brush.Success", d.AvailableStaff.Caption, "", "Brush.Info", "Brush.InfoLight", "Kpi_Staff"));
        if (ShowRevenue)
            Kpis.Add(new KpiCard(isToday ? "Revenue Today" : $"Revenue {Date:MMM d}", Formats.Money(d.RevenueToday.Value), null,
                new ChangeConverter().Convert(d.RevenueToday.ChangePercent!, typeof(string), null!, CultureInfo.InvariantCulture) as string ?? "",
                d.RevenueToday.ChangePercent is null or >= 0 ? "Brush.Success" : "Brush.Danger", d.RevenueToday.Caption, "", "Brush.Success", "Brush.SuccessLight", "Kpi_Revenue"));

        WeeklyRevenue = d.WeeklyRevenue.Select(w => new ChartPoint(w.Date.ToString("ddd d", CultureInfo.InvariantCulture), w.AmountMinor,
            $"{w.Date:ddd, MMM d}: {Formats.Money(w.AmountMinor)}", w.AmountMinor > 0 && w.AmountMinor == d.WeeklyRevenue.Max(x => x.AmountMinor))).ToList();
        WeekRange = d.WeeklyRevenue.Count == 0 ? "" : $"{d.WeeklyRevenue[0].Date:MMM d} – {d.WeeklyRevenue[^1].Date:MMM d, yyyy}";
        WeekTotal = Formats.Money(d.WeekTotalMinor);
        WeekChange = d.WeekChangePercent;

        var all = new[] { AppointmentStatus.Confirmed, AppointmentStatus.Pending, AppointmentStatus.CheckedIn, AppointmentStatus.InProgress,
            AppointmentStatus.Completed, AppointmentStatus.Cancelled, AppointmentStatus.NoShow };
        var total = d.StatusCounts.Sum(s => s.Count);
        var slices = all.Select(s => new ChartSlice(Formats.Status(s), d.StatusCounts.FirstOrDefault(c => c.Status == s)?.Count ?? 0, Palette.StatusColor(s),
            total == 0 ? "0%" : $"{(d.StatusCounts.FirstOrDefault(c => c.Status == s)?.Count ?? 0) * 100.0 / total:0}%")).ToList();
        StatusSlices = slices;
        StatusLegend.Clear();
        foreach (var s in slices.Where(s => s.Value > 0 || s.Label is "Confirmed" or "Pending" or "Cancelled" or "No Show")) StatusLegend.Add(s);
        StatusTotal = total.ToString(CultureInfo.InvariantCulture);

        TodaysSchedule.Clear();
        foreach (var s in d.TodaysSchedule) TodaysSchedule.Add(s);
        Upcoming.Clear();
        var manage = session.Has(Permission.ManageAppointments);
        foreach (var u in d.Upcoming)
            Upcoming.Add(new ScheduleRow(u, manage && u.Status == AppointmentStatus.Pending, manage, manage));
        TopStaff.Clear();
        var rank = 1;
        foreach (var s in d.TopStaff) TopStaff.Add(new RankedStaff(rank++, s));
        Activity.Clear();
        foreach (var a in d.Activity) Activity.Add(new ActivityRow(a, Formats.Relative(a.Timestamp, now), ActivityIcon(a.Action)));
        ConflictsDetected = d.ConflictsDetected;
    }

    private static string Percent(KpiValue k) => k.Total is > 0 ? $"{k.Value * 100.0 / k.Total.Value:0}%" : "";

    private static string ActivityIcon(string action) => action switch
    {
        "Appointment.Created" => "",
        "Appointment.Rescheduled" => "",
        "Appointment.Completed" => "",
        "Appointment.Cancelled" or "Appointment.NoShow" => "",
        "Payment.Recorded" or "Payment.Refunded" => "",
        "Customer.Created" => "",
        "Invoice.Issued" => "",
        _ => "",
    };

    partial void OnDateChanged(DateTime value) => RequestReload();

    [RelayCommand] private void PreviousDay() => Date = Date.AddDays(-1);
    [RelayCommand] private void NextDay() => Date = Date.AddDays(1);
    [RelayCommand] private void Today() => Date = DateTime.Today;

    [RelayCommand] private Task OpenAppointmentAsync(ScheduleItem item) => workflows.OpenAppointmentAsync(item.AppointmentId);
    [RelayCommand] private Task OpenUpcomingAsync(ScheduleRow row) => workflows.OpenAppointmentAsync(row.Item.AppointmentId);

    [RelayCommand]
    private Task ConfirmAsync(ScheduleRow row) => RunAsync(() => workflows.ChangeStatusAsync(row.Item.AppointmentId, row.Item.Version, AppointmentStatus.Confirmed, row.Item.Reference));

    [RelayCommand]
    private Task CancelAppointmentAsync(ScheduleRow row) => RunAsync(() => workflows.ChangeStatusAsync(row.Item.AppointmentId, row.Item.Version, AppointmentStatus.Cancelled, row.Item.Reference));

    [RelayCommand]
    private Task RescheduleAsync(ScheduleRow row) => workflows.RescheduleAsync(row.Item.AppointmentId);

    [RelayCommand]
    private async Task OpenActivityAsync(ActivityRow row)
    {
        if (row.Item.EntityType == "Appointment" && row.Item.EntityId is { } id) await workflows.OpenAppointmentAsync(id);
        else if (row.Item.CustomerId is { } cid) await workflows.OpenCustomerAsync(cid);
    }

    [RelayCommand] private Task ViewCalendarAsync() => navigation.NavigateAsync<CalendarViewModel>();
    [RelayCommand] private Task ViewAppointmentsAsync() => navigation.NavigateAsync<AppointmentsViewModel>();
    [RelayCommand] private Task ViewCustomersAsync() => navigation.NavigateAsync<CustomersViewModel>();
    [RelayCommand] private Task ViewAnalyticsAsync() => navigation.NavigateAsync<AnalyticsViewModel>();

    [RelayCommand]
    private Task CheckConflictsAsync() => workflows.CheckConflictsAsync(DateTime.Now, DateTime.Today.AddDays(14), state.SelectedLocationId);
}
