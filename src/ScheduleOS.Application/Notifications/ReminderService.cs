using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Notifications;

/// <summary>Builds reminder queue entries. All methods run inside the caller's write transaction.</summary>
public static class ReminderPlanner
{
    public static void OnBooked(IAppDbContext db, Appointment appt, Customer customer, AppSettings s, string businessName, DateTime now)
    {
        if (appt.Status == AppointmentStatus.Confirmed && s.SendConfirmationMessages)
            QueueCustomerMessage(db, appt, customer, ReminderType.Confirmation, now, s, businessName, now);
        QueueUpcoming(db, appt, customer, s, businessName, now);
    }

    public static void OnConfirmed(IAppDbContext db, Appointment appt, Customer customer, AppSettings s, string businessName, DateTime now)
    {
        if (s.SendConfirmationMessages)
            QueueCustomerMessage(db, appt, customer, ReminderType.Confirmation, now, s, businessName, now);
    }

    public static async Task OnRescheduledAsync(IAppDbContext db, Appointment appt, Customer customer, AppSettings s, string businessName, DateTime now, CancellationToken ct)
    {
        await CancelPendingAsync(db, appt.Id, [ReminderType.Upcoming], ct);
        QueueCustomerMessage(db, appt, customer, ReminderType.Reschedule, now, s, businessName, now);
        QueueUpcoming(db, appt, customer, s, businessName, now);
    }

    public static async Task OnCancelledAsync(IAppDbContext db, Appointment appt, Customer customer, AppSettings s, string businessName, DateTime now, CancellationToken ct)
    {
        await CancelPendingAsync(db, appt.Id, [ReminderType.Upcoming, ReminderType.Confirmation, ReminderType.Reschedule], ct);
        if (appt.Start > now) QueueCustomerMessage(db, appt, customer, ReminderType.Cancellation, now, s, businessName, now);
    }

    public static async Task OnClosedAsync(IAppDbContext db, Appointment appt, CancellationToken ct) =>
        await CancelPendingAsync(db, appt.Id, [ReminderType.Upcoming], ct);

    public static void QueueFollowUp(IAppDbContext db, Appointment appt, Customer customer, DateTime dueAt, string note, DateTime now)
    {
        Add(db, new Reminder
        {
            Appointment = appt.Id == 0 ? appt : null,
            AppointmentId = appt.Id == 0 ? null : appt.Id,
            CustomerId = customer.Id,
            Channel = ReminderChannel.InApp,
            Type = ReminderType.FollowUp,
            DueAt = dueAt,
            Subject = $"Follow up with {customer.FullName}",
            Body = string.IsNullOrWhiteSpace(note) ? $"Follow up on {appt.Reference}." : note.Trim(),
            DedupKey = $"{appt.Reference}:FollowUp:{dueAt:yyyyMMddHHmm}:{Guid.NewGuid():N}",
            CreatedAt = now,
            IsDemo = appt.IsDemo,
        });
    }

    private static void QueueUpcoming(IAppDbContext db, Appointment appt, Customer customer, AppSettings s, string businessName, DateTime now)
    {
        var lead = appt.ReminderLeadHours > 0 ? appt.ReminderLeadHours : s.ReminderLeadHours;
        var due = appt.Start.AddHours(-lead);
        if (due > now) QueueCustomerMessage(db, appt, customer, ReminderType.Upcoming, due, s, businessName, now);

        if (s.LocalNotificationsEnabled)
        {
            var inAppDue = appt.Start.AddMinutes(-Math.Max(0, s.InAppReminderMinutesBefore));
            if (inAppDue > now)
                Add(db, Build(appt, customer, ReminderChannel.InApp, ReminderType.Upcoming, inAppDue, null,
                    $"Upcoming: {customer.FullName} at {appt.Start:t}",
                    $"{appt.Reference} — {Services(appt)} starts at {appt.Start:t}.", now));
        }
    }

    private static void QueueCustomerMessage(IAppDbContext db, Appointment appt, Customer customer, ReminderType type, DateTime due,
        AppSettings s, string businessName, DateTime now)
    {
        if (customer.IsAnonymized) return;
        var (subject, body) = Compose(type, appt, customer, businessName, s);
        if (appt.RemindByEmail && customer.AllowEmail && !string.IsNullOrWhiteSpace(customer.Email))
            Add(db, Build(appt, customer, ReminderChannel.Email, type, due, customer.Email, subject, body, now));
        if (appt.RemindBySms && customer.AllowSms && !string.IsNullOrWhiteSpace(customer.Phone))
            Add(db, Build(appt, customer, ReminderChannel.Sms, type, due, customer.Phone, subject, ShortText(type, appt, businessName), now));
    }

    private static Reminder Build(Appointment appt, Customer customer, ReminderChannel channel, ReminderType type, DateTime due,
        string? recipient, string subject, string body, DateTime now) => new()
    {
        Appointment = appt.Id == 0 ? appt : null,
        AppointmentId = appt.Id == 0 ? null : appt.Id,
        CustomerId = customer.Id,
        Channel = channel,
        Type = type,
        DueAt = due,
        Status = ReminderStatus.Scheduled,
        Recipient = recipient,
        Subject = subject,
        Body = body,
        DedupKey = $"{appt.Reference}:{type}:{channel}:{due.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}:{appt.Start.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}",
        CreatedAt = now,
        IsDemo = appt.IsDemo,
    };

    private static void Add(IAppDbContext db, Reminder reminder)
    {
        // Duplicate protection: also enforced by a unique index on DedupKey.
        if (db.Reminders.Local.Any(r => r.DedupKey == reminder.DedupKey)) return;
        if (db.Reminders.Any(r => r.DedupKey == reminder.DedupKey)) return;
        db.Reminders.Add(reminder);
        if (reminder.Channel == ReminderChannel.InApp && reminder.Type != ReminderType.FollowUp) return; // staff-side alert, not a customer message
        if (reminder.Appointment is not null)
            reminder.Appointment.Events.Add(Event(reminder));
        else if (reminder.AppointmentId is { } id)
            db.AppointmentEvents.Add(Event(reminder, id));
    }

    private static AppointmentEvent Event(Reminder r, int? appointmentId = null) => new()
    {
        AppointmentId = appointmentId ?? 0,
        Timestamp = r.CreatedAt,
        Type = AppointmentEventType.ReminderScheduled,
        Description = $"{Describe(r.Type)} ({ChannelName(r.Channel)}) scheduled for {r.DueAt:g}",
        UserName = "System",
    };

    private static async Task CancelPendingAsync(IAppDbContext db, int appointmentId, ReminderType[] types, CancellationToken ct)
    {
        if (appointmentId == 0) return;
        var pending = await db.Reminders.Where(r => r.AppointmentId == appointmentId && r.Status == ReminderStatus.Scheduled && types.Contains(r.Type)).ToListAsync(ct);
        foreach (var r in pending) Supersede(r, "Superseded by an appointment change.");
    }

    /// <summary>Cancels a scheduled reminder and frees its de-duplication key so an equivalent reminder can be queued again.</summary>
    public static void Supersede(Reminder r, string reason)
    {
        r.Status = ReminderStatus.Cancelled;
        r.LastError = reason;
        r.DedupKey += ":x" + r.Id;
    }

    private static string Services(Appointment appt) => string.Join(", ", appt.Segments.OrderBy(s => s.SortOrder).Select(s => s.ServiceName));

    private static (string Subject, string Body) Compose(ReminderType type, Appointment appt, Customer c, string business, AppSettings s)
    {
        var when = appt.Start.ToString("dddd, MMMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture);
        var services = Services(appt);
        return type switch
        {
            ReminderType.Confirmation => ($"Your appointment at {business} is confirmed",
                $"Hello {c.FullName},\n\nYour appointment ({appt.Reference}) for {services} is confirmed for {when}.\n\n{s.CancellationPolicy}\n\n{business}"),
            ReminderType.Upcoming => ($"Reminder: your appointment at {business}",
                $"Hello {c.FullName},\n\nThis is a reminder of your appointment ({appt.Reference}) for {services} on {when}.\n\n{business}"),
            ReminderType.Reschedule => ($"Your appointment at {business} has been rescheduled",
                $"Hello {c.FullName},\n\nYour appointment ({appt.Reference}) for {services} has been moved to {when}.\n\n{business}"),
            ReminderType.Cancellation => ($"Your appointment at {business} has been cancelled",
                $"Hello {c.FullName},\n\nYour appointment ({appt.Reference}) on {when} has been cancelled.\n\n{business}"),
            _ => ($"Follow-up from {business}", $"Hello {c.FullName},\n\nThank you for visiting {business}.\n"),
        };
    }

    private static string ShortText(ReminderType type, Appointment appt, string business)
    {
        var when = appt.Start.ToString("MMM d h:mm tt", CultureInfo.InvariantCulture);
        return type switch
        {
            ReminderType.Confirmation => $"{business}: appointment {appt.Reference} confirmed for {when}.",
            ReminderType.Upcoming => $"{business}: reminder of your appointment on {when}.",
            ReminderType.Reschedule => $"{business}: your appointment moved to {when}.",
            ReminderType.Cancellation => $"{business}: your appointment on {when} was cancelled.",
            _ => $"{business}: thank you for your visit.",
        };
    }

    public static string Describe(ReminderType t) => t switch
    {
        ReminderType.Confirmation => "Confirmation message",
        ReminderType.Upcoming => "Appointment reminder",
        ReminderType.Cancellation => "Cancellation notice",
        ReminderType.Reschedule => "Reschedule notice",
        _ => "Follow-up reminder",
    };

    public static string ChannelName(ReminderChannel c) => c switch
    {
        ReminderChannel.Email => "Email",
        ReminderChannel.Sms => "SMS",
        _ => "In-app",
    };
}

public sealed class ReminderService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    IEmailSender email,
    ISmsSender sms,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier,
    ILogger<ReminderService> logger)
{
    private const int MaxAttempts = 3;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Processes due reminders. Safe to call repeatedly; a lease prevents duplicate sends.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return 0;
        try
        {
            var now = clock.Now;
            var claimed = await writer.ExecuteAsync(async (db, c) =>
            {
                var due = await db.Reminders
                    .Where(r => r.Status == ReminderStatus.Scheduled && r.DueAt <= now && (r.NextAttemptAt == null || r.NextAttemptAt <= now))
                    .OrderBy(r => r.DueAt).Take(25).ToListAsync(c);
                foreach (var r in due)
                {
                    r.Attempts++;
                    r.NextAttemptAt = now.AddMinutes(5 * r.Attempts); // lease + backoff
                }

                await db.SaveChangesAsync(c);
                return due.Select(r => (r.Id, r.Channel, r.Recipient, r.Subject, r.Body, r.Attempts)).ToList();
            }, ct);

            foreach (var (id, channel, recipient, subject, body, attempts) in claimed)
            {
                DeliveryResult result;
                try
                {
                    result = channel switch
                    {
                        ReminderChannel.InApp => DeliveryResult.Success(),
                        ReminderChannel.Email => email.IsConfigured
                            ? await email.SendAsync(new EmailMessage(recipient ?? "", subject, body), ct)
                            : DeliveryResult.Unconfigured("SMTP is not configured. The message was kept as a draft and was NOT delivered."),
                        _ => sms.IsConfigured
                            ? await sms.SendAsync(recipient ?? "", body, ct)
                            : DeliveryResult.Unconfigured("No SMS provider is configured. The message was NOT delivered."),
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = DeliveryResult.Failure(ex.Message);
                }

                await RecordOutcomeAsync(id, result, attempts, ct);
            }

            if (claimed.Count > 0) notifier.Publish(DataArea.Notifications | DataArea.Appointments);
            return claimed.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RecordOutcomeAsync(int reminderId, DeliveryResult result, int attempts, CancellationToken ct)
    {
        var now = clock.Now;
        await writer.ExecuteAsync(async (db, c) =>
        {
            var r = await db.Reminders.FirstAsync(x => x.Id == reminderId, c);
            AppointmentEventType? evt = null;
            string description;
            if (result.Delivered)
            {
                r.Status = ReminderStatus.Sent;
                r.ProcessedAt = now;
                r.LastError = null;
                description = r.Channel == ReminderChannel.InApp
                    ? $"{ReminderPlanner.Describe(r.Type)} shown in the notification centre"
                    : $"{ReminderPlanner.Describe(r.Type)} sent by {ReminderPlanner.ChannelName(r.Channel)} to {r.Recipient}";
                if (r.Channel != ReminderChannel.InApp) evt = AppointmentEventType.ReminderSent;
                if (r.Channel == ReminderChannel.InApp)
                    db.Notifications.Add(new Notification
                    {
                        CreatedAt = now, Title = r.Subject, Message = r.Body, AppointmentId = r.AppointmentId, ReminderId = r.Id,
                        Kind = r.Type == ReminderType.FollowUp ? NotificationKind.Info : NotificationKind.Reminder, IsDemo = r.IsDemo,
                    });
            }
            else if (result.NotConfigured)
            {
                r.Status = ReminderStatus.NotSent;
                r.ProcessedAt = now;
                r.LastError = result.Error;
                evt = AppointmentEventType.ReminderNotSent;
                description = $"{ReminderPlanner.Describe(r.Type)} ({ReminderPlanner.ChannelName(r.Channel)}) not sent: {result.Error}";
            }
            else
            {
                r.LastError = result.Error;
                if (attempts >= MaxAttempts)
                {
                    r.Status = ReminderStatus.Failed;
                    r.ProcessedAt = now;
                    evt = AppointmentEventType.ReminderFailed;
                    description = $"{ReminderPlanner.Describe(r.Type)} ({ReminderPlanner.ChannelName(r.Channel)}) failed after {attempts} attempts: {result.Error}";
                    db.Notifications.Add(new Notification
                    {
                        CreatedAt = now, Kind = NotificationKind.Error, Title = "Message delivery failed",
                        Message = $"{r.Subject} to {r.Recipient}: {result.Error}", AppointmentId = r.AppointmentId, ReminderId = r.Id,
                    });
                }
                else
                {
                    description = "";
                }
            }

            if (evt is not null && r.AppointmentId is { } apptId)
                db.AppointmentEvents.Add(new AppointmentEvent { AppointmentId = apptId, Timestamp = now, Type = evt.Value, Description = description, UserName = "System" });
            await db.SaveChangesAsync(c);
        }, ct);

        if (result.NotConfigured) logger.LogInformation("Reminder {ReminderId} recorded as not sent (channel not configured)", reminderId);
        else if (!result.Delivered) logger.LogWarning("Reminder {ReminderId} delivery failed (attempt {Attempt})", reminderId, attempts);
    }

    public async Task<IReadOnlyList<ReminderDto>> ListAsync(ReminderStatus? status, DateTime? from, DateTime? to, int take = 300, CancellationToken ct = default)
    {
        user.Require(Permission.ViewAllAppointments, "view the reminder queue");
        await using var db = dbFactory.CreateReadContext();
        var q = db.Reminders.AsNoTracking().AsQueryable();
        if (status is not null) q = q.Where(r => r.Status == status);
        if (from is not null) q = q.Where(r => r.DueAt >= from);
        if (to is not null) q = q.Where(r => r.DueAt < to);
        return await q.OrderByDescending(r => r.DueAt).Take(take)
            .Select(r => new ReminderDto(r.Id, r.Channel, r.Type, r.DueAt, r.Status, r.Recipient, r.Subject, r.LastError, r.Attempts,
                r.ProcessedAt, r.AppointmentId, r.Appointment != null ? r.Appointment.Reference : null))
            .ToListAsync(ct);
    }

    public async Task<(string Subject, string Body, string? Recipient)> PreviewAsync(int reminderId, CancellationToken ct = default)
    {
        user.Require(Permission.ViewAllAppointments, "preview messages");
        await using var db = dbFactory.CreateReadContext();
        var r = await db.Reminders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reminderId, ct) ?? throw new NotFoundException("Reminder", reminderId);
        return (r.Subject, r.Body, r.Recipient);
    }

    public async Task CancelAsync(int reminderId, CancellationToken ct = default)
    {
        user.Require(Permission.ManageAppointments, "cancel reminders");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var r = await db.Reminders.FirstOrDefaultAsync(x => x.Id == reminderId, c) ?? throw new NotFoundException("Reminder", reminderId);
            if (r.Status != ReminderStatus.Scheduled) throw new ValidationException("Only scheduled reminders can be cancelled.");
            ReminderPlanner.Supersede(r, "Cancelled by " + user.DisplayName);
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Notifications);
    }

    public async Task RetryAsync(int reminderId, CancellationToken ct = default)
    {
        user.Require(Permission.ManageAppointments, "retry reminders");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var r = await db.Reminders.FirstOrDefaultAsync(x => x.Id == reminderId, c) ?? throw new NotFoundException("Reminder", reminderId);
            if (r.Status is not (ReminderStatus.Failed or ReminderStatus.NotSent)) throw new ValidationException("Only failed or unsent messages can be retried.");
            r.Status = ReminderStatus.Scheduled;
            r.Attempts = 0;
            r.NextAttemptAt = null;
            r.DueAt = clock.Now;
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Notifications);
    }
}

public sealed record NotificationDto(int Id, DateTime CreatedAt, string Title, string Message, NotificationKind Kind, bool IsRead, int? AppointmentId);

public sealed class NotificationService(IAppDbContextFactory dbFactory, IWriteExecutor writer, IClock clock, IDataChangeNotifier notifier)
{
    public async Task<IReadOnlyList<NotificationDto>> ListAsync(bool includeDismissed = false, int take = 100, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var q = db.Notifications.AsNoTracking().AsQueryable();
        if (!includeDismissed) q = q.Where(n => !n.IsDismissed);
        return await q.OrderByDescending(n => n.CreatedAt).Take(take)
            .Select(n => new NotificationDto(n.Id, n.CreatedAt, n.Title, n.Message, n.Kind, n.IsRead, n.AppointmentId)).ToListAsync(ct);
    }

    public async Task<int> UnreadCountAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        return await db.Notifications.CountAsync(n => !n.IsRead && !n.IsDismissed, ct);
    }

    public async Task MarkReadAsync(int id, CancellationToken ct = default) => await UpdateAsync(n => n.Id == id, n => n.IsRead = true, ct);

    public async Task MarkAllReadAsync(CancellationToken ct = default) => await UpdateAsync(n => !n.IsRead, n => n.IsRead = true, ct);

    public async Task DismissAsync(int id, CancellationToken ct = default) => await UpdateAsync(n => n.Id == id, n =>
    {
        n.IsDismissed = true;
        n.IsRead = true;
    }, ct);

    public async Task AddAsync(string title, string message, NotificationKind kind, int? appointmentId = null, CancellationToken ct = default)
    {
        await writer.ExecuteAsync(async (db, c) =>
        {
            db.Notifications.Add(new Notification { CreatedAt = clock.Now, Title = title, Message = message, Kind = kind, AppointmentId = appointmentId });
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Notifications);
    }

    private async Task UpdateAsync(System.Linq.Expressions.Expression<Func<Notification, bool>> filter, Action<Notification> change, CancellationToken ct)
    {
        await writer.ExecuteAsync(async (db, c) =>
        {
            foreach (var n in await db.Notifications.Where(filter).ToListAsync(c)) change(n);
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Notifications);
    }
}
