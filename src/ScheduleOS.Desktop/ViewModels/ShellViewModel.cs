using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Notifications;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Desktop.ViewModels.Dialogs;
using ScheduleOS.Desktop.ViewModels.Pages;
using ScheduleOS.Domain;
using ScheduleOS.Infrastructure;
using ScheduleOS.Infrastructure.Persistence;

namespace ScheduleOS.Desktop.ViewModels;

public enum ShellPhase { Starting, Error, Onboarding, Login, Main }

public sealed record NavItem(string Key, string Title, string Icon, Type PageType, Permission? Permission, string AutomationId);

public sealed record LocationOption(int? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly DatabaseInitializer _initializer;
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly UserSession _session;
    private readonly ThemeService _theme;
    private readonly IdleMonitor _idle;
    private readonly AppState _state;
    private readonly LocationService _locations;
    private readonly NotificationService _notifications;
    private readonly IDataChangeNotifier _notifier;
    private readonly AppReadySignal _ready;
    private readonly IDemoDataService _demo;
    private readonly ILogger<ShellViewModel> _logger;
    private bool _suppressNav;

    public ShellViewModel(DatabaseInitializer initializer, SettingsService settings, AuthService auth, UserSession session,
        NavigationService navigation, DialogService dialogs, ToastService toasts, ThemeService theme, IdleMonitor idle, AppState state,
        LocationService locations, NotificationService notifications, IDataChangeNotifier notifier, AppReadySignal ready,
        IDemoDataService demo, SearchViewModel search, Workflows workflows, ILogger<ShellViewModel> logger)
    {
        _initializer = initializer;
        _settings = settings;
        _auth = auth;
        _session = session;
        _theme = theme;
        _idle = idle;
        _state = state;
        _locations = locations;
        _notifications = notifications;
        _notifier = notifier;
        _ready = ready;
        _demo = demo;
        _logger = logger;
        Navigation = navigation;
        Dialogs = dialogs;
        Toasts = toasts;
        Search = search;
        Workflows = workflows;
        Lock = new LockViewModel(auth, session, OnUnlocked, () => _ = SignOutAsync());
        Navigation.Navigated += (_, _) => SyncSelectedNav();
        _notifier.DataChanged += (_, e) =>
        {
            if ((e.Area & (DataArea.Notifications | DataArea.Settings)) != 0) Ui.Dispatch(RefreshChromeAsync);
        };
        _settings.SettingsChanged += (_, _) => Ui.Dispatch(() =>
        {
            BusinessName = _settings.Business.Name;
            IsDemoWorkspace = _settings.Business.IsDemoWorkspace;
            SidebarCollapsed = _settings.Current.SidebarCollapsed;
            _theme.Apply(_settings.Current.Theme, _settings.Current.Density);
        });
    }

    public NavigationService Navigation { get; }
    public DialogService Dialogs { get; }
    public ToastService Toasts { get; }
    public SearchViewModel Search { get; }
    public Workflows Workflows { get; }
    public LockViewModel Lock { get; }

    public ObservableCollection<NavItem> NavItems { get; } = [];
    public ObservableCollection<LocationOption> Locations { get; } = [];

    [ObservableProperty] private ShellPhase _phase = ShellPhase.Starting;
    [ObservableProperty] private string _startupMessage = "Starting ScheduleOS…";
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private OnboardingViewModel? _onboarding;
    [ObservableProperty] private LoginViewModel? _login;
    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _userRole = "";
    [ObservableProperty] private string _userInitials = "";
    [ObservableProperty] private string _businessName = "ScheduleOS";
    [ObservableProperty] private bool _isDemoWorkspace;
    [ObservableProperty] private LocationOption? _selectedLocation;
    [ObservableProperty] private bool _sidebarCollapsed;

    public bool CanCreateAppointments => _session.Has(Permission.ManageAppointments);
    public bool CanManageCustomers => _session.Has(Permission.ManageCustomers);
    public bool CanManageStaff => _session.Has(Permission.ManageStaff);
    public bool CanManageServices => _session.Has(Permission.ManageServices);
    public bool CanRecordPayments => _session.Has(Permission.RecordPayments);

    public async Task StartAsync()
    {
        try
        {
            StartupMessage = "Opening your database…";
            var result = await Task.Run(() => _initializer.InitializeAsync());
            if (!result.Success)
            {
                StartupMessage = result.Message + " Restore a backup from %LOCALAPPDATA%\\ScheduleOS\\Backups or contact support.";
                Phase = ShellPhase.Error;
                return;
            }

            await _settings.LoadAsync();
            SettingsBridge.Attach(_settings);
            _theme.Apply(_settings.Current.Theme, _settings.Current.Density);
            BusinessName = string.IsNullOrWhiteSpace(_settings.Business.Name) ? "ScheduleOS" : _settings.Business.Name;
            IsDemoWorkspace = _settings.Business.IsDemoWorkspace;
            SidebarCollapsed = _settings.Current.SidebarCollapsed;

            if (!await _auth.HasAnyUserAsync())
            {
                Onboarding = new OnboardingViewModel(_auth, _demo, async () => await ShowLoginAsync("Your workspace is ready. Sign in with the owner account you just created."));
                Phase = ShellPhase.Onboarding;
            }
            else
            {
                await ShowLoginAsync(null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup failed");
            StartupMessage = "ScheduleOS could not start: " + ex.Message;
            Phase = ShellPhase.Error;
        }
    }

    private Task ShowLoginAsync(string? message)
    {
        Login = new LoginViewModel(_auth, _settings, OnSignedInAsync) { Info = message ?? "" };
        Onboarding = null;
        Phase = ShellPhase.Login;
        return Task.CompletedTask;
    }

    private async Task OnSignedInAsync()
    {
        UserName = _session.DisplayName;
        UserRole = _session.Role.ToString();
        UserInitials = Domain.Entities.NameHelper.Initials(_session.DisplayName);
        BuildNavigation();
        await LoadLocationsAsync();
        Phase = ShellPhase.Main;
        Login = null;
        OnPropertyChanged(nameof(CanCreateAppointments));
        OnPropertyChanged(nameof(CanManageCustomers));
        OnPropertyChanged(nameof(CanManageStaff));
        OnPropertyChanged(nameof(CanManageServices));
        OnPropertyChanged(nameof(CanRecordPayments));
        await RefreshChromeAsync();
        _ready.Signal();
        _idle.Start(() => _settings.Current.SessionLockMinutes, () => IsLocked = true);
        await Navigation.NavigateAsync<DashboardViewModel>();
    }

    private void BuildNavigation()
    {
        NavItems.Clear();
        var all = new NavItem[]
        {
            new("Dashboard", "Dashboard", "", typeof(DashboardViewModel), Permission.ViewDashboard, "Nav_Dashboard"),
            new("Calendar", "Calendar", "", typeof(CalendarViewModel), null, "Nav_Calendar"),
            new("Appointments", "Appointments", "", typeof(AppointmentsViewModel), null, "Nav_Appointments"),
            new("Customers", "Customers", "", typeof(CustomersViewModel), Permission.ViewCustomers, "Nav_Customers"),
            new("Staff", "Staff", "", typeof(StaffViewModel), Permission.ViewStaff, "Nav_Staff"),
            new("Services", "Services", "", typeof(ServicesViewModel), null, "Nav_Services"),
            new("Resources", "Rooms & Resources", "", typeof(ResourcesViewModel), null, "Nav_Resources"),
            new("Payments", "Payments", "", typeof(PaymentsViewModel), Permission.ViewPayments, "Nav_Payments"),
            new("Analytics", "Analytics & Reports", "", typeof(AnalyticsViewModel), Permission.ViewReports, "Nav_Analytics"),
            new("Settings", "Settings", "", typeof(SettingsViewModel), null, "Nav_Settings"),
        };
        foreach (var item in all.Where(i => i.Permission is null || _session.Has(i.Permission.Value))) NavItems.Add(item);
    }

    private async Task LoadLocationsAsync()
    {
        Locations.Clear();
        var list = await _locations.ListAsync();
        if (list.Count > 1) Locations.Add(new LocationOption(null, "All locations"));
        foreach (var l in list) Locations.Add(new LocationOption(l.Id, l.Name));
        SelectedLocation = Locations.FirstOrDefault();
    }

    partial void OnSelectedLocationChanged(LocationOption? value)
    {
        _state.SelectedLocationId = value?.Id;
        _state.SelectedLocationName = value?.Name ?? "All locations";
        if (Phase == ShellPhase.Main) _ = Navigation.ReloadAsync();
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (_suppressNav || value is null) return;
        _ = Navigation.NavigateAsync(value.PageType);
    }

    private void SyncSelectedNav()
    {
        var key = Navigation.Current?.NavKey;
        _suppressNav = true;
        SelectedNav = NavItems.FirstOrDefault(n => n.Key == key);
        _suppressNav = false;
    }

    private async Task RefreshChromeAsync()
    {
        if (!_session.IsAuthenticated) return;
        try
        {
            UnreadCount = await _notifications.UnreadCountAsync();
            BusinessName = _settings.Business.Name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh notification count");
        }
    }

    private void OnUnlocked()
    {
        IsLocked = false;
        _idle.Start(() => _settings.Current.SessionLockMinutes, () => IsLocked = true);
    }

    [RelayCommand]
    private void SelectLocation(LocationOption option) => SelectedLocation = option;

    [RelayCommand]
    private void LockSession()
    {
        _idle.Stop();
        Lock.Reset();
        IsLocked = true;
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        _idle.Stop();
        Navigation.Reset();
        foreach (var d in Dialogs.Stack.ToList()) d.Cancel();
        _auth.Logout();
        IsLocked = false;
        NavItems.Clear();
        await ShowLoginAsync("You have been signed out.");
    }

    [RelayCommand]
    private async Task ChangePasswordAsync() => await Dialogs.ShowAsync(new ChangePasswordDialogViewModel(_auth));

    [RelayCommand]
    private Task OpenNotificationsAsync() => Navigation.NavigateAsync<NotificationsViewModel>(addToHistory: true);

    [RelayCommand]
    private Task GoBackAsync() => Navigation.GoBackAsync();

    [RelayCommand]
    private Task RefreshAsync() => Navigation.ReloadAsync();

    [RelayCommand]
    private async Task ToggleSidebarAsync()
    {
        SidebarCollapsed = !SidebarCollapsed;
        await TrySaveAppearanceAsync();
    }

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        var next = _settings.Current.Theme == ColorTheme.Dark ? ColorTheme.Light : ColorTheme.Dark;
        _theme.Apply(next, _settings.Current.Density);
        await _settings.SaveAppearanceAsync(next, _settings.Current.Density, SidebarCollapsed);
    }

    private async Task TrySaveAppearanceAsync()
    {
        try
        {
            await _settings.SaveAppearanceAsync(_settings.Current.Theme, _settings.Current.Density, SidebarCollapsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save appearance");
        }
    }

    [RelayCommand] private Task NewAppointmentAsync() => Workflows.NewAppointmentAsync();
    [RelayCommand] private Task NewCustomerAsync() => Workflows.NewCustomerAsync(openAfter: true);
    [RelayCommand] private Task NewStaffAsync() => Workflows.NewStaffAsync();
    [RelayCommand] private Task NewServiceAsync() => Workflows.NewServiceAsync();
    [RelayCommand] private Task RecordPaymentAsync() => Workflows.RecordPaymentAsync(null);

    [RelayCommand]
    private void OpenLogFolder()
    {
        var dir = Ui.Get<IDatabaseInfo>().LogDirectory;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
