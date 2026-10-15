using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Notifications;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Reporting;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Staff;
using ScheduleOS.Desktop.Controls;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Desktop.ViewModels.Dialogs;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.ViewModels.Pages;

public sealed partial class PaymentsViewModel(PaymentService payments, Workflows workflows, UserSession session, DialogService dialogs) : PageViewModel
{
    private CancellationTokenSource? _cts;

    public override string Title => "Payments";
    protected override DataArea RefreshOn => DataArea.Payments | DataArea.Appointments;

    public IReadOnlyList<RangePreset> Presets => RangePreset.Reports;
    public ObservableCollection<PaymentDto> Items { get; } = [];
    public ObservableCollection<OutstandingItem> Outstanding { get; } = [];
    public IReadOnlyList<string> MethodOptions { get; } = ["All methods", .. Enum.GetValues<PaymentMethod>().Select(m => PaymentService.MethodName(m))];
    public IReadOnlyList<string> KindOptions { get; } = ["All types", .. Enum.GetNames<PaymentKind>()];

    [ObservableProperty] private RangePreset _preset = RangePreset.Reports[4];
    [ObservableProperty] private string _method = "All methods";
    [ObservableProperty] private string _kind = "All types";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private PaymentTotals? _totals;
    [ObservableProperty] private long _outstandingTotal;
    [ObservableProperty] private int _tab;
    [ObservableProperty] private PaymentDto? _selected;

    public bool CanRecord => session.Has(Permission.RecordPayments);
    public bool CanRefund => session.Has(Permission.RefundPayments);
    public bool CanReverse => session.Has(Permission.ReversePayments);

    public override async Task LoadAsync()
    {
        var (from, to) = Preset.Range(DateTime.Today);
        PaymentMethod? method = Enum.GetValues<PaymentMethod>().Cast<PaymentMethod?>().FirstOrDefault(m => PaymentService.MethodName(m!.Value) == Method);
        PaymentKind? kind = Enum.TryParse<PaymentKind>(Kind, out var k) ? k : null;
        var (page, totals) = await payments.ListAsync(new PaymentQuery { From = from, To = to, Method = method, Kind = kind, Search = Search, PageSize = 500 });
        Items.Clear();
        foreach (var p in page.Items) Items.Add(p);
        Totals = totals;
        Outstanding.Clear();
        foreach (var o in await payments.GetOutstandingAsync()) Outstanding.Add(o);
        OutstandingTotal = Outstanding.Sum(o => o.BalanceMinor);
    }

    partial void OnPresetChanged(RangePreset value) => RequestReload();
    partial void OnMethodChanged(string value) => RequestReload();
    partial void OnKindChanged(string value) => RequestReload();

    partial void OnSearchChanged(string value)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Ui.Dispatch(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token);
                await ReloadAsync();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    [RelayCommand] private Task RecordAsync() => workflows.RecordPaymentAsync(null);
    [RelayCommand] private Task CollectAsync(OutstandingItem o) => workflows.RecordPaymentAsync(o.AppointmentId);
    [RelayCommand] private Task OpenAppointmentAsync(OutstandingItem o) => workflows.OpenAppointmentAsync(o.AppointmentId);
    [RelayCommand] private Task ReceiptAsync(PaymentDto p) => RunAsync(() => workflows.ShowReceiptAsync(p.Id));

    [RelayCommand]
    private Task OpenPaymentAppointmentAsync(PaymentDto p) => p.AppointmentId is { } id ? workflows.OpenAppointmentAsync(id) : Task.CompletedTask;

    [RelayCommand]
    private async Task RefundAsync(PaymentDto p) => await OutflowAsync(p, false);

    [RelayCommand]
    private async Task ReverseAsync(PaymentDto p) => await OutflowAsync(p, true);

    private async Task OutflowAsync(PaymentDto p, bool reversal)
    {
        if (!p.Kind.Equals(PaymentKind.Payment) && !p.Kind.Equals(PaymentKind.Deposit) || p.RefundableMinor <= 0)
        {
            Ui.Toasts.Info("This entry has nothing left to refund or reverse.");
            return;
        }

        var vm = ActivatorUtilities.CreateInstance<RefundViewModel>(Ui.Services, p, reversal);
        if (await dialogs.ShowAsync(vm) is string receipt) Ui.Toasts.Success($"{(reversal ? "Reversal" : "Refund")} recorded ({receipt}).");
    }
}

public sealed partial class AnalyticsViewModel(AnalyticsService analytics, AppState state, IReportExporter exporter, FileDialogs files, PrintService printer) : PageViewModel
{
    private AnalyticsReport? _report;

    public override string Title => "Analytics & Reports";
    protected override DataArea RefreshOn => DataArea.Payments | DataArea.Appointments;

    public IReadOnlyList<RangePreset> Presets => RangePreset.Reports;
    public ObservableCollection<ServicePerformance> Services { get; } = [];
    public ObservableCollection<StaffPerformanceDto> Staff { get; } = [];
    public ObservableCollection<ResourceUtilization> Resources { get; } = [];
    public ObservableCollection<OutstandingItem> Outstanding { get; } = [];
    public ObservableCollection<ChartSlice> StatusLegend { get; } = [];

    [ObservableProperty] private RangePreset _preset = RangePreset.Reports[4];
    [ObservableProperty] private DateTime? _customFrom = DateTime.Today.AddDays(-29);
    [ObservableProperty] private DateTime? _customTo = DateTime.Today;
    [ObservableProperty] private bool _isCustom;
    [ObservableProperty] private IReadOnlyList<ChartPoint> _revenueChart = [];
    [ObservableProperty] private IReadOnlyList<ChartPoint> _volumeChart = [];
    [ObservableProperty] private IReadOnlyList<ChartSlice> _statusSlices = [];
    [ObservableProperty] private IReadOnlyList<ChartSlice> _customerSlices = [];
    [ObservableProperty] private string _periodText = "";
    [ObservableProperty] private int _tab;

    public AnalyticsReport? Report => _report;

    public override async Task LoadAsync()
    {
        var (from, to) = Range();
        PeriodText = $"{from:MMM d, yyyy} – {to.AddDays(-1):MMM d, yyyy}";
        var r = await analytics.GetReportAsync(from, to, state.SelectedLocationId);
        _report = r;
        OnPropertyChanged(nameof(Report));
        var maxRevenue = r.RevenueByDay.Count == 0 ? 0 : r.RevenueByDay.Max(d => d.AmountMinor);
        var fmt = r.RevenueByDay.Count > 14 ? "M/d" : "ddd d";
        RevenueChart = r.RevenueByDay.Select(d => new ChartPoint(d.Date.ToString(fmt, CultureInfo.InvariantCulture), d.AmountMinor,
            $"{d.Date:MMM d}: {Formats.Money(d.AmountMinor)}", d.AmountMinor == maxRevenue && maxRevenue > 0)).ToList();
        VolumeChart = r.VolumeByDay.Select(d => new ChartPoint(d.Date.ToString(fmt, CultureInfo.InvariantCulture), d.Appointments,
            $"{d.Date:MMM d}: {d.Appointments} appointments ({d.Completed} completed)")).ToList();
        var total = r.StatusBreakdown.Sum(s => s.Count);
        StatusSlices = r.StatusBreakdown.Select(s => new ChartSlice(Formats.Status(s.Status), s.Count, Palette.StatusColor(s.Status),
            total == 0 ? "0%" : $"{s.Count} · {s.Count * 100.0 / total:0}%")).ToList();
        StatusLegend.Clear();
        foreach (var s in StatusSlices) StatusLegend.Add(s);
        CustomerSlices =
        [
            new ChartSlice("New", r.NewCustomers, Palette.Parse("#00B8BD"), r.NewCustomers.ToString(CultureInfo.InvariantCulture)),
            new ChartSlice("Returning", r.ReturningCustomers, Palette.Parse("#102532"), r.ReturningCustomers.ToString(CultureInfo.InvariantCulture)),
        ];
        Services.Clear();
        foreach (var s in r.Services) Services.Add(s);
        Staff.Clear();
        foreach (var s in r.Staff) Staff.Add(s);
        Resources.Clear();
        foreach (var x in r.Resources) Resources.Add(x);
        Outstanding.Clear();
        foreach (var o in r.Outstanding) Outstanding.Add(o);
    }

    private (DateTime From, DateTime To) Range()
    {
        if (IsCustom && CustomFrom is { } f && CustomTo is { } t && t >= f) return (f.Date, t.Date.AddDays(1));
        return Preset.Range(DateTime.Today);
    }

    partial void OnPresetChanged(RangePreset value)
    {
        IsCustom = value.Name == "Custom Range";
        RequestReload();
    }

    partial void OnCustomFromChanged(DateTime? value) { if (IsCustom) RequestReload(); }
    partial void OnCustomToChanged(DateTime? value) { if (IsCustom) RequestReload(); }

    private async Task ExportAsync(string kind)
    {
        if (_report is null) return;
        var (filter, ext) = kind switch
        {
            "xlsx" => ("Excel workbook (*.xlsx)|*.xlsx", "xlsx"),
            "pdf" => ("PDF document (*.pdf)|*.pdf", "pdf"),
            _ => ("CSV file (*.csv)|*.csv", "csv"),
        };
        var path = files.Save("Export report", filter, $"scheduleos-report-{_report.From:yyyyMMdd}-{_report.To.AddDays(-1):yyyyMMdd}.{ext}");
        if (path is null) return;
        var tables = analytics.ToTables(_report);
        var ok = await RunAsync(async () =>
        {
            switch (kind)
            {
                case "xlsx": await exporter.ExportXlsxAsync(path, tables); break;
                case "pdf": await exporter.ExportPdfAsync(path, "ScheduleOS Business Report", PeriodText, tables); break;
                default: await exporter.ExportCsvAsync(path, tables); break;
            }
        }, "Report exported.");
        if (ok && kind != "csv") Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand] private Task ExportCsvAsync() => ExportAsync("csv");
    [RelayCommand] private Task ExportXlsxAsync() => ExportAsync("xlsx");
    [RelayCommand] private Task ExportPdfAsync() => ExportAsync("pdf");

    [RelayCommand]
    private void Print()
    {
        if (_report is null) return;
        var doc = new System.Windows.Documents.FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 11 };
        doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("ScheduleOS Business Report")) { FontSize = 20, FontWeight = System.Windows.FontWeights.SemiBold });
        doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(PeriodText)));
        foreach (var t in analytics.ToTables(_report))
        {
            doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(t.Title)) { FontSize = 14, FontWeight = System.Windows.FontWeights.SemiBold, Margin = new System.Windows.Thickness(0, 12, 0, 4) });
            var table = new System.Windows.Documents.Table();
            foreach (var _ in t.Columns) table.Columns.Add(new System.Windows.Documents.TableColumn());
            var g = new System.Windows.Documents.TableRowGroup();
            var head = new System.Windows.Documents.TableRow();
            foreach (var c in t.Columns) head.Cells.Add(new System.Windows.Documents.TableCell(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(c)) { FontWeight = System.Windows.FontWeights.SemiBold }));
            g.Rows.Add(head);
            foreach (var row in t.Rows)
            {
                var tr = new System.Windows.Documents.TableRow();
                foreach (var v in row) tr.Cells.Add(new System.Windows.Documents.TableCell(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""))));
                g.Rows.Add(tr);
            }

            table.RowGroups.Add(g);
            doc.Blocks.Add(table);
        }

        printer.Print(doc, "ScheduleOS report");
    }
}

public sealed partial class NotificationsViewModel(NotificationService notifications, ReminderService reminders, Workflows workflows, UserSession session, DialogService dialogs) : PageViewModel
{
    public override string Title => "Notifications";
    public override string NavKey => "Notifications";
    protected override DataArea RefreshOn => DataArea.Notifications | DataArea.Appointments;

    public ObservableCollection<NotificationDto> Items { get; } = [];
    public ObservableCollection<ReminderDto> Queue { get; } = [];
    public IReadOnlyList<string> StatusOptions { get; } = ["All", .. Enum.GetNames<ReminderStatus>()];

    [ObservableProperty] private int _tab;
    [ObservableProperty] private string _queueStatus = "All";

    public bool CanSeeQueue => session.Has(Permission.ViewAllAppointments);

    public override async Task LoadAsync()
    {
        Items.Clear();
        foreach (var n in await notifications.ListAsync()) Items.Add(n);
        if (CanSeeQueue)
        {
            Queue.Clear();
            ReminderStatus? status = Enum.TryParse<ReminderStatus>(QueueStatus, out var s) ? s : null;
            foreach (var r in await reminders.ListAsync(status, null, null)) Queue.Add(r);
        }
    }

    partial void OnQueueStatusChanged(string value) => RequestReload();

    [RelayCommand] private Task MarkAllReadAsync() => RunAsync(() => notifications.MarkAllReadAsync());
    [RelayCommand] private Task DismissAsync(NotificationDto n) => RunAsync(() => notifications.DismissAsync(n.Id));

    [RelayCommand]
    private async Task OpenAsync(NotificationDto n)
    {
        await notifications.MarkReadAsync(n.Id);
        if (n.AppointmentId is { } id) await workflows.OpenAppointmentAsync(id);
    }

    [RelayCommand]
    private async Task PreviewAsync(ReminderDto r)
    {
        var (subject, body, recipient) = await reminders.PreviewAsync(r.Id);
        await dialogs.AlertAsync(subject, $"To: {recipient ?? "(in-app)"}\nStatus: {r.Status}{(r.LastError is null ? "" : $" — {r.LastError}")}\n\n{body}");
    }

    [RelayCommand] private Task RetryAsync(ReminderDto r) => RunAsync(() => reminders.RetryAsync(r.Id), "Message queued again.");
    [RelayCommand] private Task CancelReminderAsync(ReminderDto r) => RunAsync(() => reminders.CancelAsync(r.Id), "Reminder cancelled.");
    [RelayCommand] private Task ProcessNowAsync() => RunAsync(async () => Ui.Toasts.Info($"{await reminders.ProcessDueAsync()} due reminder(s) processed."));

    [RelayCommand]
    private Task OpenReminderAppointmentAsync(ReminderDto r) => r.AppointmentId is { } id ? workflows.OpenAppointmentAsync(id) : Task.CompletedTask;
}

public sealed partial class SearchViewModel(SearchService search, Workflows workflows, NavigationService navigation) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<SearchResult> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private SearchResult? _selected;
    [ObservableProperty] private string _status = "";

    partial void OnQueryChanged(string value)
    {
        _cts?.Cancel();
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
        {
            Results.Clear();
            IsOpen = false;
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        Ui.Dispatch(async () =>
        {
            try
            {
                await Task.Delay(220, cts.Token);
                var results = await search.SearchAsync(value, cts.Token);
                if (cts.IsCancellationRequested) return;
                Results.Clear();
                foreach (var r in results) Results.Add(r);
                Status = Results.Count == 0 ? "No matches" : $"{Results.Count} results";
                IsOpen = true;
                Selected = Results.FirstOrDefault();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await Ui.Errors.HandleAsync(ex);
            }
        });
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }

    [RelayCommand]
    public async Task OpenAsync(SearchResult? result)
    {
        result ??= Selected;
        if (result is null) return;
        IsOpen = false;
        Query = "";
        switch (result.Kind)
        {
            case SearchKind.Customer: await workflows.OpenCustomerAsync(result.Id); break;
            case SearchKind.Appointment or SearchKind.Payment when result.Id > 0: await workflows.OpenAppointmentAsync(result.Id); break;
            case SearchKind.Staff: await navigation.NavigateAsync<StaffViewModel>(); break;
            case SearchKind.Service: await workflows.EditServiceAsync(result.Id); break;
            case SearchKind.Invoice: await workflows.ShowInvoiceByIdAsync(result.Id); break;
        }
    }

    public static string IconFor(SearchKind kind) => kind switch
    {
        SearchKind.Customer => "",
        SearchKind.Appointment => "",
        SearchKind.Staff => "",
        SearchKind.Service => "",
        SearchKind.Invoice => "",
        _ => "",
    };
}
