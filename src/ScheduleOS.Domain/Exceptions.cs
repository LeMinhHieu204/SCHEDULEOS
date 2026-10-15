using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Domain;

/// <summary>Base type for business rule violations that are safe to show to the user.</summary>
public class DomainException(string message) : Exception(message);

public sealed class ValidationException(IReadOnlyList<string> errors)
    : DomainException(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;

    public ValidationException(string error) : this([error]) { }
}

public sealed class NotFoundException(string entity, object id)
    : DomainException($"{entity} '{id}' was not found.");

public sealed class InvalidTransitionException(AppointmentStatus from, AppointmentStatus to)
    : DomainException($"An appointment cannot move from {from} to {to}.")
{
    public AppointmentStatus From { get; } = from;
    public AppointmentStatus To { get; } = to;
}

public sealed class SchedulingConflictException(IReadOnlyList<ScheduleConflict> conflicts)
    : DomainException(BuildMessage(conflicts))
{
    public IReadOnlyList<ScheduleConflict> Conflicts { get; } = conflicts;

    private static string BuildMessage(IReadOnlyList<ScheduleConflict> conflicts) =>
        "The booking conflicts with the schedule:" + Environment.NewLine +
        string.Join(Environment.NewLine, conflicts.Select(c => "• " + c.Message));
}

public sealed class ConcurrencyConflictException(string message) : DomainException(message);

public sealed class DuplicateSubmissionException(string reference)
    : DomainException($"This booking was already submitted as {reference}.")
{
    public string Reference { get; } = reference;
}

public sealed class PermissionDeniedException(string action)
    : DomainException($"You do not have permission to {action}.");
