using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Catalog;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Settings;
using ScheduleOS.Application.Staff;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Desktop.ViewModels.Dialogs;

public sealed partial class RescheduleSegmentViewModel(RescheduleViewModel owner, SegmentDto segment) : ObservableObject
{
    public SegmentDto Segment { get; } = segment;
    public ObservableCollection<Lookup> StaffOptions { get; } = [];
    public ObservableCollection<Lookup> ResourceOptions { get; } = [];
    public bool HasResource => Segment.ResourceId is not null;

    [ObservableProperty] private Lookup? _staff;
    [ObservableProperty] private Lookup? _resource;

    partial void OnStaffChanged(Lookup? value) => owner.ScheduleCheck();
    partial void OnResourceChanged(Lookup? value) => owner.ScheduleCheck();
}

/// <summary>Reschedule / reassign dialog with live validation; nothing is saved until the user confirms.</summary>
public sealed partial class RescheduleViewModel(
    AppointmentService appointments,
    SchedulingService scheduling,
    StaffService staff,
    ResourceService resources,
    CatalogService catalog,
    SettingsService settings,
    Workflows workflows) : DialogViewModel
{
    private AppointmentDetailDto? _detail;
    private CancellationTokenSource? _cts;
    private bool _ready;

    public int AppointmentId { get; set; }
    public override string Title => "Reschedule Appointment";
    public override double DialogWidth => 720;

    public ObservableCollection<RescheduleSegmentViewModel> Segments { get; } = [];
    public ObservableCollection<TimeOnly> TimeSlots { get; } = [];
    public ObservableCollection<ConflictRow> ConflictRows { get; } = [];

    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _currentText = "";
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private TimeOnly? _time;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _stateIcon = "";
    [ObservableProperty] private bool _hasBlocking;
    [ObservableProperty] private string _newRange = "";

    public override async Task InitializeAsync()
    {
        _detail = await appointments.GetDetailAsync(AppointmentId);
        if (!_detail.Actions.CanReschedule) throw new ValidationException($"A {Formats.Status(_detail.Status)} appointment cannot be rescheduled.");
        var s = settings.Current;
        for (var t = new TimeOnly(s.CalendarStartHour, 0); t.Hour < s.CalendarEndHour && TimeSlots.Count < 200; t = t.AddMinutes(s.SlotIntervalMinutes))
        {
            TimeSlots.Add(t);
            if (t.Hour * 60 + t.Minute + s.SlotIntervalMinutes >= s.CalendarEndHour * 60) break;
        }

        Reference = _detail.Reference;
        CurrentText = $"{_detail.Start:dddd, MMMM d, yyyy} · {_detail.Start:h:mm tt} – {_detail.End:h:mm tt}";
        Date = _detail.Start.Date;
        var t0 = TimeOnly.FromDateTime(_detail.Start);
        if (!TimeSlots.Contains(t0)) TimeSlots.Add(t0);
        Time = t0;
        foreach (var seg in _detail.Segments)
        {
            var vm = new RescheduleSegmentViewModel(this, seg);
            foreach (var l in await staff.LookupAsync(seg.ServiceId)) vm.StaffOptions.Add(l);
            if (vm.StaffOptions.All(o => o.Id != seg.StaffMemberId)) vm.StaffOptions.Insert(0, new Lookup(seg.StaffMemberId, seg.StaffName));
            if (seg.ResourceId is not null)
            {
                var (dto, _, _) = await catalog.GetAsync(seg.ServiceId);
                foreach (var r in await resources.LookupAsync(dto.RequiredResourceTypes)) vm.ResourceOptions.Add(r);
                if (vm.ResourceOptions.All(o => o.Id != seg.ResourceId)) vm.ResourceOptions.Insert(0, new Lookup(seg.ResourceId.Value, seg.ResourceName ?? "Room"));
            }

            vm.Staff = vm.StaffOptions.First(o => o.Id == seg.StaffMemberId);
            vm.Resource = vm.ResourceOptions.FirstOrDefault(o => o.Id == seg.ResourceId);
            Segments.Add(vm);
        }

        _ready = true;
        ScheduleCheck();
    }

    partial void OnDateChanged(DateTime? value) => ScheduleCheck();
    partial void OnTimeChanged(TimeOnly? value) => ScheduleCheck();

    private DateTime? NewStart => Date is { } d && Time is { } t ? d.Date + t.ToTimeSpan() : null;

    internal void ScheduleCheck()
    {
        if (!_ready) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Ui.Dispatch(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token);
                await CheckAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private IReadOnlyList<ServiceLineRequest> Lines() => Segments.Select(s => new ServiceLineRequest(s.Segment.ServiceId, s.Staff?.Id ?? s.Segment.StaffMemberId,
        s.Resource?.Id ?? s.Segment.ResourceId, s.Segment.UnitPriceMinor, s.Segment.DiscountMinor)).ToList();

    private async Task CheckAsync(CancellationToken ct)
    {
        if (NewStart is not { } start || _detail is null) return;
        var result = await scheduling.CheckAsync(new ConflictCheckRequest(start, Lines(), _detail.LocationId, _detail.Id), ct);
        ConflictRows.Clear();
        foreach (var c in result.Conflicts)
            ConflictRows.Add(new ConflictRow(c.Severity == ConflictSeverity.Blocking ? "" : "", c.Message,
                c.Severity == ConflictSeverity.Blocking ? "Blocking" : "Warning", c.Severity == ConflictSeverity.Blocking));
        HasBlocking = result.HasBlocking;
        StateIcon = result.HasBlocking ? "" : result.HasWarnings ? "" : "";
        State = result.HasBlocking ? "This time is not available" : result.HasWarnings ? "Available with warnings" : "This time is available";
        NewRange = $"{result.Start:ddd, MMM d} · {result.Start:h:mm tt} – {result.End:h:mm tt}";
    }

    [RelayCommand]
    private async Task FindTimeAsync()
    {
        if (_detail is null) return;
        await workflows.FindTimeAsync(_detail.Segments.Select(s => s.ServiceId).ToList(), (start, picked) =>
        {
            Date = start.Date;
            var t = TimeOnly.FromDateTime(start);
            if (!TimeSlots.Contains(t)) TimeSlots.Add(t);
            Time = t;
            for (var i = 0; i < picked.Count && i < Segments.Count; i++)
            {
                Segments[i].Staff = Segments[i].StaffOptions.FirstOrDefault(o => o.Id == picked[i].StaffMemberId) ?? Segments[i].Staff;
                Segments[i].Resource = Segments[i].ResourceOptions.FirstOrDefault(o => o.Id == picked[i].ResourceId) ?? Segments[i].Resource;
            }
        });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_detail is null || NewStart is not { } start) return;
        var staffMap = Segments.Where(s => s.Staff is not null && s.Staff.Id != s.Segment.StaffMemberId).ToDictionary(s => s.Segment.SortOrder, s => s.Staff!.Id);
        var resourceMap = Segments.Where(s => s.Resource is not null && s.Resource.Id != s.Segment.ResourceId).ToDictionary(s => s.Segment.SortOrder, s => (int?)s.Resource!.Id);
        var saved = false;
        await RunAsync(async () =>
        {
            saved = await workflows.SaveScheduleAsync(o => appointments.RescheduleAsync(new RescheduleRequest
            {
                AppointmentId = _detail.Id,
                ExpectedVersion = _detail.Version,
                NewStart = start,
                StaffBySegment = staffMap,
                ResourceBySegment = resourceMap,
                Reason = Reason,
                OverrideWarnings = o,
            }));
        });
        if (saved) Close(true);
    }
}

public sealed partial class SelectableService(BookableService service) : ObservableObject
{
    public BookableService Service { get; } = service;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>"Find Available Time": suggests conflict-free slots with compatible staff and rooms.</summary>
public sealed partial class FindTimeViewModel(SchedulingService scheduling, CatalogService catalog, StaffService staff, AppState state) : DialogViewModel
{
    public IReadOnlyList<int>? InitialServiceIds { get; set; }
    public override string Title => "Find Available Time";
    public override double DialogWidth => 820;

    public ObservableCollection<SelectableService> Services { get; } = [];
    public ObservableCollection<Lookup> StaffOptions { get; } = [];
    public ObservableCollection<SlotSuggestion> Results { get; } = [];

    [ObservableProperty] private DateTime? _date = DateTime.Today;
    [ObservableProperty] private Lookup? _preferredStaff;
    [ObservableProperty] private string _preferredTime = "";
    [ObservableProperty] private int _days = 7;
    [ObservableProperty] private SlotSuggestion? _selected;
    [ObservableProperty] private string _message = "Choose services and press Search.";

    public override async Task InitializeAsync()
    {
        foreach (var s in await catalog.GetBookableAsync())
            Services.Add(new SelectableService(s) { IsSelected = InitialServiceIds?.Contains(s.Id) == true });
        StaffOptions.Add(new Lookup(0, "Any staff member"));
        foreach (var s in await staff.LookupAsync(null, state.SelectedLocationId)) StaffOptions.Add(s);
        PreferredStaff = StaffOptions[0];
        if (InitialServiceIds is { Count: > 0 }) await SearchAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var ids = InitialServiceIds is { Count: > 0 } && Services.Count(s => s.IsSelected) == 0
            ? InitialServiceIds
            : Services.Where(s => s.IsSelected).Select(s => s.Service.Id).ToList();
        // Keep the requested order for multi-service bookings.
        if (InitialServiceIds is { Count: > 0 } initial) ids = initial.Where(ids.Contains).Concat(ids.Where(i => !initial.Contains(i))).ToList();
        if (ids.Count == 0)
        {
            Message = "Select at least one service.";
            return;
        }

        TimeOnly? time = TimeOnly.TryParse(PreferredTime, CultureInfo.InvariantCulture, out var t) ? t : null;
        await RunAsync(async () =>
        {
            var slots = await scheduling.FindSlotsAsync(new FindSlotsRequest(ids, DateOnly.FromDateTime(Date ?? DateTime.Today),
                PreferredStaff is { Id: > 0 } p ? p.Id : null, time, state.SelectedLocationId, Math.Clamp(Days, 1, 31), 24));
            Results.Clear();
            foreach (var s in slots) Results.Add(s);
            Message = Results.Count == 0 ? "No available time was found in this period. Try other dates, staff or services." : $"{Results.Count} available time(s) found.";
        });
    }

    [RelayCommand]
    private void Pick(SlotSuggestion? slot)
    {
        if ((slot ?? Selected) is { } s) Close(s);
    }
}

public sealed record ConflictReportRow(int AppointmentId, string Reference, string When, string Customer, IReadOnlyList<ConflictRow> Conflicts);

public sealed partial class ConflictReportViewModel(DateTime from, DateTime to, SchedulingService scheduling, Workflows workflows) : DialogViewModel
{
    public int? LocationId { get; set; }
    public override string Title => "Conflict Check";
    public override double DialogWidth => 760;
    public ObservableCollection<ConflictReportRow> Rows { get; } = [];
    [ObservableProperty] private string _summary = "";

    public override async Task InitializeAsync()
    {
        var items = await scheduling.ScanConflictsAsync(from, to, LocationId);
        foreach (var i in items)
            Rows.Add(new ConflictReportRow(i.AppointmentId, i.Reference, i.Start.ToString("ddd, MMM d · h:mm tt", CultureInfo.InvariantCulture), i.CustomerName,
                i.Conflicts.Select(c => new ConflictRow(c.Severity == ConflictSeverity.Blocking ? "" : "", c.Message,
                    c.Severity.ToString(), c.Severity == ConflictSeverity.Blocking)).ToList()));
        Summary = Rows.Count == 0
            ? $"No conflicts between {from:MMM d} and {to.AddDays(-1):MMM d}. Every booking respects staff hours, breaks, leave, rooms and maintenance."
            : $"{Rows.Count} appointment(s) between {from:MMM d} and {to.AddDays(-1):MMM d} need attention (for example after leave or maintenance was recorded).";
    }

    [RelayCommand]
    private async Task OpenAsync(ConflictReportRow row)
    {
        Close(null);
        await workflows.OpenAppointmentAsync(row.AppointmentId);
    }

    [RelayCommand]
    private async Task RescheduleAsync(ConflictReportRow row)
    {
        await workflows.RescheduleAsync(row.AppointmentId);
        Rows.Clear();
        await InitializeAsync();
    }
}
