using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Catalog;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Staff;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Desktop.ViewModels.Dialogs;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.ViewModels.Pages;

public sealed partial class ShiftRow(DayOfWeek day) : ObservableObject
{
    public DayOfWeek Day { get; } = day;
    public string DayName => Day.ToString();
    [ObservableProperty] private bool _works;
    [ObservableProperty] private string _start = "09:00";
    [ObservableProperty] private string _end = "18:00";
    [ObservableProperty] private string _breakStart = "";
    [ObservableProperty] private string _breakEnd = "";
}

public sealed partial class StaffViewModel(StaffService staff, Workflows workflows, UserSession session, DialogService dialogs, AppState state) : PageViewModel
{
    public override string Title => "Staff";
    protected override DataArea RefreshOn => DataArea.Staff | DataArea.Appointments;

    public ObservableCollection<StaffListItem> Items { get; } = [];
    public ObservableCollection<ShiftRow> Shifts { get; } = [];
    public ObservableCollection<TimeOffDto> TimeOff { get; } = [];
    public ObservableCollection<StaffPerformanceDto> Performance { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _includeArchived;
    [ObservableProperty] private StaffListItem? _selected;
    [ObservableProperty] private StaffDetailDto? _detail;
    [ObservableProperty] private int _tab;

    public bool CanManage => session.Has(Permission.ManageStaff);

    public override async Task LoadAsync()
    {
        var keep = Selected?.Id;
        var list = await staff.ListAsync(Search, IncludeArchived, state.SelectedLocationId);
        Items.Clear();
        foreach (var s in list) Items.Add(s);
        Performance.Clear();
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        foreach (var p in await staff.GetPerformanceAsync(monthStart, monthStart.AddMonths(1))) Performance.Add(p);
        Selected = Items.FirstOrDefault(i => i.Id == keep) ?? Items.FirstOrDefault();
        await LoadDetailAsync();
    }

    partial void OnSearchChanged(string value) => RequestReload();
    partial void OnIncludeArchivedChanged(bool value) => RequestReload();

    partial void OnSelectedChanged(StaffListItem? value)
    {
        if (IsLoaded) _ = TryAsync(LoadDetailAsync);
    }

    private async Task LoadDetailAsync()
    {
        if (Selected is null)
        {
            Detail = null;
            return;
        }

        var d = await staff.GetAsync(Selected.Id);
        Detail = d;
        Shifts.Clear();
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var sh = d.Shifts.FirstOrDefault(s => s.Day == day);
            Shifts.Add(new ShiftRow(day)
            {
                Works = sh is not null,
                Start = sh?.Start.ToString("HH:mm") ?? "09:00",
                End = sh?.End.ToString("HH:mm") ?? "18:00",
                BreakStart = sh?.BreakStart?.ToString("HH:mm") ?? "",
                BreakEnd = sh?.BreakEnd?.ToString("HH:mm") ?? "",
            });
        }

        TimeOff.Clear();
        foreach (var t in d.TimeOff) TimeOff.Add(t);
    }

    [RelayCommand] private Task NewAsync() => workflows.NewStaffAsync();
    [RelayCommand] private Task EditAsync() => Selected is { } s ? workflows.EditStaffAsync(s.Id) : Task.CompletedTask;

    [RelayCommand]
    private async Task ToggleArchiveAsync()
    {
        if (Selected is not { } s) return;
        if (!s.IsArchived && !await dialogs.ConfirmAsync("Archive staff member", $"Archive {s.FullName}? They will no longer be bookable. History is kept.", "Archive")) return;
        await RunAsync(() => staff.SetArchivedAsync(s.Id, !s.IsArchived), s.IsArchived ? "Staff member restored." : "Staff member archived.");
    }

    [RelayCommand]
    private async Task SaveScheduleAsync()
    {
        if (Selected is not { } s) return;
        var shifts = new List<ShiftDto>();
        foreach (var row in Shifts.Where(r => r.Works))
        {
            if (!TimeOnly.TryParse(row.Start, CultureInfo.InvariantCulture, out var st) || !TimeOnly.TryParse(row.End, CultureInfo.InvariantCulture, out var en))
            {
                Ui.Toasts.Warning($"{row.DayName}: enter start and end as HH:mm.");
                return;
            }

            TimeOnly? bs = TimeOnly.TryParse(row.BreakStart, CultureInfo.InvariantCulture, out var b1) ? b1 : null;
            TimeOnly? be = TimeOnly.TryParse(row.BreakEnd, CultureInfo.InvariantCulture, out var b2) ? b2 : null;
            shifts.Add(new ShiftDto(row.Day, st, en, bs, be));
        }

        IReadOnlyList<AffectedAppointment> affected = [];
        if (await RunAsync(async () => affected = await staff.SaveShiftsAsync(s.Id, shifts), "Working schedule saved.") && affected.Count > 0)
            await dialogs.AlertAsync("Bookings outside the new schedule",
                $"{affected.Count} upcoming appointment(s) now fall outside {s.FullName}'s working hours: " +
                string.Join(", ", affected.Take(12).Select(a => $"{a.Reference} ({a.Start:MMM d h:mm tt})")) + ". They are flagged in the calendar; reschedule them as needed.");
    }

    [RelayCommand]
    private async Task AddTimeOffAsync()
    {
        if (Selected is not { } s) return;
        var vm = new DateRangeDialogViewModel("Record time off", $"Block {s.FullName} from new bookings for leave, sickness, training or holidays.", "Save time off",
            showReason: false, allDayDefault: true, Enum.GetNames<TimeOffType>());
        if (await dialogs.ShowAsync(vm) is not DateRangeDialogViewModel.Result r) return;
        IReadOnlyList<AffectedAppointment> affected = [];
        if (await RunAsync(async () => affected = await staff.AddTimeOffAsync(s.Id, r.Start, r.End, Enum.Parse<TimeOffType>(r.Kind), r.Reason), "Time off recorded.") && affected.Count > 0)
            await dialogs.AlertAsync("Existing bookings affected",
                $"{affected.Count} appointment(s) overlap this time off and must be rescheduled: " + string.Join(", ", affected.Select(a => $"{a.Reference} ({a.CustomerName})")));
    }

    [RelayCommand]
    private async Task RemoveTimeOffAsync(TimeOffDto t)
    {
        if (await dialogs.ConfirmAsync("Remove time off", $"Remove {t.Type} {t.Start:MMM d} – {t.End:MMM d}?", "Remove"))
            await RunAsync(() => staff.RemoveTimeOffAsync(t.Id), "Time off removed.");
    }
}

public sealed partial class ServicesViewModel(CatalogService catalog, Workflows workflows, UserSession session, DialogService dialogs) : PageViewModel
{
    public override string Title => "Services";
    protected override DataArea RefreshOn => DataArea.Services | DataArea.Staff;

    public ObservableCollection<ServiceListItem> Items { get; } = [];
    public ObservableCollection<CategoryDto> Categories { get; } = [];
    public ObservableCollection<CategoryDto> CategoryFilter { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private CategoryDto? _category;
    [ObservableProperty] private bool _includeArchived;
    [ObservableProperty] private ServiceListItem? _selected;

    public bool CanManage => session.Has(Permission.ManageServices);

    public override async Task LoadAsync()
    {
        var cats = await catalog.GetCategoriesAsync();
        Categories.Clear();
        foreach (var c in cats) Categories.Add(c);
        var keep = Category?.Id ?? 0;
        CategoryFilter.Clear();
        CategoryFilter.Add(new CategoryDto(0, "All categories", "#008C95", cats.Sum(c => c.ServiceCount)));
        foreach (var c in cats) CategoryFilter.Add(c);
        _suppress = true;
        Category = CategoryFilter.FirstOrDefault(c => c.Id == keep) ?? CategoryFilter[0];
        _suppress = false;
        var list = await catalog.ListAsync(Search, Category is { Id: > 0 } cf ? cf.Id : null, IncludeArchived);
        Items.Clear();
        foreach (var s in list) Items.Add(s);
    }

    partial void OnSearchChanged(string value) => RequestReload();
    private bool _suppress;
    partial void OnCategoryChanged(CategoryDto? value) { if (!_suppress) RequestReload(); }
    partial void OnIncludeArchivedChanged(bool value) => RequestReload();

    [RelayCommand] private Task NewAsync() => workflows.NewServiceAsync();
    [RelayCommand] private Task EditAsync(ServiceListItem s) => workflows.EditServiceAsync(s.Id);

    [RelayCommand]
    private async Task ToggleArchiveAsync(ServiceListItem s)
    {
        if (!s.IsArchived && !await dialogs.ConfirmAsync("Archive service",
                $"Archive {s.Name}? It can no longer be booked. Past and upcoming appointments keep their agreed name and price.", "Archive")) return;
        await RunAsync(() => catalog.SetArchivedAsync(s.Id, !s.IsArchived), s.IsArchived ? "Service restored." : "Service archived.");
    }

    [RelayCommand]
    private async Task NewCategoryAsync()
    {
        var name = await dialogs.PromptAsync("New category", "Service categories group your catalog (Hair, Nails, Massage…).", "Category name", true, "Create", multiline: false);
        if (name is not null) await RunAsync(() => catalog.SaveCategoryAsync(null, name, StaffService.Palette[Categories.Count % StaffService.Palette.Length]), "Category created.");
    }

    [RelayCommand]
    private async Task RenameCategoryAsync(CategoryDto c)
    {
        var name = await dialogs.PromptAsync("Rename category", "", "Category name", true, "Save", c.Name, multiline: false);
        if (name is not null) await RunAsync(() => catalog.SaveCategoryAsync(c.Id, name, c.ColorHex), "Category renamed.");
    }

    [RelayCommand]
    private async Task DeleteCategoryAsync(CategoryDto c)
    {
        if (await dialogs.ConfirmAsync("Delete category", $"Delete the empty category {c.Name}?", "Delete", danger: true))
            await RunAsync(() => catalog.DeleteCategoryAsync(c.Id), "Category deleted.");
    }
}

public sealed partial class ResourcesViewModel(ResourceService resources, Workflows workflows, UserSession session, DialogService dialogs, AppState state) : PageViewModel
{
    public override string Title => "Rooms & Resources";
    protected override DataArea RefreshOn => DataArea.Resources | DataArea.Appointments;

    public ObservableCollection<ResourceListItem> Items { get; } = [];
    public ObservableCollection<MaintenanceDto> Maintenance { get; } = [];

    [ObservableProperty] private bool _includeArchived;
    [ObservableProperty] private ResourceListItem? _selected;

    public bool CanManage => session.Has(Permission.ManageResources);
    public int AvailableCount => Items.Count(i => i.State == ResourceState.Available);
    public int InUseCount => Items.Count(i => i.State == ResourceState.InUse);
    public int UnavailableCount => Items.Count(i => i.State is ResourceState.Maintenance or ResourceState.Blocked);

    public override async Task LoadAsync()
    {
        Items.Clear();
        foreach (var r in await resources.ListAsync(IncludeArchived, state.SelectedLocationId)) Items.Add(r);
        Maintenance.Clear();
        foreach (var m in await resources.GetMaintenanceAsync()) Maintenance.Add(m);
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(InUseCount));
        OnPropertyChanged(nameof(UnavailableCount));
    }

    partial void OnIncludeArchivedChanged(bool value) => RequestReload();

    [RelayCommand] private Task NewAsync() => workflows.NewResourceAsync();
    [RelayCommand] private Task EditAsync(ResourceListItem r) => workflows.EditResourceAsync(r.Id);

    [RelayCommand]
    private async Task ScheduleMaintenanceAsync(ResourceListItem r)
    {
        var vm = new DateRangeDialogViewModel($"Block {r.Name}", "New bookings cannot use this resource during the period. Existing bookings that overlap are listed after saving.",
            "Block resource", showReason: true, allDayDefault: false) { Reason = "Equipment maintenance" };
        if (await dialogs.ShowAsync(vm) is not DateRangeDialogViewModel.Result res) return;
        IReadOnlyList<AffectedAppointment> affected = [];
        if (await RunAsync(async () => affected = await resources.AddMaintenanceAsync(r.Id, res.Start, res.End, res.Reason), "Maintenance period saved.") && affected.Count > 0)
            await dialogs.AlertAsync("Existing bookings affected",
                $"{affected.Count} appointment(s) use {r.Name} during this period and must be moved: " + string.Join(", ", affected.Select(a => $"{a.Reference} ({a.Start:MMM d h:mm tt})")));
    }

    [RelayCommand]
    private async Task RemoveMaintenanceAsync(MaintenanceDto m)
    {
        if (await dialogs.ConfirmAsync("Remove block", $"Make {m.ResourceName} available again for {m.Start:g} – {m.End:g}?", "Remove"))
            await RunAsync(() => resources.RemoveMaintenanceAsync(m.Id), "Block removed.");
    }

    [RelayCommand]
    private async Task ToggleArchiveAsync(ResourceListItem r)
    {
        if (!r.IsArchived && !await dialogs.ConfirmAsync("Archive resource", $"Archive {r.Name}?", "Archive")) return;
        await RunAsync(() => resources.SetArchivedAsync(r.Id, !r.IsArchived), r.IsArchived ? "Resource restored." : "Resource archived.");
    }
}
