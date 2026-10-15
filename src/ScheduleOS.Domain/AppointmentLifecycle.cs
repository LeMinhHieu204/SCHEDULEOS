namespace ScheduleOS.Domain;

/// <summary>Strict appointment state machine.</summary>
public static class AppointmentLifecycle
{
    private static readonly Dictionary<AppointmentStatus, AppointmentStatus[]> Transitions = new()
    {
        [AppointmentStatus.Draft] = [AppointmentStatus.Pending, AppointmentStatus.Confirmed, AppointmentStatus.Cancelled],
        [AppointmentStatus.Pending] = [AppointmentStatus.Confirmed, AppointmentStatus.Cancelled, AppointmentStatus.NoShow],
        [AppointmentStatus.Confirmed] = [AppointmentStatus.CheckedIn, AppointmentStatus.Cancelled, AppointmentStatus.NoShow],
        [AppointmentStatus.CheckedIn] = [AppointmentStatus.InProgress, AppointmentStatus.Cancelled],
        [AppointmentStatus.InProgress] = [AppointmentStatus.Completed],
        [AppointmentStatus.Completed] = [],
        [AppointmentStatus.Cancelled] = [],
        [AppointmentStatus.NoShow] = [],
    };

    /// <summary>
    /// Controlled corrections for terminal states. These require the CorrectAppointmentStatus permission,
    /// a written reason and (for reinstatement) a fresh conflict check.
    /// </summary>
    private static readonly Dictionary<AppointmentStatus, AppointmentStatus> Corrections = new()
    {
        [AppointmentStatus.Completed] = AppointmentStatus.InProgress,
        [AppointmentStatus.NoShow] = AppointmentStatus.Confirmed,
        [AppointmentStatus.Cancelled] = AppointmentStatus.Pending,
    };

    public static bool CanTransition(AppointmentStatus from, AppointmentStatus to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyList<AppointmentStatus> AllowedTargets(AppointmentStatus from) =>
        Transitions.TryGetValue(from, out var targets) ? targets : [];

    public static AppointmentStatus? CorrectionTarget(AppointmentStatus from) =>
        Corrections.TryGetValue(from, out var to) ? to : null;

    public static bool IsTerminal(AppointmentStatus status) => AllowedTargets(status).Count == 0;

    /// <summary>Statuses whose time slot is occupied (they block staff and resources).</summary>
    public static bool IsBlocking(AppointmentStatus status) => status is
        AppointmentStatus.Pending or AppointmentStatus.Confirmed or AppointmentStatus.CheckedIn or
        AppointmentStatus.InProgress or AppointmentStatus.Completed;

    public static bool CanReschedule(AppointmentStatus status) => status is
        AppointmentStatus.Draft or AppointmentStatus.Pending or AppointmentStatus.Confirmed;

    public static bool CanEditServices(AppointmentStatus status) => status is
        AppointmentStatus.Draft or AppointmentStatus.Pending or AppointmentStatus.Confirmed or AppointmentStatus.CheckedIn;

    public static bool AcceptsPayments(AppointmentStatus status) => status is not AppointmentStatus.Draft;
}
