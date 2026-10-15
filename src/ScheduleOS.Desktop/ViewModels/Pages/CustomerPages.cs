using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Customers;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Security;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.ViewModels.Pages;

public sealed partial class CustomersViewModel(CustomerService customers, Workflows workflows, UserSession session, IReportExporter exporter, FileDialogs files, DialogService dialogs) : PageViewModel
{
    private CancellationTokenSource? _cts;

    public override string Title => "Customers";
    protected override DataArea RefreshOn => DataArea.Customers | DataArea.Appointments | DataArea.Payments;

    public ObservableCollection<CustomerListItem> Items { get; } = [];
    public IReadOnlyList<string> Segments { get; } = ["All customers", .. CustomerSegmentation.All];
    public IReadOnlyList<CustomerSort> Sorts { get; } = Enum.GetValues<CustomerSort>();
    public ObservableCollection<string> Tags { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _segment = "All customers";
    [ObservableProperty] private string _tag = "All tags";
    [ObservableProperty] private CustomerSort _sort = CustomerSort.Name;
    [ObservableProperty] private bool _includeArchived;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageCount = 1;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private CustomerListItem? _selected;

    public bool CanManage => session.Has(Permission.ManageCustomers);
    public bool CanSeeContact => session.Has(Permission.ViewCustomerContact);

    public override async Task LoadAsync()
    {
        var tags = await customers.GetAllTagsAsync();
        if (Tags.Count != tags.Count + 1)
        {
            Tags.Clear();
            Tags.Add("All tags");
            foreach (var t in tags) Tags.Add(t);
        }

        var result = await customers.SearchAsync(new CustomerQuery
        {
            Search = Search, Segment = Segment == "All customers" ? null : Segment, Tag = Tag == "All tags" ? null : Tag, Sort = Sort,
            IncludeArchived = IncludeArchived, Page = Page, PageSize = 50,
        });
        Items.Clear();
        foreach (var c in result.Items) Items.Add(c);
        TotalCount = result.TotalCount;
        PageCount = result.PageCount;
    }

    private void Reload()
    {
        Page = 1;
        RequestReload();
    }

    partial void OnSegmentChanged(string value) => Reload();
    partial void OnTagChanged(string value) => Reload();
    partial void OnSortChanged(CustomerSort value) => Reload();
    partial void OnIncludeArchivedChanged(bool value) => Reload();

    partial void OnSearchChanged(string value)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _ = ReloadSearchAsync(cts.Token);
    }

    private async Task ReloadSearchAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            ct.ThrowIfCancellationRequested();
            Page = 1;
            // Await the page gate directly so a search typed while the first load is
            // in progress cannot be lost or leave the UI showing the unfiltered list.
            await ReloadAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public override void OnNavigatedFrom()
    {
        _cts?.Cancel();
        base.OnNavigatedFrom();
    }

    [RelayCommand] private Task NextPageAsync() { if (Page >= PageCount) return Task.CompletedTask; Page++; return ReloadAsync(); }
    [RelayCommand] private Task PreviousPageAsync() { if (Page <= 1) return Task.CompletedTask; Page--; return ReloadAsync(); }
    [RelayCommand] private Task OpenAsync(CustomerListItem c) => workflows.OpenCustomerAsync(c.Id);
    [RelayCommand] private Task NewAsync() => workflows.NewCustomerAsync(openAfter: true);

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = files.Save("Export customers", "CSV file (*.csv)|*.csv", $"customers-{DateTime.Today:yyyyMMdd}.csv");
        if (path is null) return;
        await RunAsync(async () =>
        {
            var all = await customers.SearchAsync(new CustomerQuery { Search = Search, IncludeArchived = IncludeArchived, PageSize = 500, Page = 1 });
            var rows = new List<IReadOnlyList<object?>>();
            for (var p = 1; p <= all.PageCount; p++)
            {
                var page = p == 1 ? all : await customers.SearchAsync(new CustomerQuery { Search = Search, IncludeArchived = IncludeArchived, PageSize = 500, Page = p });
                rows.AddRange(page.Items.Select(c => (IReadOnlyList<object?>)[c.FullName, c.Phone, c.Email, string.Join("; ", c.Tags), string.Join("; ", c.Segments),
                    c.CompletedVisits, c.LastVisit?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Formats.Currency.ToEditString(c.TotalPaidMinor)]));
            }

            await exporter.ExportCsvAsync(path, [new TabularReport("Customers", ["Name", "Phone", "Email", "Tags", "Segments", "Visits", "Last visit", "Total paid"], rows)]);
        }, "Customers exported.");
    }

    /// <summary>Imports customers from a CSV with the columns Name, Phone, Email, Notes, Tags. Possible duplicates are skipped.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = files.Open("Import customers (CSV: Name, Phone, Email, Notes, Tags)", "CSV file (*.csv)|*.csv");
        if (path is null) return;
        await RunAsync(async () =>
        {
            var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8);
            int added = 0, duplicates = 0, invalid = 0;
            foreach (var line in lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var cells = ParseCsvLine(line);
                var dto = new CustomerEditDto
                {
                    FullName = cells.ElementAtOrDefault(0) ?? "", Phone = cells.ElementAtOrDefault(1), Email = cells.ElementAtOrDefault(2),
                    Notes = cells.ElementAtOrDefault(3),
                    Tags = (cells.ElementAtOrDefault(4) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                };
                try
                {
                    await customers.CreateAsync(dto, ignoreDuplicates: false);
                    added++;
                }
                catch (DuplicateCustomerException)
                {
                    duplicates++;
                }
                catch (ValidationException)
                {
                    invalid++;
                }
            }

            await dialogs.AlertAsync("Import finished", $"{added} customer(s) imported. {duplicates} possible duplicate(s) skipped. {invalid} invalid row(s) skipped.");
        });
    }

    internal static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',')
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else sb.Append(ch);
        }

        result.Add(sb.ToString().Trim());
        return result;
    }
}

public sealed partial class CustomerDetailViewModel(CustomerService customers, Workflows workflows, UserSession session, DialogService dialogs, FileDialogs files,
    NavigationService navigation) : PageViewModel
{
    private int _id;

    public override string Title => "Customer";
    public override string NavKey => "Customers";
    protected override DataArea RefreshOn => DataArea.Customers | DataArea.Appointments | DataArea.Payments;

    public ObservableCollection<AppointmentListItem> Appointments { get; } = [];
    public ObservableCollection<PaymentDto> Payments { get; } = [];
    public ObservableCollection<InvoiceSummaryDto> Invoices { get; } = [];

    [ObservableProperty] private CustomerDetailDto? _detail;
    [ObservableProperty] private string _upcomingText = "";

    public bool CanManage => session.Has(Permission.ManageCustomers);
    public bool CanAnonymize => session.Has(Permission.AnonymizeCustomers);
    public bool CanExport => session.Has(Permission.ExportCustomerData);
    public bool CanBook => session.Has(Permission.ManageAppointments);
    public bool CanViewPayments => session.Has(Permission.ViewPayments);

    public override Task OnNavigatedToAsync(object? parameter)
    {
        _id = parameter is int id ? id : 0;
        return base.OnNavigatedToAsync(parameter);
    }

    public override async Task LoadAsync()
    {
        var d = await customers.GetAsync(_id);
        Detail = d;
        Appointments.Clear();
        foreach (var a in d.Appointments) Appointments.Add(a);
        Payments.Clear();
        foreach (var p in d.Payments) Payments.Add(p);
        Invoices.Clear();
        foreach (var i in d.Invoices) Invoices.Add(i);
        var next = d.Appointments.Where(a => a.Start > DateTime.Now && a.Status is AppointmentStatus.Pending or AppointmentStatus.Confirmed).MinBy(a => a.Start);
        UpcomingText = next is null ? "No upcoming appointments" : $"Next: {next.Start:ddd, MMM d · h:mm tt} ({next.Services})";
    }

    [RelayCommand] private Task BackAsync() => navigation.CanGoBack ? navigation.GoBackAsync() : navigation.NavigateAsync<CustomersViewModel>();
    [RelayCommand] private Task EditAsync() => workflows.EditCustomerAsync(_id);
    [RelayCommand] private Task BookAsync() => workflows.NewAppointmentAsync(new AppointmentPrefill(CustomerId: _id));
    [RelayCommand] private Task OpenAppointmentAsync(AppointmentListItem a) => workflows.OpenAppointmentAsync(a.Id);
    [RelayCommand] private Task OpenInvoiceAsync(InvoiceSummaryDto i) => RunAsync(() => workflows.ShowInvoiceByIdAsync(i.Id));
    [RelayCommand] private Task OpenReceiptAsync(PaymentDto p) => RunAsync(() => workflows.ShowReceiptAsync(p.Id));

    [RelayCommand]
    private async Task ToggleArchiveAsync()
    {
        if (Detail is not { } d) return;
        if (!d.IsArchived && !await dialogs.ConfirmAsync("Archive customer", $"Archive {d.Profile.FullName}? Archived customers are hidden from search and cannot be booked. History is kept.", "Archive"))
            return;
        await RunAsync(() => customers.SetArchivedAsync(_id, !d.IsArchived), d.IsArchived ? "Customer restored." : "Customer archived.");
    }

    [RelayCommand]
    private async Task AnonymizeAsync()
    {
        if (Detail is not { } d) return;
        if (!await dialogs.ConfirmAsync("Anonymise customer",
                $"Permanently remove the personal data of {d.Profile.FullName} (name, contact details, notes, tags)? Appointment, payment and invoice records required for accounting are kept. This cannot be undone.",
                "Anonymise", danger: true)) return;
        await RunAsync(() => customers.AnonymizeAsync(_id), "Customer anonymised.");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Detail is not { } d) return;
        if (!await dialogs.ConfirmAsync("Delete customer", $"Delete {d.Profile.FullName}? Only customers without any appointment or payment history can be deleted.", "Delete", danger: true)) return;
        if (await RunAsync(() => customers.DeleteAsync(_id), "Customer deleted.")) await navigation.NavigateAsync<CustomersViewModel>();
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (Detail is not { } d) return;
        var path = files.Save("Export personal data", "JSON file (*.json)|*.json", $"customer-{_id}-data.json");
        if (path is null) return;
        await RunAsync(async () => await File.WriteAllTextAsync(path, await customers.ExportPersonalDataJsonAsync(_id)), "Personal data exported.");
    }
}
