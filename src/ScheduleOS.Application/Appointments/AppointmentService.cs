using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Customers;
using ScheduleOS.Application.Notifications;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Application.Appointments;

public sealed record AppointmentRef(int Id, string Reference);

/// <summary>
/// All appointment mutations go through this service. Each mutation runs inside a serialised
/// SQLite write transaction, re-reads overlapping bookings inside that transaction, validates every
/// staff/resource interval and writes atomically together with its history and audit records.
/// </summary>
public sealed class AppointmentService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    SettingsService settings,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier,
    ILogger<AppointmentService> logger)
{
    // ------------------------------------------------------------------ create

    public async Task<AppointmentRef> CreateAsync(CreateAppointmentRequest req, CancellationToken ct = default)
    {
        user.Require(Permission.ManageAppointments, "create appointments");
        ValidateLines(req.Services);
        if (req.InitialStatus is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
            throw new ValidationException("New appointments must start as Pending or Confirmed.");
        if (req.Start == default) throw new ValidationException("Choose a date and start time.");
        if (req.OverrideWarnings) RequireOverride();
        var s = settings.Current;
        var businessName = settings.Business.Name;

        var result = await writer.ExecuteAsync(async (db, c) =>
        {
            var duplicate = await db.Appointments.Where(a => a.SubmissionKey == req.SubmissionKey).Select(a => a.Reference).FirstOrDefaultAsync(c);
            if (duplicate is not null) throw new DuplicateSubmissionException(duplicate);

            var customer = await db.Customers.FirstOrDefaultAsync(x => x.Id == req.CustomerId, c)
                           ?? throw new ValidationException("Select a customer.");
            if (customer.IsArchived || customer.IsAnonymized) throw new ValidationException("This customer is archived. Restore the customer before booking.");

            var (segments, services) = await SchedulingService.PlanAsync(db, req.Start, req.Services, c);
            var ctx = await SchedulingService.LoadContextAsync(db, SchedulingService.WindowStart(segments), SchedulingService.WindowEnd(segments),
                req.LocationId, s, clock.Now, c);
            var conflicts = ConflictDetector.Detect(segments, ctx, null, checkLeadTime: true);
            EnsureAcceptable(conflicts, req.OverrideWarnings, s);

            var now = clock.Now;
            var appt = new Appointment
            {
                Reference = await Sequences.NextAsync(db, "AP", now.Year, 4, c),
                SubmissionKey = req.SubmissionKey,
                CustomerId = customer.Id,
                LocationId = req.LocationId,
                Status = req.InitialStatus,
                Source = req.Source,
                CustomerNotes = Normalization.Clean(req.CustomerNotes),
                InternalNotes = user.Has(Permission.EditInternalNotes) ? Normalization.Clean(req.InternalNotes) : null,
                TaxRateBasisPoints = s.TaxRateBasisPoints,
                RemindByEmail = req.RemindByEmail,
                RemindBySms = req.RemindBySms,
                ReminderLeadHours = req.ReminderLeadHours ?? s.ReminderLeadHours,
                IsDemo = false,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedByUserId = user.UserId,
                CreatedByName = user.DisplayName,
                Version = 1,
            };
            for (var i = 0; i < segments.Count; i++)
                appt.Segments.Add(NewSegment(segments[i], services[i], req.Services[i], AppointmentLifecycle.IsBlocking(appt.Status)));
            appt.RecalculateFromSegments();

            appt.Events.Add(NewEvent(AppointmentEventType.Created, $"Booked via {SourceName(req.Source)} as {appt.Status}", null, appt.Status));
            if (conflicts.Count > 0)
                appt.Events.Add(NewEvent(AppointmentEventType.NotesUpdated, "Scheduling warnings overridden: " + string.Join("; ", conflicts.Select(x => x.Message))));

            db.Appointments.Add(appt);
            ReminderPlanner.OnBooked(db, appt, customer, s, businessName, now);
            await db.SaveChangesAsync(c);
            Audit.Add(db, user, clock, "Appointment.Created", "Appointment", appt.Id,
                $"Booked {string.Join(", ", appt.Segments.Select(x => x.ServiceName))} for {appt.Start:MMM d, h:mm tt}", customer.Id);
            await db.SaveChangesAsync(c);
            return new AppointmentRef(appt.Id, appt.Reference);
        }, ct);

        logger.LogInformation("Appointment {Reference} created", result.Reference);
        notifier.Publish(DataArea.Appointments | DataArea.Notifications);
        return result;
    }

    // ------------------------------------------------------------------ reschedule / reassign

    public async Task RescheduleAsync(RescheduleRequest req, CancellationToken ct = default)
    {
        user.Require(Permission.ManageAppointments, "reschedule appointments");
        if (req.OverrideWarnings) RequireOverride();
        var s = settings.Current;
        var businessName = settings.Business.Name;

        await writer.ExecuteAsync(async (db, c) =>
        {
            var appt = await LoadForUpdateAsync(db, req.AppointmentId, req.ExpectedVersion, c);
            if (!AppointmentLifecycle.CanReschedule(appt.Status))
                throw new ValidationException($"A {appt.Status} appointment cannot be rescheduled.");

            var segs = appt.Segments.OrderBy(x => x.SortOrder).ToList();
            if (req.NewDurationMinutes is { } newDuration)
            {
                if (segs.Count != 1) throw new ValidationException("Duration can only be adjusted directly for single-service appointments.");
                if (newDuration is < 5 or > 12 * 60) throw new ValidationException("Duration must be between 5 minutes and 12 hours.");
            }

            var planned = segs.Select((x, i) => new PlannedService(
                x.ServiceId, x.ServiceName,
                i == 0 && req.NewDurationMinutes is { } d ? d : x.DurationMinutes,
                x.PrepMinutes, x.CleanupMinutes,
                req.StaffBySegment is not null && req.StaffBySegment.TryGetValue(x.SortOrder, out var st) ? st : x.StaffMemberId,
                req.ResourceBySegment is not null && req.ResourceBySegment.TryGetValue(x.SortOrder, out var rs) ? rs : x.ResourceId)).ToList();
            var proposed = SegmentPlanner.Plan(req.NewStart, planned);
            var ctx = await SchedulingService.LoadContextAsync(db, SchedulingService.WindowStart(proposed), SchedulingService.WindowEnd(proposed),
                appt.LocationId, s, clock.Now, c);
            var conflicts = ConflictDetector.Detect(proposed, ctx, appt.Id, checkLeadTime: true);
            EnsureAcceptable(conflicts, req.OverrideWarnings, s);

            var oldStart = appt.Start;
            var oldEnd = appt.End;
            var reassigned = new List<string>();
            for (var i = 0; i < segs.Count; i++)
            {
                var seg = segs[i];
                var p = proposed[i];
                if (seg.StaffMemberId != p.StaffMemberId)
                    reassigned.Add($"{seg.ServiceName}: staff {ctx.Staff.GetValueOrDefault(seg.StaffMemberId)?.Name ?? "?"} → {ctx.Staff[p.StaffMemberId].Name}");
                if (seg.ResourceId != p.ResourceId)
                    reassigned.Add($"{seg.ServiceName}: room {Name(ctx, seg.ResourceId)} → {Name(ctx, p.ResourceId)}");
                seg.StaffMemberId = p.StaffMemberId;
                seg.ResourceId = p.ResourceId;
                seg.DurationMinutes = planned[i].DurationMinutes;
                seg.Start = p.Start;
                seg.End = p.End;
                seg.BlockStart = p.BlockStart;
                seg.BlockEnd = p.BlockEnd;
            }

            appt.RecalculateFromSegments();
            Touch(appt);

            var moved = oldStart != appt.Start || oldEnd != appt.End;
            if (moved)
                appt.Events.Add(NewEvent(AppointmentEventType.Rescheduled,
                    $"Moved from {oldStart:ddd MMM d, h:mm tt}–{oldEnd:h:mm tt} to {appt.Start:ddd MMM d, h:mm tt}–{appt.End:h:mm tt}" +
                    (string.IsNullOrWhiteSpace(req.Reason) ? "" : $" ({req.Reason.Trim()})")));
            if (reassigned.Count > 0)
                appt.Events.Add(NewEvent(AppointmentEventType.Reassigned, string.Join("; ", reassigned)));
            if (conflicts.Count > 0)
                appt.Events.Add(NewEvent(AppointmentEventType.NotesUpdated, "Scheduling warnings overridden: " + string.Join("; ", conflicts.Select(x => x.Message))));
            if (!moved && reassigned.Count == 0) return;

            if (oldStart != appt.Start)
                await ReminderPlanner.OnRescheduledAsync(db, appt, appt.Customer!, s, businessName, clock.Now, c);
            Audit.Add(db, user, clock, moved ? "Appointment.Rescheduled" : "Appointment.Reassigned", "Appointment", appt.Id,
                moved ? $"Rescheduled {appt.Reference} to {appt.Start:MMM d, h:mm tt}" : $"Reassigned {appt.Reference}", appt.CustomerId);
            await db.SaveChangesAsync(c);
        }, ct);

        notifier.Publish(DataArea.Appointments | DataArea.Notifications);
    }

    /// <summary>
    /// Validates a reschedule / reassignment exactly as <see cref="RescheduleAsync"/> would, without saving.
    /// Used for the live drag-and-drop preview in the calendar.
    /// </summary>
    public async Task<IReadOnlyList<ScheduleConflict>> PreviewRescheduleAsync(RescheduleRequest req, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var appt = await db.Appointments.AsNoTracking().Include(a => a.Segments).FirstOrDefaultAsync(a => a.Id == req.AppointmentId, ct)
                   ?? throw new NotFoundException("Appointment", req.AppointmentId);
        if (!AppointmentLifecycle.CanReschedule(appt.Status))
            return [new ScheduleConflict(ConflictType.InvalidTime, ConflictSeverity.Blocking, $"A {StatusTitle(appt.Status)} appointment cannot be moved.")];
        var segs = appt.Segments.OrderBy(x => x.SortOrder).ToList();
        var planned = segs.Select((x, i) => new PlannedService(
            x.ServiceId, x.ServiceName,
            i == 0 && req.NewDurationMinutes is { } d && segs.Count == 1 ? d : x.DurationMinutes,
            x.PrepMinutes, x.CleanupMinutes,
            req.StaffBySegment is not null && req.StaffBySegment.TryGetValue(x.SortOrder, out var st) ? st : x.StaffMemberId,
            req.ResourceBySegment is not null && req.ResourceBySegment.TryGetValue(x.SortOrder, out var rs) ? rs : x.ResourceId)).ToList();
        var proposed = SegmentPlanner.Plan(req.NewStart, planned);
        var ctx = await SchedulingService.LoadContextAsync(db, SchedulingService.WindowStart(proposed), SchedulingService.WindowEnd(proposed),
            appt.LocationId, settings.Current, clock.Now, ct);
        return ConflictDetector.Detect(proposed, ctx, appt.Id, checkLeadTime: true);
    }

    // ------------------------------------------------------------------ services

    public async Task UpdateServicesAsync(UpdateServicesRequest req, CancellationToken ct = default)
    {
        user.Require(Permission.ManageAppointments, "change appointment services");
        ValidateLines(req.Services);
        if (req.OverrideWarnings) RequireOverride();
        var s = settings.Current;

        await writer.ExecuteAsync(async (db, c) =>
        {
            var appt = await LoadForUpdateAsync(db, req.AppointmentId, req.ExpectedVersion, c);
            await db.Payments.Where(p => p.AppointmentId == appt.Id).LoadAsync(c);
            if (!AppointmentLifecycle.CanEditServices(appt.Status))
                throw new ValidationException($"Services cannot be changed once an appointment is {appt.Status}.");

            var (segments, services) = await SchedulingService.PlanAsync(db, appt.Start, req.Services, c);
            var ctx = await SchedulingService.LoadContextAsync(db, SchedulingService.WindowStart(segments), SchedulingService.WindowEnd(segments),
                appt.LocationId, s, clock.Now, c);
            var conflicts = ConflictDetector.Detect(segments, ctx, appt.Id, checkLeadTime: false);
            EnsureAcceptable(conflicts, req.OverrideWarnings, s);

            var before = string.Join(", ", appt.Segments.OrderBy(x => x.SortOrder).Select(x => x.ServiceName));
            var oldTotal = appt.TotalMinor;
            db.AppointmentSegments.RemoveRange(appt.Segments);
            appt.Segments.Clear();
            for (var i = 0; i < segments.Count; i++)
                appt.Segments.Add(NewSegment(segments[i], services[i], req.Services[i], AppointmentLifecycle.IsBlocking(appt.Status)));
            appt.RecalculateFromSegments();
            if (appt.TotalMinor < appt.PaidMinor)
                throw new ValidationException("The new total would be lower than the amount already paid. Record a refund first.");
            Touch(appt);

            var cf = settings.Currency;
            appt.Events.Add(NewEvent(AppointmentEventType.ServicesChanged,
                $"Services changed from [{before}] to [{string.Join(", ", appt.Segments.Select(x => x.ServiceName))}]; total {cf.Format(oldTotal)} → {cf.Format(appt.TotalMinor)}"));
            Audit.Add(db, user, clock, "Appointment.ServicesChanged", "Appointment", appt.Id, $"Changed services on {appt.Reference}", appt.CustomerId);
            await db.SaveChangesAsync(c);
        }, ct);

        notifier.Publish(DataArea.Appointments);
    }

    // ------------------------------------------------------------------ lifecycle

    public async Task ChangeStatusAsync(int appointmentId, int expectedVersion, AppointmentStatus target, string? reason = null, CancellationToken ct = default)
    {
        var needsManage = target is AppointmentStatus.Confirmed or AppointmentStatus.Cancelled or AppointmentStatus.NoShow or AppointmentStatus.Pending;
        if (needsManage) user.Require(Permission.ManageAppointments, $"mark appointments as {target}");
        else user.Require(Permission.ChangeAppointmentStatus, "update appointment progress");
        var s = settings.Current;
        var businessName = settings.Business.Name;

        await writer.ExecuteAsync(async (db, c) =>
        {
            var appt = await LoadForUpdateAsync(db, appointmentId, expectedVersion, c);
            EnsureCanAccess(appt);
            var now = clock.Now;

            switch (target)
            {
                case AppointmentStatus.NoShow when now < appt.Start.AddMinutes(s.NoShowGraceMinutes):
                    throw new ValidationException($"An appointment can be marked as no-show {s.NoShowGraceMinutes} minutes after its start time.");
                case AppointmentStatus.CheckedIn when DateOnly.FromDateTime(appt.Start) != DateOnly.FromDateTime(now):
                    throw new ValidationException("Customers can only be checked in on the day of the appointment.");
                case AppointmentStatus.Cancelled when string.IsNullOrWhiteSpace(reason):
                    throw new ValidationException("Enter a cancellation reason.");
            }

            var from = appt.Status;
            appt.ChangeStatus(target);
            Touch(appt);

            var description = target switch
            {
                AppointmentStatus.Cancelled => "Cancelled: " + reason!.Trim() +
                    (appt.Start > now && appt.Start - now < TimeSpan.FromHours(s.CancellationNoticeHours)
                        ? $" (late cancellation — less than {s.CancellationNoticeHours} h notice)" : ""),
                AppointmentStatus.NoShow => "Customer did not arrive" + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason.Trim()),
                _ => string.IsNullOrWhiteSpace(reason) ? $"{StatusTitle(from)} → {StatusTitle(target)}" : reason.Trim(),
            };
            if (target == AppointmentStatus.Cancelled) appt.CancellationReason = reason!.Trim();
            appt.Events.Add(NewEvent(AppointmentEventType.StatusChanged, description, from, target));

            switch (target)
            {
                case AppointmentStatus.Confirmed:
                    ReminderPlanner.OnConfirmed(db, appt, appt.Customer!, s, businessName, now);
                    break;
                case AppointmentStatus.Cancelled:
                    await ReminderPlanner.OnCancelledAsync(db, appt, appt.Customer!, s, businessName, now, c);
                    break;
                case AppointmentStatus.NoShow:
                    await ReminderPlanner.OnClosedAsync(db, appt, c);
                    break;
                case AppointmentStatus.Completed:
                    await ReminderPlanner.OnClosedAsync(db, appt, c);
                    if (s.FollowUpDaysAfterVisit > 0)
                        ReminderPlanner.QueueFollowUp(db, appt, appt.Customer!, now.Date.AddDays(s.FollowUpDaysAfterVisit).AddHours(10),
                            $"Invite {appt.Customer!.FullName} to book their next visit (last visit {appt.Reference}).", now);
                    break;
            }

            Audit.Add(db, user, clock, "Appointment." + target, "Appointment", appt.Id, $"{StatusTitle(target)}: {appt.Reference}", appt.CustomerId);
            await db.SaveChangesAsync(c);
        }, ct);

        notifier.Publish(DataArea.Appointments | DataArea.Notifications);
    }

    /// <summary>Controlled correction of a terminal status (Completed→InProgress, NoShow→Confirmed, Cancelled→Pending).</summary>
    public async Task CorrectStatusAsync(int appointmentId, int expectedVersion, string reason, CancellationToken ct = default)
    {
        user.Require(Permission.CorrectAppointmentStatus, "correct appointment statuses");
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("A reason is required for status corrections.");
        var s = settings.Current;

        await writer.ExecuteAsync(async (db, c) =>
        {
            var appt = await LoadForUpdateAsync(db, appointmentId, expectedVersion, c);
            var target = AppointmentLifecycle.CorrectionTarget(appt.Status)
                         ?? throw new ValidationException($"No correction is available for {appt.Status} appointments.");

            if (!AppointmentLifecycle.IsBlocking(appt.Status) && AppointmentLifecycle.IsBlocking(target))
            {
                // Reinstating a booking: the time slot must still be free.
                var segs = appt.Segments.OrderBy(x => x.SortOrder).Select((x, i) => new ProposedSegment(i, x.ServiceId, x.ServiceName,
                    x.StaffMemberId, x.ResourceId, x.Start, x.End, x.BlockStart, x.BlockEnd)).ToList();
                var ctx = await SchedulingService.LoadContextAsync(db, SchedulingService.WindowStart(segs), SchedulingService.WindowEnd(segs),
                    appt.LocationId, s, clock.Now, c);
                var conflicts = ConflictDetector.Detect(segs, ctx, appt.Id, checkLeadTime: false);
                if (ConflictDetector.HasBlocking(conflicts)) throw new SchedulingConflictException(conflicts);
            }

            var from = appt.Status;
            appt.ApplyCorrection();
            if (from == AppointmentStatus.Cancelled) appt.CancellationReason = null;
            Touch(appt);
            appt.Events.Add(NewEvent(AppointmentEventType.StatusCorrected, $"Status corrected from {StatusTitle(from)} to {StatusTitle(target)}: {reason.Trim()}", from, target));
            Audit.Add(db, user, clock, "Appointment.StatusCorrected", "Appointment", appt.Id, $"Corrected {appt.Reference}: {from} → {target}", appt.CustomerId);
            await db.SaveChangesAsync(c);
        }, ct);

        notifier.Publish(DataArea.Appointments);
    }

    public async Task UpdateNotesAsync(int appointmentId, int expectedVersion, string? customerNotes, string? internalNotes, CancellationToken ct = default)
    {
        user.Require(Permission.ManageAppointments, "edit appointment notes");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var appt = await LoadForUpdateAsync(db, appointmentId, expectedVersion, c);
            var changed = new List<string>();
            if (appt.CustomerNotes != Normalization.Clean(customerNotes))
            {
                appt.CustomerNotes = Normalization.Clean(customerNotes);
                changed.Add("appointment notes");
            }

            if (user.Has(Permission.EditInternalNotes) && appt.InternalNotes != Normalization.Clean(internalNotes))
            {
                appt.InternalNotes = Normalization.Clean(internalNotes);
                changed.Add("internal notes");
            }

            if (changed.Count == 0) return;
            Touch(appt);
            appt.Events.Add(NewEvent(AppointmentEventType.NotesUpdated, "Updated " + string.Join(" and ", changed)));
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Appointments);
    }

    public async Task UpdateReminderPreferencesAsync(int appointmentId, int expectedVersion, bool email, bool sms, int leadHours, CancellationToken ct = default)
    {
        user.Require(Permission.ManageAppointments, "change reminder preferences");
        if (leadHours is < 1 or > 168) throw new ValidationException("Reminder lead time must be between 1 and 168 hours.");
        var s = settings.Current;
        var businessName = settings.Business.Name;
        await writer.ExecuteAsync(async (db, c) =>
        {
            var appt = await LoadForUpdateAsync(db, appointmentId, expectedVersion, c);
            appt.RemindByEmail = email;
            appt.RemindBySms = sms;
            appt.ReminderLeadHours = leadHours;
            Touch(appt);
            var pending = await db.Reminders.Where(r => r.AppointmentId == appt.Id && r.Status == ReminderStatus.Scheduled &&
                                                        r.Type == ReminderType.Upcoming && r.Channel != ReminderChannel.InApp).ToListAsync(c);
            foreach (var r in pending) ReminderPlanner.Supersede(r, "Reminder preferences changed.");

            if (AppointmentLifecycle.IsBlocking(appt.Status) && appt.Status is not (AppointmentStatus.Completed or AppointmentStatus.InProgress))
            {
                var due = appt.Start.AddHours(-leadHours);
                if (due > clock.Now)
                {
                    var tmp = new AppSettings { LocalNotificationsEnabled = false, ReminderLeadHours = leadHours };
                    ReminderPlanner.OnBooked(db, new Appointment
                    {
                        Id = appt.Id, Reference = appt.Reference, Start = appt.Start, Status = AppointmentStatus.Pending,
                        RemindByEmail = email, RemindBySms = sms, ReminderLeadHours = leadHours, Segments = appt.Segments, IsDemo = appt.IsDemo,
                    }, appt.Customer!, tmp, businessName, clock.Now);
                }
            }

            appt.Events.Add(NewEvent(AppointmentEventType.NotesUpdated,
                $"Reminder preferences: email {(email ? "on" : "off")}, SMS {(sms ? "on" : "off")}, {leadHours} h before"));
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Appointments | DataArea.Notifications);
    }

    public async Task AddFollowUpAsync(int appointmentId, DateTime dueAt, string note, CancellationToken ct = default)
    {
        user.Require(Permission.ManageAppointments, "add follow-up tasks");
        if (dueAt <= clock.Now) throw new ValidationException("The follow-up time must be in the future.");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var appt = await db.Appointments.Include(a => a.Customer).FirstOrDefaultAsync(a => a.Id == appointmentId, c)
                       ?? throw new NotFoundException("Appointment", appointmentId);
            ReminderPlanner.QueueFollowUp(db, appt, appt.Customer!, dueAt, note, clock.Now);
            db.AppointmentEvents.Add(new AppointmentEvent
            {
                AppointmentId = appt.Id, Timestamp = clock.Now, Type = AppointmentEventType.FollowUpAdded,
                Description = $"Follow-up task due {dueAt:g}" + (string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}"),
                UserId = user.UserId, UserName = user.DisplayName,
            });
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Appointments | DataArea.Notifications);
    }

    // ------------------------------------------------------------------ queries

    public async Task<AppointmentDetailDto> GetDetailAsync(int id, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var a = await db.Appointments.AsNoTracking()
            .Include(x => x.Customer).ThenInclude(x => x!.Tags)
            .Include(x => x.Segments).ThenInclude(x => x.Service).ThenInclude(x => x!.Category)
            .Include(x => x.Segments).ThenInclude(x => x.StaffMember)
            .Include(x => x.Segments).ThenInclude(x => x.Resource)
            .Include(x => x.Payments)
            .Include(x => x.Events)
            .Include(x => x.Reminders)
            .Include(x => x.Invoices)
            .Include(x => x.Location)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Appointment", id);
        EnsureCanAccess(a);

        var cust = a.Customer!;
        var completedVisits = await db.Appointments.CountAsync(x => x.CustomerId == cust.Id && x.Status == AppointmentStatus.Completed, ct);
        var customerPaid = await db.Payments.Where(p => p.CustomerId == cust.Id)
            .SumAsync(p => p.Kind == PaymentKind.Deposit || p.Kind == PaymentKind.Payment ? p.AmountMinor : -p.AmountMinor, ct);
        var stats = await CustomerSegmentation.LoadStatsAsync(db, [cust.Id], clock.Now, ct);
        var segments = CustomerSegmentation.Classify(cust, stats.GetValueOrDefault(cust.Id), settings.Current, clock.Now);
        var contactHidden = !user.Has(Permission.ViewCustomerContact);
        var customer = new CustomerSummaryDto(cust.Id, cust.FullName, cust.Initials,
            contactHidden ? Mask(cust.Phone) : cust.Phone, contactHidden ? Mask(cust.Email) : cust.Email,
            contactHidden ? null : cust.Address, cust.Tags.Select(t => t.Tag).OrderBy(t => t).ToList(), segments,
            completedVisits, customerPaid, cust.CreatedAt, contactHidden ? null : cust.Notes, contactHidden);

        var segs = a.Segments.OrderBy(x => x.SortOrder).Select(x => new SegmentDto(x.Id, x.SortOrder, x.ServiceId, x.ServiceName,
            x.Service?.Category?.Name, x.StaffMemberId, x.StaffMember?.FullName ?? "", x.StaffMember?.Position ?? "", x.StaffMember?.ColorHex ?? "#008C95",
            x.ResourceId, x.Resource?.Name, x.Resource is null ? null : ResourceTypeNames.Display(x.Resource.Type),
            x.Start, x.End, x.DurationMinutes, x.PrepMinutes, x.CleanupMinutes, x.UnitPriceMinor, x.DiscountMinor, x.LineTotalMinor)).ToList();

        var payments = PaymentService.ToDtos(a.Payments, a.Reference, cust.FullName);
        var paid = a.PaidMinor;
        var internalVisible = user.Has(Permission.ViewInternalNotes);
        var history = a.Events.OrderBy(e => e.Timestamp).ThenBy(e => e.Id)
            .Select(e => new AppointmentEventDto(e.Timestamp, e.Type, EventTitle(e), e.Description, e.UserName)).ToList();
        var reminders = a.Reminders.OrderBy(r => r.DueAt).Select(r => new ReminderDto(r.Id, r.Channel, r.Type, r.DueAt, r.Status, r.Recipient,
            r.Subject, r.LastError, r.Attempts, r.ProcessedAt, a.Id, a.Reference)).ToList();

        return new AppointmentDetailDto(a.Id, a.Reference, a.Version, a.Status, a.Source, a.Start, a.End, a.CreatedAt, a.CreatedByName, a.UpdatedAt,
            a.Location?.Name, a.LocationId, a.CustomerNotes, internalVisible ? a.InternalNotes : null, internalVisible, a.CancellationReason,
            customer, segs, a.SubtotalMinor, a.DiscountMinor, a.TaxRateBasisPoints, a.TaxMinor, a.TotalMinor, a.DepositRequiredMinor,
            paid, a.TotalMinor - paid, PaymentService.PaymentStatus(a.TotalMinor, a.DepositRequiredMinor, a.Payments),
            payments, a.Invoices.Where(i => i.Status != InvoiceStatus.Void).OrderByDescending(i => i.IssuedAt)
                .Select(i => new InvoiceSummaryDto(i.Id, i.Number, i.IssuedAt, i.Status, i.TotalMinor)).FirstOrDefault(),
            history, reminders, a.RemindByEmail, a.RemindBySms, a.ReminderLeadHours, ComputeActions(a, paid));
    }

    public async Task<PagedResult<AppointmentListItem>> QueryAsync(AppointmentQuery q, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var query = db.Appointments.AsNoTracking().AsQueryable();
        if (q.From is not null) query = query.Where(a => a.End > q.From);
        if (q.To is not null) query = query.Where(a => a.Start < q.To);
        if (q.Status is not null) query = query.Where(a => a.Status == q.Status);
        if (q.CustomerId is not null) query = query.Where(a => a.CustomerId == q.CustomerId);
        if (q.LocationId is not null) query = query.Where(a => a.LocationId == q.LocationId);
        if (q.StaffMemberId is not null) query = query.Where(a => a.Segments.Any(s => s.StaffMemberId == q.StaffMemberId));
        query = ApplyOwnOnly(query);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var like = Normalization.LikePattern(q.Search);
            query = query.Where(a => EF.Functions.Like(a.Reference, like, "\\") || EF.Functions.Like(a.Customer!.FullName, like, "\\") ||
                                     a.Segments.Any(s => EF.Functions.Like(s.ServiceName, like, "\\")));
        }

        query = q.Descending ? query.OrderByDescending(a => a.Start) : query.OrderBy(a => a.Start);
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 500);
        var rows = await query.Skip((page - 1) * size).Take(size).Select(a => new
        {
            a.Id, a.Reference, a.Start, a.End, a.Status, a.CustomerId, Customer = a.Customer!.FullName, a.TotalMinor,
            Location = a.Location != null ? a.Location.Name : null,
            Paid = a.Payments.Sum(p => p.Kind == PaymentKind.Deposit || p.Kind == PaymentKind.Payment ? p.AmountMinor : -p.AmountMinor),
            Segs = a.Segments.OrderBy(s => s.SortOrder).Select(s => new { s.ServiceName, Staff = s.StaffMember!.FullName, Res = s.Resource != null ? s.Resource.Name : null }).ToList(),
        }).ToListAsync(ct);

        var items = rows.Select(r => new AppointmentListItem(r.Id, r.Reference, r.Start, r.End, r.Status, r.CustomerId, r.Customer,
            string.Join(", ", r.Segs.Select(s => s.ServiceName)), string.Join(", ", r.Segs.Select(s => s.Staff).Distinct()),
            r.Segs.Any(s => s.Res != null) ? string.Join(", ", r.Segs.Where(s => s.Res != null).Select(s => s.Res).Distinct()) : null,
            r.TotalMinor, r.Paid, r.Location)).ToList();
        return new PagedResult<AppointmentListItem>(items, total, page, size);
    }

    public async Task<CalendarData> GetCalendarAsync(CalendarQuery q, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var segQuery = db.AppointmentSegments.AsNoTracking().Where(s => s.Start < q.To && s.End > q.From);
        if (!q.IncludeCancelled) segQuery = segQuery.Where(s => s.IsBlocking);
        if (q.LocationId is not null) segQuery = segQuery.Where(s => s.Appointment!.LocationId == q.LocationId || s.Appointment.LocationId == null);
        if (q.StaffIds is { Count: > 0 }) segQuery = segQuery.Where(s => q.StaffIds.Contains(s.StaffMemberId));
        if (q.ServiceId is not null) segQuery = segQuery.Where(s => s.ServiceId == q.ServiceId);
        if (q.ServiceCategoryId is not null) segQuery = segQuery.Where(s => s.Service!.CategoryId == q.ServiceCategoryId);
        if (!user.Has(Permission.ViewAllAppointments))
        {
            var own = user.StaffMemberId ?? -1;
            segQuery = segQuery.Where(s => s.StaffMemberId == own);
        }

        var raw = await segQuery.Select(s => new
        {
            s.AppointmentId, s.Id, s.Appointment!.Reference, s.Appointment.Version, s.Appointment.Status, s.Appointment.CustomerId,
            Customer = s.Appointment.Customer!.FullName, s.ServiceName, CategoryColor = s.Service!.Category != null ? s.Service.Category.ColorHex : null,
            s.StaffMemberId, Staff = s.StaffMember!.FullName, StaffColor = s.StaffMember.ColorHex, s.ResourceId,
            Resource = s.Resource != null ? s.Resource.Name : null, s.Start, s.End, s.BlockStart, s.BlockEnd, s.ServiceId, s.SortOrder,
            ApptStart = s.Appointment.Start, ApptEnd = s.Appointment.End, Count = s.Appointment.Segments.Count,
        }).ToListAsync(ct);

        // Conflict indicators: re-run the rule engine against each displayed appointment.
        var ctx = await SchedulingService.LoadContextAsync(db, q.From.Date, q.To.Date.AddDays(1), q.LocationId, settings.Current, clock.Now, ct);
        var conflicted = new HashSet<int>();
        foreach (var g in raw.Where(r => AppointmentLifecycle.IsBlocking(r.Status) && r.Status != AppointmentStatus.Completed).GroupBy(r => r.AppointmentId))
        {
            var proposal = g.OrderBy(x => x.SortOrder).Select((x, i) => new ProposedSegment(i, x.ServiceId, x.ServiceName, x.StaffMemberId, x.ResourceId,
                x.Start, x.End, x.BlockStart, x.BlockEnd)).ToList();
            var conflicts = ConflictDetector.Detect(proposal, ctx, g.Key, checkLeadTime: false);
            if (conflicts.Any(c => c.Type is not (ConflictType.ServiceInactive or ConflictType.StaffWrongLocation))) conflicted.Add(g.Key);
        }

        var segments = raw.Select(r => new CalendarSegment(r.AppointmentId, r.Id, r.Reference, r.Version, r.Status, r.CustomerId, r.Customer,
            r.ServiceName, r.CategoryColor, r.StaffMemberId, r.Staff, r.StaffColor, r.ResourceId, r.Resource, r.Start, r.End,
            r.ApptStart, r.ApptEnd, r.Count, conflicted.Contains(r.AppointmentId), r.SortOrder)).OrderBy(r => r.Start).ToList();

        var staffQuery = db.Staff.AsNoTracking().Where(s => !s.IsArchived && s.EmploymentStatus != EmploymentStatus.Inactive);
        if (q.LocationId is not null) staffQuery = staffQuery.Where(s => s.LocationId == q.LocationId || s.LocationId == null);
        if (q.StaffIds is { Count: > 0 }) staffQuery = staffQuery.Where(s => q.StaffIds.Contains(s.Id));
        if (!user.Has(Permission.ViewAllAppointments)) staffQuery = staffQuery.Where(s => s.Id == (user.StaffMemberId ?? -1));
        var staff = await staffQuery.OrderBy(s => s.FullName).Select(s => new CalendarColumnInfo(s.Id, s.FullName, s.Position, s.ColorHex)).ToListAsync(ct);

        var resQuery = db.Resources.AsNoTracking().Where(r => !r.IsArchived);
        if (q.LocationId is not null) resQuery = resQuery.Where(r => r.LocationId == q.LocationId || r.LocationId == null);
        var resources = (await resQuery.OrderBy(r => r.Name).ToListAsync(ct))
            .Select(r => new CalendarColumnInfo(r.Id, r.Name, ResourceTypeNames.Display(r.Type) + (r.IsActive ? "" : " · blocked"), "#008C95")).ToList();

        var blocks = BuildBlocks(ctx, q, staff.Select(s => s.Id).ToHashSet(), resources.Select(r => r.Id).ToHashSet());
        return new CalendarData(segments, blocks, staff, resources);
    }

    private List<CalendarBlock> BuildBlocks(SchedulingContext ctx, CalendarQuery q, HashSet<int> staffIds, HashSet<int> resourceIds)
    {
        var s = settings.Current;
        var blocks = new List<CalendarBlock>();
        for (var day = q.From.Date; day < q.To; day = day.AddDays(1))
        {
            var dayStart = day.AddHours(s.CalendarStartHour);
            var dayEnd = day.AddHours(s.CalendarEndHour);
            if (ctx.Closures.TryGetValue(DateOnly.FromDateTime(day), out var closure))
                blocks.Add(new CalendarBlock(CalendarBlockKind.Closed, dayStart, dayEnd, null, null, "Closed · " + closure));

            foreach (var st in ctx.Staff.Values.Where(x => staffIds.Contains(x.Id)))
            {
                var shifts = st.Shifts.Where(x => x.Day == day.DayOfWeek).OrderBy(x => x.Start).ToList();
                var cursor = dayStart;
                foreach (var sh in shifts)
                {
                    var shStart = day + sh.Start.ToTimeSpan();
                    if (shStart > cursor) blocks.Add(new CalendarBlock(CalendarBlockKind.OffShift, cursor, shStart, st.Id, null, "Not working"));
                    if (sh.BreakStart is { } bs && sh.BreakEnd is { } be && be > bs)
                        blocks.Add(new CalendarBlock(CalendarBlockKind.Break, day + bs.ToTimeSpan(), day + be.ToTimeSpan(), st.Id, null, "Break"));
                    var shEnd = day + sh.End.ToTimeSpan();
                    if (shEnd > cursor) cursor = shEnd;
                }

                if (cursor < dayEnd) blocks.Add(new CalendarBlock(CalendarBlockKind.OffShift, cursor, dayEnd, st.Id, null, shifts.Count == 0 ? "Day off" : "Not working"));
                foreach (var off in st.TimeOff.Where(o => o.Start < day.AddDays(1) && o.End > day))
                    blocks.Add(new CalendarBlock(CalendarBlockKind.TimeOff, Max(off.Start, dayStart), Min(off.End, dayEnd), st.Id, null, off.Description));
            }

            foreach (var r in ctx.Resources.Values.Where(x => resourceIds.Contains(x.Id)))
            {
                foreach (var m in r.Maintenance.Where(m => m.Start < day.AddDays(1) && m.End > day))
                    blocks.Add(new CalendarBlock(CalendarBlockKind.Maintenance, Max(m.Start, dayStart), Min(m.End, dayEnd), null, r.Id, "Room unavailable · " + m.Description));
                if (!r.IsBookable) blocks.Add(new CalendarBlock(CalendarBlockKind.Maintenance, dayStart, dayEnd, null, r.Id, "Blocked"));
            }
        }

        return blocks.Where(b => b.End > b.Start).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Appointment> LoadForUpdateAsync(IAppDbContext db, int id, int expectedVersion, CancellationToken ct)
    {
        var appt = await db.Appointments.Include(a => a.Segments).Include(a => a.Customer).FirstOrDefaultAsync(a => a.Id == id, ct)
                   ?? throw new NotFoundException("Appointment", id);
        if (appt.Version != expectedVersion)
            throw new ConcurrencyConflictException($"Appointment {appt.Reference} was changed by someone else. Reload it and try again.");
        return appt;
    }

    private IQueryable<Appointment> ApplyOwnOnly(IQueryable<Appointment> query)
    {
        if (user.Has(Permission.ViewAllAppointments)) return query;
        var own = user.StaffMemberId ?? -1;
        return query.Where(a => a.Segments.Any(s => s.StaffMemberId == own));
    }

    private void EnsureCanAccess(Appointment a)
    {
        if (user.Has(Permission.ViewAllAppointments)) return;
        if (user.StaffMemberId is { } sid && a.Segments.Any(s => s.StaffMemberId == sid)) return;
        throw new PermissionDeniedException("view this appointment");
    }

    private void RequireOverride()
    {
        user.Require(Permission.OverrideSchedulingWarnings, "override scheduling warnings");
    }

    private static void EnsureAcceptable(IReadOnlyList<ScheduleConflict> conflicts, bool overrideWarnings, AppSettings s)
    {
        if (conflicts.Count == 0) return;
        if (ConflictDetector.HasBlocking(conflicts) || !overrideWarnings || !s.AllowOvertimeOverride)
            throw new SchedulingConflictException(conflicts);
    }

    private static void ValidateLines(IReadOnlyList<ServiceLineRequest> lines)
    {
        if (lines.Count == 0) throw new ValidationException("Select at least one service.");
        var errors = new List<string>();
        foreach (var l in lines)
        {
            if (l.StaffMemberId <= 0) errors.Add("Assign a staff member to every service.");
            if (l.UnitPriceMinor is < 0) errors.Add("Prices cannot be negative.");
            if (l.DiscountMinor < 0) errors.Add("Discounts cannot be negative.");
            if (l.UnitPriceMinor is { } p && l.DiscountMinor > p) errors.Add("A discount cannot exceed the service price.");
        }

        if (errors.Count > 0) throw new ValidationException(errors.Distinct().ToList());
    }

    private static AppointmentSegment NewSegment(ProposedSegment p, Service svc, ServiceLineRequest line, bool blocking)
    {
        var price = line.UnitPriceMinor ?? svc.PriceMinor;
        if (line.DiscountMinor > price) throw new ValidationException($"The discount on {svc.Name} exceeds its price.");
        return new AppointmentSegment
        {
            SortOrder = p.Index,
            ServiceId = svc.Id,
            ServiceName = svc.Name,
            DurationMinutes = svc.DurationMinutes,
            PrepMinutes = svc.PrepMinutes,
            CleanupMinutes = svc.CleanupMinutes,
            UnitPriceMinor = price,
            DiscountMinor = line.DiscountMinor,
            DepositMinor = svc.DepositMinor,
            StaffMemberId = p.StaffMemberId,
            ResourceId = p.ResourceId,
            Start = p.Start,
            End = p.End,
            BlockStart = p.BlockStart,
            BlockEnd = p.BlockEnd,
            IsBlocking = blocking,
        };
    }

    private void Touch(Appointment a)
    {
        a.UpdatedAt = clock.Now;
        a.Version++;
    }

    private AppointmentEvent NewEvent(AppointmentEventType type, string description, AppointmentStatus? from = null, AppointmentStatus? to = null) => new()
    {
        Timestamp = clock.Now,
        Type = type,
        FromStatus = from,
        ToStatus = to,
        Description = description,
        UserId = user.UserId,
        UserName = user.DisplayName,
    };

    private AppointmentActions ComputeActions(Appointment a, long paid)
    {
        var manage = user.Has(Permission.ManageAppointments);
        var own = user.Has(Permission.ViewAllAppointments) || (user.StaffMemberId is { } sid && a.Segments.Any(s => s.StaffMemberId == sid));
        var progress = user.Has(Permission.ChangeAppointmentStatus) && own;
        var now = clock.Now;
        bool Can(AppointmentStatus to) => AppointmentLifecycle.CanTransition(a.Status, to);
        var hasRefundable = a.Payments.Any(p => p.IsInflow) && paid > 0;
        return new AppointmentActions(
            CanConfirm: manage && Can(AppointmentStatus.Confirmed),
            CanCheckIn: progress && Can(AppointmentStatus.CheckedIn) && DateOnly.FromDateTime(a.Start) == DateOnly.FromDateTime(now),
            CanStart: progress && Can(AppointmentStatus.InProgress),
            CanComplete: progress && Can(AppointmentStatus.Completed),
            CanCancel: manage && Can(AppointmentStatus.Cancelled),
            CanNoShow: manage && Can(AppointmentStatus.NoShow) && now >= a.Start.AddMinutes(settings.Current.NoShowGraceMinutes),
            CanReschedule: manage && AppointmentLifecycle.CanReschedule(a.Status),
            CanEditServices: manage && AppointmentLifecycle.CanEditServices(a.Status),
            CanEditNotes: manage,
            CanEditInternalNotes: user.Has(Permission.EditInternalNotes),
            CanRecordPayment: user.Has(Permission.RecordPayments) && AppointmentLifecycle.AcceptsPayments(a.Status) && a.TotalMinor - paid > 0,
            CanRefund: user.Has(Permission.RefundPayments) && hasRefundable,
            CanReverse: user.Has(Permission.ReversePayments) && hasRefundable,
            CorrectionTarget: user.Has(Permission.CorrectAppointmentStatus) ? AppointmentLifecycle.CorrectionTarget(a.Status) : null);
    }

    public static string StatusTitle(AppointmentStatus s) => s switch
    {
        AppointmentStatus.CheckedIn => "Checked In",
        AppointmentStatus.InProgress => "In Progress",
        AppointmentStatus.NoShow => "No Show",
        _ => s.ToString(),
    };

    private static string EventTitle(AppointmentEvent e) => e.Type switch
    {
        AppointmentEventType.Created => "Appointment Booked",
        AppointmentEventType.StatusChanged => e.ToStatus switch
        {
            AppointmentStatus.Confirmed => "Confirmed",
            AppointmentStatus.CheckedIn => "Checked In",
            AppointmentStatus.InProgress => "Service Started",
            AppointmentStatus.Completed => "Service Completed",
            AppointmentStatus.Cancelled => "Cancelled",
            AppointmentStatus.NoShow => "Marked No-Show",
            _ => "Status Changed",
        },
        AppointmentEventType.StatusCorrected => "Status Corrected",
        AppointmentEventType.Rescheduled => "Rescheduled",
        AppointmentEventType.ServicesChanged => "Services Changed",
        AppointmentEventType.Reassigned => "Staff / Room Reassigned",
        AppointmentEventType.NotesUpdated => "Updated",
        AppointmentEventType.PaymentRecorded => "Payment Recorded",
        AppointmentEventType.RefundRecorded => "Refund Recorded",
        AppointmentEventType.PaymentReversed => "Payment Reversed",
        AppointmentEventType.InvoiceIssued => "Invoice Issued",
        AppointmentEventType.ReminderScheduled => "Message Scheduled",
        AppointmentEventType.ReminderSent => "Message Sent",
        AppointmentEventType.ReminderNotSent => "Message Not Sent",
        AppointmentEventType.ReminderFailed => "Message Failed",
        AppointmentEventType.FollowUpAdded => "Follow-up Added",
        _ => e.Type.ToString(),
    };

    public static string SourceName(BookingSource s) => s switch
    {
        BookingSource.WalkIn => "walk-in",
        BookingSource.Phone => "phone",
        BookingSource.Online => "online booking",
        BookingSource.Other => "other",
        _ => "front desk",
    };

    private static string Name(SchedulingContext ctx, int? resourceId) =>
        resourceId is { } id && ctx.Resources.TryGetValue(id, out var r) ? r.Name : "none";

    private static string? Mask(string? value) => string.IsNullOrEmpty(value) ? value : "•••••" + value[^Math.Min(2, value.Length)..];
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
