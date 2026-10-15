using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Catalog;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Customers;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Staff;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Desktop.ViewModels.Dialogs;

public sealed partial class CheckOption<T>(T value, string label, bool isChecked = false) : ObservableObject
{
    public T Value { get; } = value;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isChecked = isChecked;
}

public sealed partial class CustomerEditorViewModel(CustomerService customers, StaffService staff) : DialogViewModel
{
    private int _version;

    public int? CustomerId { get; set; }
    public override string Title => CustomerId is null ? "New Customer" : "Edit Customer";
    public override double DialogWidth => 620;
    public ObservableCollection<Lookup> StaffOptions { get; } = [];

    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private DateTime? _birthday;
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private Lookup? _preferredStaff;
    [ObservableProperty] private bool _allowEmail = true;
    [ObservableProperty] private bool _allowSms = true;
    [ObservableProperty] private string _duplicateWarning = "";

    public override async Task InitializeAsync()
    {
        StaffOptions.Add(new Lookup(0, "No preference"));
        foreach (var s in await staff.LookupAsync()) StaffOptions.Add(s);
        PreferredStaff = StaffOptions[0];
        if (CustomerId is not { } id) return;
        var d = await customers.GetAsync(id);
        _version = d.Version;
        var p = d.Profile;
        FullName = p.FullName;
        Phone = p.Phone ?? "";
        Email = p.Email ?? "";
        Birthday = p.Birthday?.ToDateTime(TimeOnly.MinValue);
        Address = p.Address ?? "";
        Notes = p.Notes ?? "";
        Tags = string.Join(", ", p.Tags);
        PreferredStaff = StaffOptions.FirstOrDefault(s => s.Id == p.PreferredStaffId) ?? StaffOptions[0];
        AllowEmail = p.AllowEmail;
        AllowSms = p.AllowSms;
    }

    private CustomerEditDto Dto() => new()
    {
        FullName = FullName, Phone = Phone, Email = Email, Birthday = Birthday is { } b ? DateOnly.FromDateTime(b) : null, Address = Address, Notes = Notes,
        Tags = Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        PreferredStaffId = PreferredStaff is { Id: > 0 } s ? s.Id : null, AllowEmail = AllowEmail, AllowSms = AllowSms,
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        object? result = null;
        var ok = await RunAsync(async () =>
        {
            try
            {
                result = await SaveCoreAsync(ignoreDuplicates: false);
            }
            catch (DuplicateCustomerException dup)
            {
                DuplicateWarning = "Possible duplicate: " + string.Join(", ", dup.Matches.Select(m => $"{m.Name} ({m.Detail})"));
                if (await Ui.Dialogs.ConfirmAsync("Possible duplicate customer",
                        DuplicateWarning + ". Profiles are never merged automatically. Save this customer anyway?", "Save anyway"))
                    result = await SaveCoreAsync(ignoreDuplicates: true);
            }
        });
        if (ok && result is not null) Close(result);
    }

    private async Task<object> SaveCoreAsync(bool ignoreDuplicates)
    {
        if (CustomerId is { } id)
        {
            await customers.UpdateAsync(id, _version, Dto(), ignoreDuplicates);
            return id;
        }

        return await customers.CreateAsync(Dto(), ignoreDuplicates);
    }
}

public sealed partial class StaffEditorViewModel(StaffService staff, CatalogService catalog, LocationService locations) : DialogViewModel
{
    private int _version;

    public int? StaffId { get; set; }
    public override string Title => StaffId is null ? "New Staff Member" : "Edit Staff Member";
    public override double DialogWidth => 720;
    public ObservableCollection<CheckOption<int>> ServiceOptions { get; } = [];
    public ObservableCollection<LocationOption> LocationOptions { get; } = [];
    public IReadOnlyList<EmploymentStatus> Statuses { get; } = Enum.GetValues<EmploymentStatus>();
    public IReadOnlyList<string> Colors { get; } = StaffService.Palette;

    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _position = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _skillsText = "";
    [ObservableProperty] private EmploymentStatus _status = EmploymentStatus.Active;
    [ObservableProperty] private LocationOption? _location;
    [ObservableProperty] private string _color = StaffService.Palette[0];

    public override async Task InitializeAsync()
    {
        LocationOptions.Add(new LocationOption(null, "Any location"));
        foreach (var l in await locations.ListAsync()) LocationOptions.Add(new LocationOption(l.Id, l.Name));
        Location = LocationOptions[0];
        var selected = new HashSet<int>();
        if (StaffId is { } id)
        {
            var d = await staff.GetAsync(id);
            _version = d.Version;
            var p = d.Profile;
            FullName = p.FullName;
            Position = p.Position;
            Phone = p.Phone ?? "";
            Email = p.Email ?? "";
            SkillsText = p.SkillsText ?? "";
            Status = p.EmploymentStatus;
            Location = LocationOptions.FirstOrDefault(l => l.Id == p.LocationId) ?? LocationOptions[0];
            Color = p.ColorHex;
            selected = p.ServiceIds.ToHashSet();
        }
        else Color = StaffService.Palette[Random.Shared.Next(StaffService.Palette.Length)];

        foreach (var s in await catalog.ListAsync()) ServiceOptions.Add(new CheckOption<int>(s.Id, $"{s.Name}  ·  {s.CategoryName}", selected.Contains(s.Id)));
    }

    [RelayCommand]
    private void PickColor(string color) => Color = color;

    [RelayCommand]
    private async Task SaveAsync()
    {
        var dto = new StaffEditDto
        {
            FullName = FullName, Position = Position, Phone = Phone, Email = Email, SkillsText = SkillsText, EmploymentStatus = Status,
            LocationId = Location?.Id, ColorHex = Color, ServiceIds = ServiceOptions.Where(o => o.IsChecked).Select(o => o.Value).ToList(),
        };
        object? result = null;
        var ok = await RunAsync(async () =>
        {
            if (StaffId is { } id)
            {
                await staff.UpdateAsync(id, _version, dto);
                result = id;
            }
            else result = await staff.CreateAsync(dto);
        });
        if (ok) Close(result);
    }
}

public sealed partial class ServiceEditorViewModel(CatalogService catalog, StaffService staff) : DialogViewModel
{
    private int _version;

    public int? ServiceId { get; set; }
    public override string Title => ServiceId is null ? "New Service" : "Edit Service";
    public override double DialogWidth => 720;
    public ObservableCollection<CategoryDto> Categories { get; } = [];
    public ObservableCollection<CheckOption<int>> StaffOptions { get; } = [];
    public ObservableCollection<CheckOption<ResourceType>> ResourceOptions { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private CategoryDto? _category;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private int _duration = 60;
    [ObservableProperty] private int _prep;
    [ObservableProperty] private int _cleanup = 10;
    [ObservableProperty] private string _price = "";
    [ObservableProperty] private string _deposit = "";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private bool _isArchived;

    public override async Task InitializeAsync()
    {
        foreach (var c in await catalog.GetCategoriesAsync()) Categories.Add(c);
        var dto = new ServiceEditDto();
        if (ServiceId is { } id)
        {
            (dto, _version, IsArchived) = await catalog.GetAsync(id);
            Name = dto.Name;
            Description = dto.Description ?? "";
            Duration = dto.DurationMinutes;
            Prep = dto.PrepMinutes;
            Cleanup = dto.CleanupMinutes;
            Price = Formats.Currency.ToEditString(dto.PriceMinor);
            Deposit = dto.DepositMinor == 0 ? "" : Formats.Currency.ToEditString(dto.DepositMinor);
            IsActive = dto.IsActive;
        }

        Category = Categories.FirstOrDefault(c => c.Id == dto.CategoryId) ?? Categories.FirstOrDefault();
        foreach (var t in Enum.GetValues<ResourceType>())
            ResourceOptions.Add(new CheckOption<ResourceType>(t, ResourceTypeNames.Display(t), dto.RequiredResourceTypes.Contains(t)));
        foreach (var s in await staff.LookupAsync()) StaffOptions.Add(new CheckOption<int>(s.Id, $"{s.Name}  ·  {s.Detail}", dto.StaffIds.Contains(s.Id)));
    }

    [RelayCommand]
    private async Task NewCategoryAsync()
    {
        var name = await Ui.Dialogs.PromptAsync("New category", "Create a service category (for example Hair, Nails, Massage).", "Category name", true, "Create", multiline: false);
        if (name is null) return;
        await RunAsync(async () =>
        {
            var id = await catalog.SaveCategoryAsync(null, name, StaffService.Palette[Categories.Count % StaffService.Palette.Length]);
            Categories.Clear();
            foreach (var c in await catalog.GetCategoriesAsync()) Categories.Add(c);
            Category = Categories.FirstOrDefault(c => c.Id == id);
        });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!Formats.Currency.TryParse(Price, out var price))
        {
            Ui.Toasts.Warning("Enter a valid price.");
            return;
        }

        long deposit = 0;
        if (!string.IsNullOrWhiteSpace(Deposit) && !Formats.Currency.TryParse(Deposit, out deposit))
        {
            Ui.Toasts.Warning("Enter a valid deposit.");
            return;
        }

        var dto = new ServiceEditDto
        {
            Name = Name, CategoryId = Category?.Id, Description = Description, DurationMinutes = Duration, PrepMinutes = Prep, CleanupMinutes = Cleanup,
            PriceMinor = price, DepositMinor = deposit, IsActive = IsActive,
            RequiredResourceTypes = ResourceOptions.Where(o => o.IsChecked).Select(o => o.Value).ToList(),
            StaffIds = StaffOptions.Where(o => o.IsChecked).Select(o => o.Value).ToList(),
        };
        object? result = null;
        var ok = await RunAsync(async () =>
        {
            if (ServiceId is { } id)
            {
                await catalog.UpdateAsync(id, _version, dto);
                result = id;
            }
            else result = await catalog.CreateAsync(dto);
        });
        if (ok)
        {
            // Complete the catalog refresh before exposing the page to UI automation or the user.
            await Ui.Navigation.ReloadAsync();
            Close(result);
        }
    }
}

public sealed partial class ResourceEditorViewModel(ResourceService resources, LocationService locations) : DialogViewModel
{
    private int _version;

    public int? ResourceId { get; set; }
    public override string Title => ResourceId is null ? "New Room / Resource" : "Edit Room / Resource";
    public override double DialogWidth => 560;
    public IReadOnlyList<ResourceType> Types { get; } = Enum.GetValues<ResourceType>();
    public ObservableCollection<LocationOption> LocationOptions { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ResourceType _type = ResourceType.TreatmentRoom;
    [ObservableProperty] private LocationOption? _location;
    [ObservableProperty] private int _capacity = 1;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _availableFrom = "";
    [ObservableProperty] private string _availableUntil = "";

    public override async Task InitializeAsync()
    {
        LocationOptions.Add(new LocationOption(null, "Any location"));
        foreach (var l in await locations.ListAsync()) LocationOptions.Add(new LocationOption(l.Id, l.Name));
        Location = LocationOptions.Count > 1 ? LocationOptions[1] : LocationOptions[0];
        if (ResourceId is not { } id) return;
        var (dto, version) = await resources.GetAsync(id);
        _version = version;
        Name = dto.Name;
        Type = dto.Type;
        Location = LocationOptions.FirstOrDefault(l => l.Id == dto.LocationId) ?? LocationOptions[0];
        Capacity = dto.Capacity;
        IsActive = dto.IsActive;
        Notes = dto.Notes ?? "";
        AvailableFrom = dto.AvailableFrom?.ToString("HH:mm") ?? "";
        AvailableUntil = dto.AvailableUntil?.ToString("HH:mm") ?? "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        TimeOnly? from = TimeOnly.TryParse(AvailableFrom, out var f) ? f : null;
        TimeOnly? until = TimeOnly.TryParse(AvailableUntil, out var u) ? u : null;
        var dto = new ResourceEditDto
        {
            Name = Name, Type = Type, LocationId = Location?.Id, Capacity = Capacity, IsActive = IsActive, Notes = Notes, AvailableFrom = from, AvailableUntil = until,
        };
        object? result = null;
        var ok = await RunAsync(async () =>
        {
            if (ResourceId is { } id)
            {
                var affected = await resources.UpdateAsync(id, _version, dto);
                if (affected.Count > 0)
                    await Ui.Dialogs.AlertAsync("Bookings affected", $"{affected.Count} upcoming booking(s) use this resource and must be moved: " +
                                                                     string.Join(", ", affected.Select(a => a.Reference)));
                result = id;
            }
            else result = await resources.CreateAsync(dto);
        });
        if (ok) Close(result);
    }
}

public sealed partial class ChangePasswordDialogViewModel(AuthService auth) : DialogViewModel
{
    public override string Title => "Change Password";
    public override double DialogWidth => 440;
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _new = "";
    [ObservableProperty] private string _confirm = "";

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (New != Confirm)
        {
            Ui.Toasts.Warning("The new passwords do not match.");
            return;
        }

        if (await RunAsync(() => auth.ChangePasswordAsync(Current, New), "Password changed.")) Close(true);
    }
}

public sealed partial class UserEditorViewModel(AuthService auth, StaffService staff) : DialogViewModel
{
    public UserDto? Existing { get; set; }
    public override string Title => Existing is null ? "New User Account" : "Edit User Account";
    public override double DialogWidth => 520;
    public IReadOnlyList<UserRole> Roles { get; } = Enum.GetValues<UserRole>();
    public ObservableCollection<Lookup> StaffOptions { get; } = [];

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private UserRole _role = UserRole.Receptionist;
    [ObservableProperty] private Lookup? _staffMember;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _roleDescription = "";

    public bool IsNew => Existing is null;

    public override async Task InitializeAsync()
    {
        StaffOptions.Add(new Lookup(0, "Not linked"));
        foreach (var s in await staff.LookupAsync()) StaffOptions.Add(s);
        StaffMember = StaffOptions[0];
        if (Existing is { } u)
        {
            Username = u.Username;
            DisplayName = u.DisplayName;
            Role = u.Role;
            StaffMember = StaffOptions.FirstOrDefault(s => s.Id == u.StaffMemberId) ?? StaffOptions[0];
            IsActive = u.IsActive;
        }

        RoleDescription = RolePermissions.Describe(Role);
    }

    partial void OnRoleChanged(UserRole value) => RoleDescription = RolePermissions.Describe(value);

    [RelayCommand]
    private async Task SaveAsync()
    {
        int? staffId = StaffMember is { Id: > 0 } s ? s.Id : null;
        var ok = await RunAsync(async () =>
        {
            if (Existing is { } u) await auth.UpdateUserAsync(u.Id, DisplayName, Role, staffId, IsActive);
            else await auth.CreateUserAsync(Username, DisplayName, Role, staffId, Password);
        });
        Password = "";
        if (ok) Close(true);
    }
}

public sealed partial class DateRangeDialogViewModel : DialogViewModel
{
    public DateRangeDialogViewModel(string title, string message, string confirmText, bool showReason, bool allDayDefault, IReadOnlyList<string>? kinds = null)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        ShowReason = showReason;
        AllDay = allDayDefault;
        Kinds = kinds ?? [];
        Kind = Kinds.FirstOrDefault() ?? "";
    }

    public override string Title { get; }
    public override double DialogWidth => 520;
    public string Message { get; }
    public string ConfirmText { get; }
    public bool ShowReason { get; }
    public IReadOnlyList<string> Kinds { get; }
    public bool HasKinds => Kinds.Count > 0;

    [ObservableProperty] private DateTime? _startDate = DateTime.Today;
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private string _startTime = "09:00";
    [ObservableProperty] private string _endTime = "17:00";
    [ObservableProperty] private bool _allDay;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _kind = "";

    public sealed record Result(DateTime Start, DateTime End, string Reason, string Kind);

    [RelayCommand]
    private void Confirm()
    {
        if (StartDate is null || EndDate is null)
        {
            Ui.Toasts.Warning("Choose the start and end dates.");
            return;
        }

        DateTime start, end;
        if (AllDay)
        {
            start = StartDate.Value.Date;
            end = EndDate.Value.Date.AddDays(1);
        }
        else
        {
            if (!TimeOnly.TryParse(StartTime, out var st) || !TimeOnly.TryParse(EndTime, out var et))
            {
                Ui.Toasts.Warning("Enter times as HH:mm.");
                return;
            }

            start = StartDate.Value.Date + st.ToTimeSpan();
            end = EndDate.Value.Date + et.ToTimeSpan();
        }

        if (end <= start)
        {
            Ui.Toasts.Warning("The end must be after the start.");
            return;
        }

        if (ShowReason && string.IsNullOrWhiteSpace(Reason))
        {
            Ui.Toasts.Warning("Enter a reason.");
            return;
        }

        Close(new Result(start, end, Reason.Trim(), Kind));
    }
}
