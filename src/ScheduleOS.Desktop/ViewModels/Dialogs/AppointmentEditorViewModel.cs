using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Catalog;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Customers;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Application.Staff;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Desktop.ViewModels.Dialogs;

public sealed partial class ServiceLineViewModel(AppointmentEditorViewModel owner) : ObservableObject
{
    private bool _loading;

    public ObservableCollection<Lookup> StaffOptions { get; } = [];
    public ObservableCollection<Lookup> ResourceOptions { get; } = [];

    [ObservableProperty] private BookableService? _service;
    [ObservableProperty] private Lookup? _staff;
    [ObservableProperty] private Lookup? _resource;
    [ObservableProperty] private string _priceText = "";
    [ObservableProperty] private string _discountText = "";
    [ObservableProperty] private bool _needsResource;
    [ObservableProperty] private string _durationText = "";

    public int? PreferredStaffId { get; set; }
    public int? PreferredResourceId { get; set; }

    async partial void OnServiceChanged(BookableService? value)
    {
        if (value is null) return;
        _loading = true;
        try
        {
            PriceText = Formats.Currency.ToEditString(value.PriceMinor);
            DurationText = Formats.Duration(value.DurationMinutes) + (value.CleanupMinutes > 0 ? $" + {value.CleanupMinutes} min cleanup" : "");
            NeedsResource = value.RequiredResourceTypes.Count > 0;
            var staff = await owner.StaffLookup(value.Id);
            var resources = NeedsResource ? await owner.ResourceLookup(value.RequiredResourceTypes) : [];
            StaffOptions.Clear();
            foreach (var s in staff) StaffOptions.Add(s);
            ResourceOptions.Clear();
            foreach (var r in resources) ResourceOptions.Add(r);
            Staff = StaffOptions.FirstOrDefault(s => s.Id == PreferredStaffId) ?? StaffOptions.FirstOrDefault(s => s.Id == Staff?.Id) ?? StaffOptions.FirstOrDefault();
            Resource = NeedsResource ? ResourceOptions.FirstOrDefault(r => r.Id == PreferredResourceId) ?? ResourceOptions.FirstOrDefault() : null;
        }
        finally
        {
            _loading = false;
            owner.ScheduleCheck();
        }
    }

    partial void OnStaffChanged(Lookup? value) { if (!_loading) owner.ScheduleCheck(); }
    partial void OnResourceChanged(Lookup? value) { if (!_loading) owner.ScheduleCheck(); }
    partial void OnPriceTextChanged(string value) { if (!_loading) owner.ScheduleCheck(); }
    partial void OnDiscountTextChanged(string value) { if (!_loading) owner.ScheduleCheck(); }

    public ServiceLineRequest? ToRequest(out string? error)
    {
        error = null;
        if (Service is null || Staff is null)
        {
            error = "Choose a service and a staff member for every line.";
            return null;
        }

        long? price = null;
        if (!string.IsNullOrWhiteSpace(PriceText))
        {
            if (!Formats.Currency.TryParse(PriceText, out var p) || p < 0)
            {
                error = $"The price for {Service.Name} is not valid.";
                return null;
            }

            price = p;
        }

        long discount = 0;
        if (!string.IsNullOrWhiteSpace(DiscountText) && (!Formats.Currency.TryParse(DiscountText, out discount) || discount < 0 || discount > (price ?? Service.PriceMinor)))
        {
            error = $"The discount for {Service.Name} is not valid.";
            return null;
        }

        return new ServiceLineRequest(Service.Id, Staff.Id, Resource?.Id, price, discount);
    }

    [RelayCommand]
    private void Remove() => owner.RemoveLine(this);
}

/// <summary>New appointment (and "edit services") workflow with live conflict checking.</summary>
public sealed partial class AppointmentEditorViewModel(
    AppointmentService appointments,
    SchedulingService scheduling,
    CatalogService catalog,
    CustomerService customers,
    StaffService staff,
    ResourceService resources,
    SettingsService settings,
    ICurrentUser user,
    AppState state,
    Workflows workflows) : DialogViewModel
{
    private readonly Guid _submissionKey = Guid.NewGuid();
    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _searchCts;
    private int _expectedVersion;
    private bool _initializing = true;

    public AppointmentPrefill? Prefill { get; set; }
    public int? EditAppointmentId { get; set; }
    public bool IsCreate => EditAppointmentId is null;

    public override string Title => IsCreate ? "New Appointment" : "Edit Services";
    public override double DialogWidth => 1000;

    public ObservableCollection<BookableService> Services { get; } = [];
    public ObservableCollection<ServiceLineViewModel> Lines { get; } = [];
    public ObservableCollection<Lookup> CustomerResults { get; } = [];
    public ObservableCollection<TimeOnly> TimeSlots { get; } = [];
    public ObservableCollection<ConflictRow> ConflictRows { get; } = [];
    public ObservableCollection<SegmentPreview> Timeline { get; } = [];
    public IReadOnlyList<AppointmentStatus> Statuses { get; } = [AppointmentStatus.Pending, AppointmentStatus.Confirmed];
    public IReadOnlyList<BookingSource> Sources { get; } = Enum.GetValues<BookingSource>();

    [ObservableProperty] private string _customerSearch = "";
    [ObservableProperty] private Lookup? _customer;
    [ObservableProperty] private string _customerDisplay = "";
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private TimeOnly? _time;
    [ObservableProperty] private AppointmentStatus _status = AppointmentStatus.Pending;
    [ObservableProperty] private BookingSource _source = BookingSource.Phone;
    [ObservableProperty] private string _customerNotes = "";
    [ObservableProperty] private string _internalNotes = "";
    [ObservableProperty] private bool _remindByEmail;
    [ObservableProperty] private bool _remindBySms;
    [ObservableProperty] private int _reminderLeadHours = 24;
    [ObservableProperty] private string _summaryState = "Select a customer, services and a time";
    [ObservableProperty] private string _summaryIcon = "";
    [ObservableProperty] private bool _hasBlocking;
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private bool _isClear;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _durationText = "—";
    [ObservableProperty] private string _endText = "—";
    [ObservableProperty] private long _subtotal;
    [ObservableProperty] private long _discount;
    [ObservableProperty] private long _tax;
    [ObservableProperty] private long _total;
    [ObservableProperty] private long _deposit;
    [ObservableProperty] private string _formError = "";

    public bool CanEditInternalNotes => user.Has(Permission.EditInternalNotes);
    public bool CanEditSchedule => IsCreate;

    public override async Task InitializeAsync()
    {
        var s = settings.Current;
        Status = s.NewBookingsConfirmed ? AppointmentStatus.Confirmed : AppointmentStatus.Pending;
        RemindByEmail = s.DefaultRemindByEmail;
        RemindBySms = s.DefaultRemindBySms;
        ReminderLeadHours = s.ReminderLeadHours;
        for (var t = new TimeOnly(s.CalendarStartHour, 0); ; t = t.AddMinutes(s.SlotIntervalMinutes))
        {
            TimeSlots.Add(t);
            if (t.Hour * 60 + t.Minute + s.SlotIntervalMinutes >= s.CalendarEndHour * 60) break;
        }

        foreach (var svc in await catalog.GetBookableAsync()) Services.Add(svc);

        if (EditAppointmentId is { } id)
        {
            var d = await appointments.GetDetailAsync(id);
            _expectedVersion = d.Version;
            Customer = new Lookup(d.Customer.Id, d.Customer.FullName);
            CustomerDisplay = d.Customer.FullName;
            Date = d.Start.Date;
            Time = TimeOnly.FromDateTime(d.Start);
            Status = d.Status;
            foreach (var seg in d.Segments)
            {
                var svc = Services.FirstOrDefault(x => x.Id == seg.ServiceId);
                if (svc is null)
                {
                    // Archived service: keep it selectable for this appointment.
                    var (dto, _, _) = await catalog.GetAsync(seg.ServiceId);
                    svc = new BookableService(seg.ServiceId, seg.ServiceName, seg.CategoryName, seg.DurationMinutes, seg.PrepMinutes, seg.CleanupMinutes,
                        seg.UnitPriceMinor, 0, dto.RequiredResourceTypes);
                    Services.Add(svc);
                }

                var line = new ServiceLineViewModel(this) { PreferredStaffId = seg.StaffMemberId, PreferredResourceId = seg.ResourceId };
                Lines.Add(line);
                line.Service = svc;
                await Task.Delay(1);
                line.PriceText = Formats.Currency.ToEditString(seg.UnitPriceMinor);
                line.DiscountText = seg.DiscountMinor == 0 ? "" : Formats.Currency.ToEditString(seg.DiscountMinor);
            }
        }
        else
        {
            var p = Prefill;
            var start = p?.Start ?? NextSlot(DateTime.Now, s.SlotIntervalMinutes);
            Date = start.Date;
            Time = TimeSlots.FirstOrDefault(t => t == TimeOnly.FromDateTime(start)) is var match && match != default ? match : TimeOnly.FromDateTime(start);
            if (!TimeSlots.Contains(Time.Value)) TimeSlots.Add(Time.Value);
            if (p?.CustomerId is { } cid)
            {
                var c = await customers.GetAsync(cid);
                Customer = new Lookup(cid, c.Profile.FullName);
            }

            if (p?.Services is { Count: > 0 } lines)
            {
                foreach (var l in lines)
                {
                    var line = new ServiceLineViewModel(this) { PreferredStaffId = l.StaffMemberId, PreferredResourceId = l.ResourceId };
                    Lines.Add(line);
                    line.Service = Services.FirstOrDefault(x => x.Id == l.ServiceId);
                }
            }
            else
            {
                Lines.Add(new ServiceLineViewModel(this) { PreferredStaffId = p?.StaffId, PreferredResourceId = p?.ResourceId });
            }

            await SearchCustomersAsync("");
        }

        _initializing = false;
        ScheduleCheck();
    }

    internal Task<IReadOnlyList<Lookup>> StaffLookup(int serviceId) => staff.LookupAsync(serviceId, state.SelectedLocationId);

    internal Task<IReadOnlyList<Lookup>> ResourceLookup(IReadOnlyList<ResourceType> types) => resources.LookupAsync(types.ToList(), state.SelectedLocationId);

    private static DateTime NextSlot(DateTime now, int interval)
    {
        var minutes = (int)Math.Ceiling((now.Minute + 1) / (double)interval) * interval;
        return now.Date.AddHours(now.Hour).AddMinutes(minutes);
    }

    partial void OnCustomerSearchChanged(string value)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        Ui.Dispatch(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                await SearchCustomersAsync(value);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task SearchCustomersAsync(string term)
    {
        var results = await customers.LookupAsync(term, 25);
        CustomerResults.Clear();
        foreach (var r in results) CustomerResults.Add(r);
        if (Customer is not null && CustomerResults.All(c => c.Id != Customer.Id)) CustomerResults.Insert(0, Customer);
    }

    partial void OnCustomerChanged(Lookup? value)
    {
        CustomerDisplay = value?.Name ?? "";
        ScheduleCheck();
    }

    partial void OnDateChanged(DateTime? value) => ScheduleCheck();
    partial void OnTimeChanged(TimeOnly? value) => ScheduleCheck();

    [RelayCommand]
    private void AddLine()
    {
        var last = Lines.LastOrDefault();
        Lines.Add(new ServiceLineViewModel(this) { PreferredStaffId = last?.Staff?.Id });
        ScheduleCheck();
    }

    internal void RemoveLine(ServiceLineViewModel line)
    {
        if (Lines.Count <= 1) return;
        Lines.Remove(line);
        ScheduleCheck();
    }

    [RelayCommand]
    private async Task NewCustomerAsync()
    {
        var id = await workflows.NewCustomerAsync(openAfter: false);
        if (id is null) return;
        var c = await customers.GetAsync(id.Value);
        var lookup = new Lookup(id.Value, c.Profile.FullName, c.Profile.Phone ?? c.Profile.Email);
        CustomerResults.Insert(0, lookup);
        Customer = lookup;
    }

    [RelayCommand]
    private async Task FindTimeAsync()
    {
        var ids = Lines.Where(l => l.Service is not null).Select(l => l.Service!.Id).ToList();
        await workflows.FindTimeAsync(ids, (start, picked) =>
        {
            Date = start.Date;
            var t = TimeOnly.FromDateTime(start);
            if (!TimeSlots.Contains(t)) TimeSlots.Add(t);
            Time = t;
            for (var i = 0; i < picked.Count && i < Lines.Count; i++)
            {
                Lines[i].PreferredStaffId = picked[i].StaffMemberId;
                Lines[i].PreferredResourceId = picked[i].ResourceId;
                Lines[i].Staff = Lines[i].StaffOptions.FirstOrDefault(s => s.Id == picked[i].StaffMemberId) ?? Lines[i].Staff;
                Lines[i].Resource = Lines[i].ResourceOptions.FirstOrDefault(r => r.Id == picked[i].ResourceId) ?? Lines[i].Resource;
            }
        });
    }

    private DateTime? StartTime => Date is { } d && Time is { } t ? d.Date + t.ToTimeSpan() : null;

    private List<ServiceLineRequest>? BuildLines(out string? error)
    {
        var result = new List<ServiceLineRequest>();
        foreach (var l in Lines)
        {
            var req = l.ToRequest(out error);
            if (req is null) return null;
            result.Add(req);
        }

        error = null;
        return result;
    }

    internal void ScheduleCheck()
    {
        if (_initializing) return;
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();
        Ui.Dispatch(async () =>
        {
            try
            {
                await Task.Delay(350, cts.Token);
                await RunCheckAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });
        SaveCommand.NotifyCanExecuteChanged();
    }

    private async Task RunCheckAsync(CancellationToken ct)
    {
        var lines = BuildLines(out var error);
        FormError = error ?? "";
        if (lines is null || StartTime is not { } start)
        {
            ResetSummary(error ?? "Choose a date and time");
            return;
        }

        IsChecking = true;
        try
        {
            var result = await scheduling.CheckAsync(new ConflictCheckRequest(start, lines, state.SelectedLocationId, EditAppointmentId), ct);
            ct.ThrowIfCancellationRequested();
            ApplyResult(result);
        }
        catch (DomainException ex)
        {
            ResetSummary(ex.Message);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void ApplyResult(ConflictCheckResult r)
    {
        Timeline.Clear();
        foreach (var s in r.Segments) Timeline.Add(s);
        ConflictRows.Clear();
        foreach (var c in r.Conflicts.Where(c => !(EditAppointmentId is not null && c.Type == ConflictType.LeadTime)))
            ConflictRows.Add(new ConflictRow(c.Severity == ConflictSeverity.Blocking ? "" : "", c.Message,
                c.Severity == ConflictSeverity.Blocking ? "Blocking" : "Warning", c.Severity == ConflictSeverity.Blocking));
        HasBlocking = ConflictRows.Any(c => c.IsBlocking);
        HasWarnings = !HasBlocking && ConflictRows.Count > 0;
        IsClear = ConflictRows.Count == 0;
        SummaryIcon = HasBlocking ? "" : HasWarnings ? "" : "";
        SummaryState = HasBlocking ? "Not available — resolve the conflicts below" : HasWarnings ? "Available with warnings" : "Available — no conflicts";
        DurationText = Formats.Duration(r.TotalMinutes);
        EndText = r.End.ToString("h:mm tt", CultureInfo.InvariantCulture);
        Subtotal = r.SubtotalMinor;
        Discount = r.DiscountMinor;
        Tax = r.TaxMinor;
        Total = r.TotalMinor;
        Deposit = r.DepositMinor;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void ResetSummary(string message)
    {
        Timeline.Clear();
        ConflictRows.Clear();
        HasBlocking = HasWarnings = IsClear = false;
        SummaryIcon = "";
        SummaryState = message;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => !IsBusy && Customer is not null && StartTime is not null && Lines.Count > 0 && Lines.All(l => l.Service is not null && l.Staff is not null);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var lines = BuildLines(out var error);
        if (lines is null || Customer is null || StartTime is not { } start)
        {
            FormError = error ?? "Complete the form.";
            return;
        }

        object? result = null;
        var ok = await RunAsync(async () =>
        {
            var saved = await workflows.SaveScheduleAsync(async overrideWarnings =>
            {
                if (IsCreate)
                {
                    result = await appointments.CreateAsync(new CreateAppointmentRequest
                    {
                        SubmissionKey = _submissionKey,
                        CustomerId = Customer.Id,
                        LocationId = state.SelectedLocationId,
                        Start = start,
                        Services = lines,
                        InitialStatus = Status,
                        Source = Source,
                        CustomerNotes = CustomerNotes,
                        InternalNotes = InternalNotes,
                        RemindByEmail = RemindByEmail,
                        RemindBySms = RemindBySms,
                        ReminderLeadHours = ReminderLeadHours,
                        OverrideWarnings = overrideWarnings,
                    });
                }
                else
                {
                    await appointments.UpdateServicesAsync(new UpdateServicesRequest
                    {
                        AppointmentId = EditAppointmentId!.Value, ExpectedVersion = _expectedVersion, Services = lines, OverrideWarnings = overrideWarnings,
                    });
                    result = true;
                }
            });
            if (!saved) result = null;
        });
        if (ok && result is not null) Close(result);
    }
}
