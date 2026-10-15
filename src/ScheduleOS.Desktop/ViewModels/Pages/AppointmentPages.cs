using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Application.Staff;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Desktop.ViewModels.Dialogs;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.ViewModels.Pages;

public sealed record RangePreset(string Name, Func<DateTime, (DateTime From, DateTime To)> Range)
{
    public override string ToString() => Name;

    public static IReadOnlyList<RangePreset> Appointments { get; } =
    [
        new("Today", t => (t, t.AddDays(1))),
        new("Tomorrow", t => (t.AddDays(1), t.AddDays(2))),
        new("This week", t => { var m = t.AddDays(-(((int)t.DayOfWeek + 6) % 7)); return (m, m.AddDays(7)); }),
        new("Next 7 days", t => (t, t.AddDays(7))),
        new("Next 30 days", t => (t, t.AddDays(30))),
        new("Last 7 days", t => (t.AddDays(-7), t.AddDays(1))),
        new("Last 30 days", t => (t.AddDays(-30), t.AddDays(1))),
        new("This month", t => (new DateTime(t.Year, t.Month, 1), new DateTime(t.Year, t.Month, 1).AddMonths(1))),
        new("All time", t => (new DateTime(2000, 1, 1), new DateTime(2100, 1, 1))),
    ];

    public static IReadOnlyList<RangePreset> Reports { get; } =
    [
        new("Today", t => (t, t.AddDays(1))),
        new("Yesterday", t => (t.AddDays(-1), t)),
        new("This Week", t => { var m = t.AddDays(-(((int)t.DayOfWeek + 6) % 7)); return (m, m.AddDays(7)); }),
        new("Last 7 Days", t => (t.AddDays(-6), t.AddDays(1))),
        new("This Month", t => (new DateTime(t.Year, t.Month, 1), new DateTime(t.Year, t.Month, 1).AddMonths(1))),
        new("Last Month", t => (new DateTime(t.Year, t.Month, 1).AddMonths(-1), new DateTime(t.Year, t.Month, 1))),
        new("Last 30 Days", t => (t.AddDays(-29), t.AddDays(1))),
        new("Custom Range", t => (t.AddDays(-29), t.AddDays(1))),
    ];
}

public sealed partial class AppointmentsViewModel(AppointmentService appointments, StaffService staff, AppState state, Workflows workflows,
    IReportExporter exporter, FileDialogs files, UserSession session) : PageViewModel
{
    private CancellationTokenSource? _searchCts;

    public override string Title => "Appointments";
    protected override DataArea RefreshOn => DataArea.Appointments | DataArea.Payments;

    public IReadOnlyList<RangePreset> Presets => RangePreset.Appointments;
    public ObservableCollection<Lookup> StaffOptions { get; } = [];
    public ObservableCollection<AppointmentListItem> Items { get; } = [];
    public IReadOnlyList<string> StatusOptions { get; } = ["All statuses", .. Enum.GetValues<AppointmentStatus>().Where(s => s != AppointmentStatus.Draft).Select(Formats.Status)];

    [ObservableProperty] private RangePreset _preset = RangePreset.Appointments[3];
    [ObservableProperty] private string _status = "All statuses";
    [ObservableProperty] private Lookup? _staffFilter;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageCount = 1;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private AppointmentListItem? _selected;
    [ObservableProperty] private bool _newestFirst;

    public bool CanCreate => session.Has(Permission.ManageAppointments);

    public override async Task LoadAsync()
    {
        if (StaffOptions.Count == 0)
        {
            StaffOptions.Add(new Lookup(0, "All staff"));
            foreach (var s in await staff.LookupAsync(null, state.SelectedLocationId)) StaffOptions.Add(s);
            StaffFilter = StaffOptions[0];
        }

        var (from, to) = Preset.Range(DateTime.Today);
        AppointmentStatus? status = Enum.GetValues<AppointmentStatus>().Cast<AppointmentStatus?>().FirstOrDefault(s => Formats.Status(s!.Value) == Status);
        var result = await appointments.QueryAsync(new AppointmentQuery
        {
            From = from, To = to, Status = status, StaffMemberId = StaffFilter is { Id: > 0 } sf ? sf.Id : null, LocationId = state.SelectedLocationId,
            Search = Search, Page = Page, PageSize = 50, Descending = NewestFirst,
        });
        Items.Clear();
        foreach (var i in result.Items) Items.Add(i);
        TotalCount = result.TotalCount;
        PageCount = result.PageCount;
    }

    private void Reload()
    {
        Page = 1;
        RequestReload();
    }

    partial void OnPresetChanged(RangePreset value) => Reload();
    partial void OnStatusChanged(string value) => Reload();
    partial void OnStaffFilterChanged(Lookup? value) => Reload();
    partial void OnNewestFirstChanged(bool value) => Reload();

    partial void OnSearchChanged(string value)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        Ui.Dispatch(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token);
                Reload();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    [RelayCommand]
    private Task NextPageAsync()
    {
        if (Page >= PageCount) return Task.CompletedTask;
        Page++;
        return ReloadAsync();
    }

    [RelayCommand]
    private Task PreviousPageAsync()
    {
        if (Page <= 1) return Task.CompletedTask;
        Page--;
        return ReloadAsync();
    }

    [RelayCommand] private Task OpenAsync(AppointmentListItem item) => workflows.OpenAppointmentAsync(item.Id);
    [RelayCommand] private Task NewAsync() => workflows.NewAppointmentAsync();

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = files.Save("Export appointments", "CSV file (*.csv)|*.csv", $"appointments-{DateTime.Today:yyyyMMdd}.csv");
        if (path is null) return;
        await RunAsync(async () =>
        {
            var (from, to) = Preset.Range(DateTime.Today);
            var all = await appointments.QueryAsync(new AppointmentQuery { From = from, To = to, Search = Search, LocationId = state.SelectedLocationId, PageSize = 500 });
            var rows = all.Items.Select(a => (IReadOnlyList<object?>)[a.Reference, a.Start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                a.End.ToString("HH:mm", CultureInfo.InvariantCulture), Formats.Status(a.Status), a.CustomerName, a.Services, a.Staff, a.Resources,
                Formats.Currency.ToEditString(a.TotalMinor), Formats.Currency.ToEditString(a.PaidMinor)]).ToList();
            await exporter.ExportCsvAsync(path, [new TabularReport("Appointments", ["Reference", "Start", "End", "Status", "Customer", "Services", "Staff", "Rooms", "Total", "Paid"], rows)]);
        }, $"Exported {Path.GetFileName(path)}");
    }
}

public sealed record HistoryRow(AppointmentEventDto Event, string Icon, string IconBrushKey, string IconBgKey);

public sealed partial class AppointmentDetailViewModel(
    AppointmentService appointments,
    SettingsService settings,
    UserSession session,
    Workflows workflows,
    DialogService dialogs,
    PrintService printer,
    NavigationService navigation) : PageViewModel
{
    private int _id;

    public override string Title => "Appointment";
    public override string NavKey => "Appointments";
    protected override DataArea RefreshOn => DataArea.Appointments | DataArea.Payments | DataArea.Customers | DataArea.Notifications;

    public ObservableCollection<HistoryRow> History { get; } = [];
    public ObservableCollection<SegmentDto> Segments { get; } = [];
    public ObservableCollection<PaymentDto> Payments { get; } = [];
    public ObservableCollection<ReminderDto> Reminders { get; } = [];

    [ObservableProperty] private AppointmentDetailDto? _detail;
    [ObservableProperty] private string _customerNotes = "";
    [ObservableProperty] private string _internalNotes = "";
    [ObservableProperty] private bool _notesDirty;
    [ObservableProperty] private bool _isTomorrow;
    [ObservableProperty] private string _relativeDay = "";
    [ObservableProperty] private string _staffSummary = "";
    [ObservableProperty] private string _roomSummary = "";

    public bool CanViewPayments => session.Has(Permission.ViewPayments);
    public bool CanEditCustomer => session.Has(Permission.ManageCustomers);

    public override Task OnNavigatedToAsync(object? parameter)
    {
        _id = parameter is int id ? id : 0;
        return base.OnNavigatedToAsync(parameter);
    }

    public override async Task LoadAsync()
    {
        var d = await appointments.GetDetailAsync(_id);
        Detail = d;
        Segments.Clear();
        foreach (var s in d.Segments) Segments.Add(s);
        Payments.Clear();
        foreach (var p in d.Payments) Payments.Add(p);
        Reminders.Clear();
        foreach (var r in d.Reminders) Reminders.Add(r);
        History.Clear();
        foreach (var e in d.History.OrderByDescending(e => e.Timestamp)) History.Add(ToRow(e));
        _customerNotesSilent = true;
        CustomerNotes = d.CustomerNotes ?? "";
        InternalNotes = d.InternalNotes ?? "";
        _customerNotesSilent = false;
        NotesDirty = false;
        var days = (d.Start.Date - DateTime.Today).Days;
        RelativeDay = days switch { 0 => "Today", 1 => "Tomorrow", -1 => "Yesterday", > 1 and < 7 => $"In {days} days", _ => "" };
        StaffSummary = string.Join(", ", d.Segments.Select(s => s.StaffName).Distinct());
        RoomSummary = string.Join(", ", d.Segments.Where(s => s.ResourceName is not null).Select(s => s.ResourceName).Distinct());
        if (string.IsNullOrEmpty(RoomSummary)) RoomSummary = "No room required";
    }

    private bool _customerNotesSilent;
    partial void OnCustomerNotesChanged(string value) { if (!_customerNotesSilent) NotesDirty = true; }
    partial void OnInternalNotesChanged(string value) { if (!_customerNotesSilent) NotesDirty = true; }

    private static HistoryRow ToRow(AppointmentEventDto e) => e.Type switch
    {
        AppointmentEventType.Created => new(e, "", "Brush.Success", "Brush.SuccessLight"),
        AppointmentEventType.PaymentRecorded => new(e, "", "Brush.Success", "Brush.SuccessLight"),
        AppointmentEventType.RefundRecorded or AppointmentEventType.PaymentReversed => new(e, "", "Brush.Warning", "Brush.WarningLight"),
        AppointmentEventType.ReminderSent => new(e, "", "Brush.Info", "Brush.InfoLight"),
        AppointmentEventType.ReminderNotSent or AppointmentEventType.ReminderFailed => new(e, "", "Brush.Warning", "Brush.WarningLight"),
        AppointmentEventType.ReminderScheduled => new(e, "", "Brush.Neutral", "Brush.NeutralLight"),
        AppointmentEventType.Rescheduled or AppointmentEventType.Reassigned => new(e, "", "Brush.Info", "Brush.InfoLight"),
        AppointmentEventType.StatusChanged when e.Title is "Cancelled" or "Marked No-Show" => new(e, "", "Brush.Danger", "Brush.DangerLight"),
        AppointmentEventType.StatusChanged => new(e, "", "Brush.Primary", "Brush.PrimaryLight"),
        AppointmentEventType.InvoiceIssued => new(e, "", "Brush.Primary", "Brush.PrimaryLight"),
        _ => new(e, "", "Brush.Neutral", "Brush.NeutralLight"),
    };

    private Task Status(AppointmentStatus target) =>
        Detail is { } d ? RunAsync(() => workflows.ChangeStatusAsync(d.Id, d.Version, target, d.Reference)) : Task.CompletedTask;

    [RelayCommand] private Task ConfirmAsync() => Status(AppointmentStatus.Confirmed);
    [RelayCommand] private Task CheckInAsync() => Status(AppointmentStatus.CheckedIn);
    [RelayCommand] private Task StartAsync() => Status(AppointmentStatus.InProgress);
    [RelayCommand] private Task CompleteAsync() => Status(AppointmentStatus.Completed);
    [RelayCommand] private Task CancelAppointmentAsync() => Status(AppointmentStatus.Cancelled);
    [RelayCommand] private Task NoShowAsync() => Status(AppointmentStatus.NoShow);

    [RelayCommand]
    private async Task CorrectStatusAsync()
    {
        if (Detail is not { } d || d.Actions.CorrectionTarget is not { } target) return;
        var reason = await dialogs.PromptAsync("Correct status",
            $"Change #{d.Reference} from {Formats.Status(d.Status)} back to {Formats.Status(target)}. This controlled correction is recorded in the history.",
            "Reason for the correction", true, "Apply correction");
        if (reason is null) return;
        await RunAsync(() => appointments.CorrectStatusAsync(d.Id, d.Version, reason), "Status corrected.");
    }

    [RelayCommand] private Task RescheduleAsync() => Detail is { } d ? workflows.RescheduleAsync(d.Id) : Task.CompletedTask;
    [RelayCommand] private Task EditServicesAsync() => Detail is { } d ? workflows.EditServicesAsync(d.Id) : Task.CompletedTask;
    [RelayCommand] private Task RecordPaymentAsync() => Detail is { } d ? workflows.RecordPaymentAsync(d.Id) : Task.CompletedTask;
    [RelayCommand] private Task ViewInvoiceAsync() => Detail is { } d ? RunAsync(() => workflows.ShowInvoiceAsync(d.Id)) : Task.CompletedTask;
    [RelayCommand] private Task ViewReceiptAsync(PaymentDto p) => RunAsync(() => workflows.ShowReceiptAsync(p.Id));
    [RelayCommand] private Task OpenCustomerAsync() => Detail is { } d ? workflows.OpenCustomerAsync(d.Customer.Id) : Task.CompletedTask;
    [RelayCommand] private Task EditCustomerAsync() => Detail is { } d ? workflows.EditCustomerAsync(d.Customer.Id) : Task.CompletedTask;
    [RelayCommand] private Task BackAsync() => navigation.CanGoBack ? navigation.GoBackAsync() : navigation.NavigateAsync<AppointmentsViewModel>();

    [RelayCommand]
    private async Task RefundAsync(PaymentDto p) => await OutflowAsync(p, reversal: false);

    [RelayCommand]
    private async Task ReverseAsync(PaymentDto p) => await OutflowAsync(p, reversal: true);

    private async Task OutflowAsync(PaymentDto p, bool reversal)
    {
        var vm = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<RefundViewModel>(Ui.Services, p, reversal);
        if (await dialogs.ShowAsync(vm) is string receipt) Ui.Toasts.Success($"{(reversal ? "Reversal" : "Refund")} recorded ({receipt}).");
    }

    [RelayCommand]
    private Task SaveNotesAsync() => Detail is { } d
        ? RunAsync(() => appointments.UpdateNotesAsync(d.Id, d.Version, CustomerNotes, InternalNotes), "Notes saved.")
        : Task.CompletedTask;

    [RelayCommand]
    private async Task EditRemindersAsync()
    {
        if (Detail is not { } d) return;
        var input = await dialogs.PromptAsync("Reminder preferences",
            "Enter channels and lead time, for example: \"email, sms, 24\". Messages are only sent when SMTP/SMS delivery is configured; otherwise they are recorded as Not Sent.",
            "Channels and hours before", true, "Save", $"{(d.RemindByEmail ? "email, " : "")}{(d.RemindBySms ? "sms, " : "")}{d.ReminderLeadHours}", multiline: false);
        if (input is null) return;
        var parts = input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var email = parts.Any(p => p.Equals("email", StringComparison.OrdinalIgnoreCase));
        var sms = parts.Any(p => p.Equals("sms", StringComparison.OrdinalIgnoreCase));
        var hours = parts.Select(p => int.TryParse(p, out var h) ? h : 0).FirstOrDefault(h => h > 0);
        await RunAsync(() => appointments.UpdateReminderPreferencesAsync(d.Id, d.Version, email, sms, hours == 0 ? d.ReminderLeadHours : hours), "Reminder preferences saved.");
    }

    [RelayCommand]
    private async Task AddFollowUpAsync()
    {
        if (Detail is not { } d) return;
        await dialogs.ShowAsync(new FollowUpViewModel(appointments, d.Id, d.Customer.FullName));
    }

    [RelayCommand]
    private void ContactCustomer()
    {
        if (Detail is not { } d) return;
        if (!string.IsNullOrWhiteSpace(d.Customer.Email) && !d.Customer.ContactHidden)
        {
            try
            {
                Process.Start(new ProcessStartInfo($"mailto:{d.Customer.Email}?subject={Uri.EscapeDataString($"Your appointment {d.Reference}")}") { UseShellExecute = true });
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No mail client registered: fall back to copying the contact details.
            }
        }

        if (d.Customer.ContactHidden)
        {
            Ui.Toasts.Warning("Your role cannot view customer contact details.");
            return;
        }

        Clipboard.SetText($"{d.Customer.FullName}\n{d.Customer.Phone}\n{d.Customer.Email}".Trim());
        Ui.Toasts.Info("No e-mail client is configured. Contact details were copied to the clipboard.");
    }

    [RelayCommand]
    private Task DuplicateAsync()
    {
        if (Detail is not { } d) return Task.CompletedTask;
        var next = d.Start.AddDays(7);
        return workflows.NewAppointmentAsync(new AppointmentPrefill(next, CustomerId: d.Customer.Id,
            Services: d.Segments.Select(s => new ServiceLineRequest(s.ServiceId, s.StaffMemberId, s.ResourceId)).ToList()));
    }

    [RelayCommand]
    private void Print()
    {
        if (Detail is { } d) printer.Print(DocumentBuilder.AppointmentSummary(d, settings.Business.Name), "Appointment " + d.Reference);
    }

    [RelayCommand]
    private void CopyReference()
    {
        if (Detail is { } d)
        {
            Clipboard.SetText(d.Reference);
            Ui.Toasts.Info("Reference copied.");
        }
    }
}
