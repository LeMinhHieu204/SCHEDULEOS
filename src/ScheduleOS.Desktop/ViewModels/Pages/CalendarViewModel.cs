using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Catalog;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Reporting;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Desktop.Controls;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Desktop.ViewModels.Pages;

public enum CalendarView { Day, Week, Month, List }

public enum CalendarGrouping { Staff, Room, Date }

public sealed record MiniDay(DateTime Date, string Day, bool InMonth, bool IsToday, bool IsSelected);

public sealed record MonthEntry(int AppointmentId, string Text, string Color);

public sealed record MonthCell(DateTime Date, string Day, bool InMonth, bool IsToday, int Count, IReadOnlyList<MonthEntry> Entries, string More);

public sealed record OverviewTile(string Value, string Caption, string Icon, string IconBrushKey, string IconBgKey, string? Delta, string DeltaBrushKey);

public sealed partial class CalendarViewModel(
    AppointmentService appointments,
    CatalogService catalog,
    ResourceService resources,
    DashboardService dashboard,
    SettingsService settings,
    UserSession session,
    AppState state,
    IClock clock,
    Workflows workflows,
    DialogService dialogs,
    ShellViewModel shell) : PageViewModel
{
    private CalendarData? _data;
    private bool _initialised;

    public override string Title => "Smart Calendar";
    protected override DataArea RefreshOn => DataArea.Appointments | DataArea.Staff | DataArea.Resources | DataArea.Services | DataArea.Settings;

    public ShellViewModel Shell { get; } = shell;
    public ObservableCollection<Lookup> StaffFilter { get; } = [];
    public ObservableCollection<Lookup> CategoryFilter { get; } = [];
    public ObservableCollection<MiniDay> MiniDays { get; } = [];
    public ObservableCollection<MonthCell> MonthCells { get; } = [];
    public ObservableCollection<AppointmentListItem> ListItems { get; } = [];
    public ObservableCollection<OverviewTile> Overview { get; } = [];
    public ObservableCollection<ResourceListItem> RoomStatus { get; } = [];

    [ObservableProperty] private CalendarView _view = CalendarView.Week;
    [ObservableProperty] private CalendarGrouping _grouping = CalendarGrouping.Staff;
    [ObservableProperty] private DateTime _date = DateTime.Today;
    [ObservableProperty] private DateTime _miniMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _miniMonthText = "";
    [ObservableProperty] private Lookup? _selectedStaff;
    [ObservableProperty] private Lookup? _selectedCategory;
    [ObservableProperty] private BoardModel? _board;
    [ObservableProperty] private bool _includeCancelled;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private string _emptyMessage = "";

    public bool IsBoardView => View is CalendarView.Day or CalendarView.Week;
    public bool IsMonthView => View == CalendarView.Month;
    public bool IsListView => View == CalendarView.List;
    public bool IsDayView => View == CalendarView.Day;
    public bool CanCreate => session.Has(Permission.ManageAppointments);
    public bool ShowSidebar => true;
    public Func<MoveRequest, Task<string?>> PreviewProvider => PreviewMoveAsync;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is DateTime d) Date = d.Date;
        if (!session.Has(Permission.ViewAllAppointments)) Grouping = CalendarGrouping.Date;
        await base.OnNavigatedToAsync(parameter);
    }

    public override async Task LoadAsync()
    {
        if (!_initialised)
        {
            StaffFilter.Clear();
            StaffFilter.Add(new Lookup(0, "All Staff", ""));
            foreach (var s in await Ui.Get<Application.Staff.StaffService>().LookupAsync(null, state.SelectedLocationId)) StaffFilter.Add(s);
            CategoryFilter.Clear();
            CategoryFilter.Add(new Lookup(0, "All Services"));
            foreach (var c in await catalog.GetCategoriesAsync()) CategoryFilter.Add(new Lookup(c.Id, c.Name));
            SelectedStaff = StaffFilter[0];
            SelectedCategory = CategoryFilter[0];
            _initialised = true;
        }

        BuildMiniCalendar();
        var (from, to) = Range();
        RangeText = View switch
        {
            CalendarView.Day => Date.ToString("dddd, MMM d, yyyy", CultureInfo.InvariantCulture),
            CalendarView.Month => Date.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            _ => $"{from:MMM d} – {to.AddDays(-1):MMM d, yyyy}",
        };

        int? staffId = SelectedStaff is { Id: > 0 } sf ? sf.Id : null;
        int? categoryId = SelectedCategory is { Id: > 0 } cf ? cf.Id : null;
        switch (View)
        {
            case CalendarView.List:
                var page = await appointments.QueryAsync(new AppointmentQuery
                {
                    From = from, To = to, StaffMemberId = staffId, LocationId = state.SelectedLocationId, PageSize = 500,
                });
                ListItems.Clear();
                foreach (var i in page.Items.Where(i => IncludeCancelled || i.Status is not (AppointmentStatus.Cancelled or AppointmentStatus.NoShow))) ListItems.Add(i);
                EmptyMessage = ListItems.Count == 0 ? "No appointments in this period." : "";
                break;
            default:
                _data = await appointments.GetCalendarAsync(new CalendarQuery(from, to, state.SelectedLocationId,
                    staffId is null ? null : [staffId.Value], categoryId, null, IncludeCancelled));
                if (View == CalendarView.Month) BuildMonth(from);
                else Board = BuildBoard(_data, from, to);
                EmptyMessage = _data.Segments.Count == 0 ? "No appointments in this period. Click an empty slot to create one." : "";
                break;
        }

        await LoadSidebarAsync();
    }

    private (DateTime From, DateTime To) Range()
    {
        switch (View)
        {
            case CalendarView.Day:
                return (Date.Date, Date.Date.AddDays(1));
            case CalendarView.Month:
                var first = new DateTime(Date.Year, Date.Month, 1);
                var gridStart = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
                return (gridStart, gridStart.AddDays(42));
            default:
                var monday = Date.Date.AddDays(-(((int)Date.DayOfWeek + 6) % 7));
                return (monday, monday.AddDays(7));
        }
    }

    private BoardModel BuildBoard(CalendarData data, DateTime from, DateTime to)
    {
        var s = settings.Current;
        var now = clock.Now;
        var canMove = session.Has(Permission.ManageAppointments);
        var columns = new List<BoardColumn>();
        var items = new List<BoardItem>();
        var blocks = new List<BoardBlock>();
        var grouping = View == CalendarView.Week ? CalendarGrouping.Date : Grouping;

        if (grouping == CalendarGrouping.Staff)
        {
            foreach (var st in data.Staff)
                columns.Add(new BoardColumn("s" + st.Id, st.Name, st.Subtitle, Domain.Entities.NameHelper.Initials(st.Name), st.Color, from, st.Id, null, from.Date == now.Date));
            var index = columns.Select((c, i) => (c.StaffId, i)).ToDictionary(x => x.StaffId!.Value, x => x.i);
            foreach (var seg in data.Segments.Where(x => index.ContainsKey(x.StaffMemberId)))
                items.Add(SegmentItem(seg, index[seg.StaffMemberId], canMove, seg.ResourceName));
            foreach (var b in data.Blocks)
            {
                if (b.StaffMemberId is { } sid && index.TryGetValue(sid, out var col)) blocks.Add(new BoardBlock(col, b.Start, b.End, b.Label, b.Kind));
                else if (b.StaffMemberId is null && b.ResourceId is null)
                    for (var i = 0; i < columns.Count; i++) blocks.Add(new BoardBlock(i, b.Start, b.End, b.Label, b.Kind));
            }
        }
        else if (grouping == CalendarGrouping.Room)
        {
            foreach (var r in data.Resources)
                columns.Add(new BoardColumn("r" + r.Id, r.Name, r.Subtitle, null, r.Color, from, null, r.Id, from.Date == now.Date));
            var index = columns.Select((c, i) => (c.ResourceId, i)).ToDictionary(x => x.ResourceId!.Value, x => x.i);
            foreach (var seg in data.Segments.Where(x => x.ResourceId is { } rid && index.ContainsKey(rid)))
                items.Add(SegmentItem(seg, index[seg.ResourceId!.Value], canMove, seg.StaffName));
            foreach (var b in data.Blocks)
            {
                if (b.ResourceId is { } rid && index.TryGetValue(rid, out var col)) blocks.Add(new BoardBlock(col, b.Start, b.End, b.Label, b.Kind));
                else if (b.StaffMemberId is null && b.ResourceId is null)
                    for (var i = 0; i < columns.Count; i++) blocks.Add(new BoardBlock(i, b.Start, b.End, b.Label, b.Kind));
            }
        }
        else
        {
            for (var d = from; d < to; d = d.AddDays(1))
                columns.Add(new BoardColumn("d" + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), d.ToString("ddd", CultureInfo.InvariantCulture),
                    d.ToString("MMM d", CultureInfo.InvariantCulture), null, null, d, null, null, d.Date == now.Date));
            foreach (var g in data.Segments.GroupBy(x => x.AppointmentId))
            {
                var segs = g.OrderBy(x => x.SortOrder).ToList();
                var first = segs[0];
                var col = (int)(first.AppointmentStart.Date - from.Date).TotalDays;
                if (col < 0 || col >= columns.Count) continue;
                var services = string.Join(", ", segs.Select(x => x.ServiceName));
                var staffNames = string.Join(", ", segs.Select(x => x.StaffName).Distinct());
                items.Add(new BoardItem
                {
                    AppointmentId = first.AppointmentId, Reference = first.Reference, Version = first.Version, Column = col,
                    Start = first.AppointmentStart, End = first.AppointmentEnd, AppointmentStart = first.AppointmentStart,
                    Title = first.CustomerName, Subtitle = services, Detail = staffNames, Status = first.Status,
                    AccentHex = first.CategoryColor ?? first.StaffColor, HasConflict = first.HasConflict,
                    CanMove = canMove && AppointmentLifecycle.CanReschedule(first.Status), CanResize = canMove && segs.Count == 1 && first.SegmentCount == 1 && AppointmentLifecycle.CanReschedule(first.Status),
                    Tooltip = Tooltip(first, services, staffNames, string.Join(", ", segs.Select(x => x.ResourceName).Where(n => n is not null).Distinct())),
                });
            }

            var singleStaff = SelectedStaff is { Id: > 0 } ss ? ss.Id : (int?)null;
            foreach (var b in data.Blocks.Where(b => (b.StaffMemberId is null && b.ResourceId is null) || (singleStaff is not null && b.StaffMemberId == singleStaff)))
            {
                var col = (int)(b.Start.Date - from.Date).TotalDays;
                if (col >= 0 && col < columns.Count) blocks.Add(new BoardBlock(col, b.Start, b.End, b.Label, b.Kind));
            }
        }

        return new BoardModel { Columns = columns, Items = items, Blocks = blocks, StartHour = s.CalendarStartHour, EndHour = s.CalendarEndHour, SlotMinutes = s.SlotIntervalMinutes, Now = now };
    }

    private static BoardItem SegmentItem(CalendarSegment seg, int column, bool canMove, string? detail) => new()
    {
        AppointmentId = seg.AppointmentId, Reference = seg.Reference, Version = seg.Version, SortOrder = seg.SortOrder, Column = column,
        Start = seg.Start, End = seg.End, AppointmentStart = seg.AppointmentStart, Title = seg.CustomerName, Subtitle = seg.ServiceName,
        Detail = detail, Status = seg.Status, AccentHex = seg.CategoryColor ?? seg.StaffColor, HasConflict = seg.HasConflict, IsSegment = true,
        CanMove = canMove && AppointmentLifecycle.CanReschedule(seg.Status),
        CanResize = canMove && seg.SegmentCount == 1 && AppointmentLifecycle.CanReschedule(seg.Status),
        Tooltip = Tooltip(seg, seg.ServiceName, seg.StaffName, seg.ResourceName),
    };

    private static string Tooltip(CalendarSegment s, string services, string staff, string? room) =>
        $"#{s.Reference} · {Formats.Status(s.Status)}\n{s.CustomerName}\n{services}\n{s.Start:h:mm tt} – {s.End:h:mm tt}\nStaff: {staff}" +
        (string.IsNullOrWhiteSpace(room) ? "" : $"\nRoom: {room}") + (s.HasConflict ? "\n⚠ Conflict detected" : "") +
        (AppointmentLifecycle.CanReschedule(s.Status) ? "\nDrag to reschedule · Ctrl+↑/↓ to nudge" : "");

    private void BuildMonth(DateTime gridStart)
    {
        MonthCells.Clear();
        var now = clock.Now.Date;
        var byDay = (_data?.Segments ?? []).GroupBy(x => x.AppointmentId).Select(g => g.OrderBy(x => x.SortOrder).First())
            .GroupBy(x => x.AppointmentStart.Date).ToDictionary(g => g.Key, g => g.OrderBy(x => x.AppointmentStart).ToList());
        for (var i = 0; i < 42; i++)
        {
            var d = gridStart.AddDays(i);
            var list = byDay.GetValueOrDefault(d) ?? [];
            var entries = list.Take(3).Select(x => new MonthEntry(x.AppointmentId, $"{x.AppointmentStart:h:mm} {x.CustomerName}", x.CategoryColor ?? x.StaffColor)).ToList();
            MonthCells.Add(new MonthCell(d, d.Day.ToString(CultureInfo.InvariantCulture), d.Month == Date.Month, d == now, list.Count, entries,
                list.Count > 3 ? $"+{list.Count - 3} more" : ""));
        }
    }

    private void BuildMiniCalendar()
    {
        MiniMonthText = MiniMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        MiniDays.Clear();
        var start = MiniMonth.AddDays(-(((int)MiniMonth.DayOfWeek + 6) % 7));
        for (var i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            MiniDays.Add(new MiniDay(d, d.Day.ToString(CultureInfo.InvariantCulture), d.Month == MiniMonth.Month, d == DateTime.Today, d == Date.Date));
        }
    }

    private async Task LoadSidebarAsync()
    {
        try
        {
            var d = await dashboard.GetAsync(DateOnly.FromDateTime(DateTime.Today), state.SelectedLocationId);
            Overview.Clear();
            Overview.Add(new OverviewTile(d.TodaysAppointments.Value.ToString(CultureInfo.InvariantCulture), "Total Appointments", "", "Brush.Info", "Brush.InfoLight",
                d.TodaysAppointments.ChangePercent is { } p ? $"{(p >= 0 ? "↑" : "↓")} {Math.Abs(p):0}%" : null, p0(d.TodaysAppointments.ChangePercent)));
            Overview.Add(new OverviewTile(d.Pending.Value.ToString(CultureInfo.InvariantCulture), "Pending Confirmations", "", "Brush.Warning", "Brush.WarningLight", null, "Brush.Success"));
            Overview.Add(new OverviewTile($"{d.AvailableStaff.Value} / {d.AvailableStaff.Total}", "Staff Scheduled", "", "Brush.Success", "Brush.SuccessLight",
                d.AvailableStaff.Total is > 0 ? $"{d.AvailableStaff.Value * 100.0 / d.AvailableStaff.Total.Value:0}%" : null, "Brush.Success"));
            ConflictCount = d.ConflictsDetected;
            RoomStatus.Clear();
            foreach (var r in await resources.ListAsync(false, state.SelectedLocationId)) RoomStatus.Add(r);
        }
        catch (PermissionDeniedException)
        {
            Overview.Clear();
        }

        static string p0(double? v) => v is null or >= 0 ? "Brush.Success" : "Brush.Danger";
    }

    partial void OnViewChanged(CalendarView value)
    {
        OnPropertyChanged(nameof(IsBoardView));
        OnPropertyChanged(nameof(IsMonthView));
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsDayView));
        RequestReload();
    }

    partial void OnGroupingChanged(CalendarGrouping value) => RequestReload();
    partial void OnSelectedStaffChanged(Lookup? value) => RequestReload();
    partial void OnSelectedCategoryChanged(Lookup? value) => RequestReload();
    partial void OnIncludeCancelledChanged(bool value) => RequestReload();

    partial void OnDateChanged(DateTime value)
    {
        MiniMonth = new DateTime(value.Year, value.Month, 1);
        RequestReload();
    }

    [RelayCommand]
    private void Previous() => Date = View switch
    {
        CalendarView.Day => Date.AddDays(-1),
        CalendarView.Month => Date.AddMonths(-1),
        _ => Date.AddDays(-7),
    };

    [RelayCommand]
    private void Next() => Date = View switch
    {
        CalendarView.Day => Date.AddDays(1),
        CalendarView.Month => Date.AddMonths(1),
        _ => Date.AddDays(7),
    };

    [RelayCommand] private void Today() => Date = DateTime.Today;

    [RelayCommand]
    private void MiniPrevious()
    {
        MiniMonth = MiniMonth.AddMonths(-1);
        BuildMiniCalendar();
    }

    [RelayCommand]
    private void MiniNext()
    {
        MiniMonth = MiniMonth.AddMonths(1);
        BuildMiniCalendar();
    }

    [RelayCommand]
    private void PickDay(MiniDay day) => Date = day.Date;

    [RelayCommand]
    private void OpenDay(MonthCell cell)
    {
        Date = cell.Date;
        View = CalendarView.Day;
    }

    [RelayCommand]
    private Task NewAppointmentAsync() => workflows.NewAppointmentAsync(new AppointmentPrefill(Date.Date == DateTime.Today ? null : Date.Date.AddHours(settings.Current.CalendarStartHour + 1)));

    [RelayCommand]
    private Task SlotAsync(SlotRequest slot)
    {
        if (!CanCreate) return Task.CompletedTask;
        return workflows.NewAppointmentAsync(new AppointmentPrefill(slot.Start, slot.Column.StaffId ?? (SelectedStaff is { Id: > 0 } s ? s.Id : null), slot.Column.ResourceId));
    }

    [RelayCommand]
    private Task OpenAsync(BoardItem item) => workflows.OpenAppointmentAsync(item.AppointmentId);

    [RelayCommand]
    private Task OpenEntryAsync(MonthEntry entry) => workflows.OpenAppointmentAsync(entry.AppointmentId);

    [RelayCommand]
    private Task OpenListItemAsync(AppointmentListItem item) => workflows.OpenAppointmentAsync(item.Id);

    private RescheduleRequest ToRequest(MoveRequest req, bool overrideWarnings = false)
    {
        Dictionary<int, int>? staffMap = null;
        Dictionary<int, int?>? resourceMap = null;
        if (req.Item.IsSegment && req.TargetColumn.StaffId is { } sid && Board?.Columns[req.Item.Column].StaffId != sid)
            staffMap = new Dictionary<int, int> { [req.Item.SortOrder] = sid };
        if (req.Item.IsSegment && req.TargetColumn.ResourceId is { } rid && Board?.Columns[req.Item.Column].ResourceId != rid)
            resourceMap = new Dictionary<int, int?> { [req.Item.SortOrder] = rid };
        return new RescheduleRequest
        {
            AppointmentId = req.Item.AppointmentId, ExpectedVersion = req.Item.Version, NewStart = req.NewStart,
            NewDurationMinutes = req.NewDurationMinutes, StaffBySegment = staffMap, ResourceBySegment = resourceMap,
            OverrideWarnings = overrideWarnings, Reason = "Moved in calendar",
        };
    }

    private async Task<string?> PreviewMoveAsync(MoveRequest req)
    {
        var conflicts = await appointments.PreviewRescheduleAsync(ToRequest(req));
        if (conflicts.Count == 0) return null;
        var first = conflicts.OrderByDescending(c => c.Severity).First();
        return (first.Severity == ConflictSeverity.Blocking ? "Blocked: " : "Warning: ") + first.Message;
    }

    [RelayCommand]
    private async Task MoveAsync(MoveRequest req)
    {
        var target = req.TargetColumn;
        var who = target.StaffId is not null && Board?.Columns[req.Item.Column].StaffId != target.StaffId ? $" with {target.Title}"
            : target.ResourceId is not null && Board?.Columns[req.Item.Column].ResourceId != target.ResourceId ? $" in {target.Title}" : "";
        var what = req.NewDurationMinutes is { } minutes
            ? $"Change the duration of #{req.Item.Reference} ({req.Item.Title}) to {Formats.Duration(minutes)}?"
            : $"Move #{req.Item.Reference} ({req.Item.Title}) to {req.NewStart:dddd, MMM d 'at' h:mm tt}{who}?";
        if (await dialogs.ConfirmAsync("Confirm schedule change", what + " The change is validated against staff hours, rooms, leave and maintenance before saving.", "Save change"))
        {
            var ok = await RunAsync(async () =>
            {
                if (await workflows.SaveScheduleAsync(o => appointments.RescheduleAsync(ToRequest(req, o)))) Ui.Toasts.Success($"#{req.Item.Reference} updated.");
            });
            if (!ok) Ui.Toasts.Info("The original time was kept.");
        }

        await ReloadAsync(); // always re-sync the board (restores the original position if not saved)
    }

    [RelayCommand]
    private async Task ActionAsync(ItemAction action)
    {
        var item = action.Item;
        switch (action.Action)
        {
            case "reschedule":
                await workflows.RescheduleAsync(item.AppointmentId);
                break;
            case "payment":
                await workflows.RecordPaymentAsync(item.AppointmentId);
                break;
            default:
                var target = action.Action switch
                {
                    "confirm" => AppointmentStatus.Confirmed,
                    "checkin" => AppointmentStatus.CheckedIn,
                    "start" => AppointmentStatus.InProgress,
                    "complete" => AppointmentStatus.Completed,
                    _ => AppointmentStatus.Cancelled,
                };
                await RunAsync(() => workflows.ChangeStatusAsync(item.AppointmentId, item.Version, target, item.Reference));
                break;
        }
    }

    [RelayCommand]
    private Task FindTimeAsync() => workflows.FindTimeAsync();

    [RelayCommand]
    private Task CheckConflictsAsync()
    {
        var (from, to) = Range();
        if (View == CalendarView.List) (from, to) = (DateTime.Today, DateTime.Today.AddDays(14));
        return workflows.CheckConflictsAsync(from < DateTime.Now ? DateTime.Now : from, to <= DateTime.Now ? DateTime.Today.AddDays(14) : to, state.SelectedLocationId);
    }
}
