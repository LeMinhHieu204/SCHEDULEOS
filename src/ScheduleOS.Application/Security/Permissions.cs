using ScheduleOS.Domain;

namespace ScheduleOS.Application.Security;

public enum Permission
{
    ViewDashboard,
    ViewAllAppointments,
    ManageAppointments,
    ChangeAppointmentStatus,
    CorrectAppointmentStatus,
    OverrideSchedulingWarnings,
    ViewInternalNotes,
    EditInternalNotes,
    ViewCustomers,
    ViewCustomerContact,
    ManageCustomers,
    ExportCustomerData,
    AnonymizeCustomers,
    ViewStaff,
    ManageStaff,
    ManageServices,
    ManageResources,
    ViewPayments,
    RecordPayments,
    RefundPayments,
    ReversePayments,
    ViewReports,
    ManageSettings,
    ManageUsers,
    CreateBackups,
    RestoreBackups,
    ViewAuditLog,
}

public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<UserRole, HashSet<Permission>> Map = new Dictionary<UserRole, HashSet<Permission>>
    {
        [UserRole.Owner] = [.. Enum.GetValues<Permission>()],
        [UserRole.Manager] =
        [
            Permission.ViewDashboard, Permission.ViewAllAppointments, Permission.ManageAppointments,
            Permission.ChangeAppointmentStatus, Permission.CorrectAppointmentStatus, Permission.OverrideSchedulingWarnings,
            Permission.ViewInternalNotes, Permission.EditInternalNotes, Permission.ViewCustomers, Permission.ViewCustomerContact,
            Permission.ManageCustomers, Permission.ExportCustomerData, Permission.ViewStaff, Permission.ManageStaff,
            Permission.ManageServices, Permission.ManageResources, Permission.ViewPayments, Permission.RecordPayments,
            Permission.RefundPayments, Permission.ReversePayments, Permission.ViewReports, Permission.CreateBackups,
            Permission.ViewAuditLog,
        ],
        [UserRole.Receptionist] =
        [
            Permission.ViewDashboard, Permission.ViewAllAppointments, Permission.ManageAppointments,
            Permission.ChangeAppointmentStatus, Permission.ViewInternalNotes, Permission.EditInternalNotes,
            Permission.ViewCustomers, Permission.ViewCustomerContact, Permission.ManageCustomers, Permission.ViewStaff,
            Permission.ViewPayments, Permission.RecordPayments,
        ],
        [UserRole.Staff] =
        [
            Permission.ViewDashboard, Permission.ChangeAppointmentStatus, Permission.ViewInternalNotes,
            Permission.ViewCustomers, Permission.ViewStaff,
        ],
    };

    public static bool Has(UserRole role, Permission permission) => Map.TryGetValue(role, out var set) && set.Contains(permission);

    public static IReadOnlyCollection<Permission> For(UserRole role) => Map.TryGetValue(role, out var set) ? set : [];

    public static string Describe(UserRole role) => role switch
    {
        UserRole.Owner => "Full access to every module, users, settings, backups and restore.",
        UserRole.Manager => "Appointments, customers, staff, services, resources, payments, refunds and reports.",
        UserRole.Receptionist => "Scheduling, customers, check-in and recording payments.",
        UserRole.Staff => "Own calendar and assigned appointments; can check in, start and complete own services.",
        _ => "",
    };
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    int? UserId { get; }
    string DisplayName { get; }
    string Username { get; }
    UserRole Role { get; }
    int? StaffMemberId { get; }
    bool Has(Permission permission);

    /// <summary>Throws <see cref="PermissionDeniedException"/> when the permission is missing. Enforced in services, not only in the UI.</summary>
    void Require(Permission permission, string action);
}

public sealed class UserSession : ICurrentUser
{
    public bool IsAuthenticated { get; private set; }
    public int? UserId { get; private set; }
    public string DisplayName { get; private set; } = "";
    public string Username { get; private set; } = "";
    public UserRole Role { get; private set; } = UserRole.Staff;
    public int? StaffMemberId { get; private set; }

    public event EventHandler? Changed;

    public void SignIn(int userId, string username, string displayName, UserRole role, int? staffMemberId)
    {
        UserId = userId;
        Username = username;
        DisplayName = displayName;
        Role = role;
        StaffMemberId = staffMemberId;
        IsAuthenticated = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        IsAuthenticated = false;
        UserId = null;
        Username = DisplayName = "";
        StaffMemberId = null;
        Role = UserRole.Staff;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Has(Permission permission) => IsAuthenticated && RolePermissions.Has(Role, permission);

    public void Require(Permission permission, string action)
    {
        if (!Has(permission)) throw new PermissionDeniedException(action);
    }
}
