using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Notifications;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Infrastructure.Seeding;

/// <summary>
/// Creates a clearly labelled demo workspace with fictional people (example.com e-mails, 555 phone numbers).
/// Dates are relative to today. Seeding is idempotent and never modifies records that are not flagged IsDemo.
/// </summary>
public sealed class DemoDataService(
    IWriteExecutor writer,
    IAppDbContextFactory dbFactory,
    SettingsService settings,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier,
    ILogger<DemoDataService> logger) : IDemoDataService
{
    public async Task<bool> IsDemoSeededAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        return await db.Staff.AnyAsync(s => s.IsDemo, ct);
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var seeded = await writer.ExecuteAsync(async (db, c) =>
        {
            if (await db.Staff.AnyAsync(s => s.IsDemo, c)) return false;
            await new Builder(db, settings, clock.Now).BuildAsync(c);
            return true;
        }, ct);
        if (seeded)
        {
            logger.LogInformation("Demo workspace seeded");
            await settings.LoadAsync(ct);
            notifier.Publish(DataArea.All);
        }
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        user.Require(Permission.ManageSettings, "reset demo data");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var keepAppointments = db.Payments.Where(p => !p.IsDemo && p.AppointmentId != null).Select(p => p.AppointmentId!.Value);
            var apptIds = await db.Appointments.Where(a => a.IsDemo && !keepAppointments.Contains(a.Id)).Select(a => a.Id).ToListAsync(c);

            await db.Notifications.Where(n => n.IsDemo || (n.AppointmentId != null && apptIds.Contains(n.AppointmentId.Value))).ExecuteDeleteAsync(c);
            await db.Reminders.Where(r => r.IsDemo || (r.AppointmentId != null && apptIds.Contains(r.AppointmentId.Value))).ExecuteDeleteAsync(c);
            await db.AuditLogs.Where(l => l.IsDemo).ExecuteDeleteAsync(c);
            await db.Payments.Where(p => p.IsDemo && p.AppointmentId != null && apptIds.Contains(p.AppointmentId.Value) && p.ReversesPaymentId != null).ExecuteDeleteAsync(c);
            await db.Payments.Where(p => p.IsDemo && p.AppointmentId != null && apptIds.Contains(p.AppointmentId.Value)).ExecuteDeleteAsync(c);
            await db.Invoices.Where(i => apptIds.Contains(i.AppointmentId)).ExecuteDeleteAsync(c);
            await db.Appointments.Where(a => apptIds.Contains(a.Id)).ExecuteDeleteAsync(c);

            await db.Customers.Where(x => x.IsDemo && !db.Appointments.Any(a => a.CustomerId == x.Id) && !db.Payments.Any(p => p.CustomerId == x.Id)).ExecuteDeleteAsync(c);
            await db.Staff.Where(x => x.IsDemo && !db.AppointmentSegments.Any(s => s.StaffMemberId == x.Id) && !db.Users.Any(u => u.StaffMemberId == x.Id)).ExecuteDeleteAsync(c);
            await db.Services.Where(x => x.IsDemo && !db.AppointmentSegments.Any(s => s.ServiceId == x.Id)).ExecuteDeleteAsync(c);
            await db.Resources.Where(x => x.IsDemo && !db.AppointmentSegments.Any(s => s.ResourceId == x.Id)).ExecuteDeleteAsync(c);
            await db.ServiceCategories.Where(x => x.IsDemo && !db.Services.Any(s => s.CategoryId == x.Id)).ExecuteDeleteAsync(c);

            if (!await db.Staff.AnyAsync(s => s.IsDemo, c)) await new Builder(db, settings, clock.Now).BuildAsync(c);
            Audit.Add(db, user, clock, "Demo.Reset", "Business", null, "Demo data reset");
            await db.SaveChangesAsync(c);
        }, ct);
        await settings.LoadAsync(ct);
        notifier.Publish(DataArea.All);
    }

    private sealed class Builder(IAppDbContext db, SettingsService settings, DateTime now)
    {
        private readonly Random _rng = new(20250421);
        private readonly int _decimals = settings.Business.CurrencyDecimals;
        private readonly AppSettings _s = settings.Current;
        private readonly Dictionary<object, List<TimeInterval>> _staffBusy = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, List<TimeInterval>> _resourceBusy = new(ReferenceEqualityComparer.Instance);

        private long P(decimal major) => Money.FromMajor(_decimals == 0 ? major * 1000 : major, _decimals);

        public async Task BuildAsync(CancellationToken ct)
        {
            var business = await db.Businesses.FirstOrDefaultAsync(ct);
            if (business is not null) business.IsDemoWorkspace = true;
            var hours = await db.BusinessHours.ToListAsync(ct);
            if (hours.Count == 0)
            {
                foreach (var d in Enum.GetValues<DayOfWeek>())
                    db.BusinessHours.Add(new BusinessHours { DayOfWeek = d, IsOpen = d != DayOfWeek.Sunday, OpenTime = new(9, 0), CloseTime = d == DayOfWeek.Saturday ? new(17, 0) : new(19, 0) });
                await db.SaveChangesAsync(ct);
                hours = await db.BusinessHours.ToListAsync(ct);
            }

            // Locations (reuse demo locations if they survived a reset)
            var downtown = await db.Locations.FirstOrDefaultAsync(l => l.IsDemo && l.Name == "Downtown Spa & Salon", ct)
                           ?? Add(new Location { Name = "Downtown Spa & Salon", Address = "100 Example Avenue, Suite 1", Phone = "(555) 010-0001", IsDemo = true });
            var riverside = await db.Locations.FirstOrDefaultAsync(l => l.IsDemo && l.Name == "Riverside Studio", ct)
                            ?? Add(new Location { Name = "Riverside Studio", Address = "200 Sample Street", Phone = "(555) 010-0002", IsDemo = true });

            // Categories and services
            var cats = new Dictionary<string, ServiceCategory>();
            var catDefs = new (string Name, string Color)[] { ("Hair", "#3689E6"), ("Nails", "#D946EF"), ("Facial Care", "#E45662"), ("Massage", "#15A77F"), ("Spa", "#008C95"), ("Wellness", "#E7A23A") };
            for (var i = 0; i < catDefs.Length; i++)
                cats[catDefs[i].Name] = Add(new ServiceCategory { Name = catDefs[i].Name, ColorHex = catDefs[i].Color, SortOrder = i, IsDemo = true });

            var svc = new Dictionary<string, Service>();
            void S(string name, string cat, int dur, decimal price, int prep, int clean, decimal deposit, string desc, params ResourceType[] res)
            {
                var s = Add(new Service
                {
                    Name = name, Category = cats[cat], DurationMinutes = dur, PriceMinor = P(price), PrepMinutes = prep, CleanupMinutes = clean,
                    DepositMinor = P(deposit), Description = desc, IsActive = true, IsDemo = true, CreatedAt = now.AddDays(-200), Version = 1,
                });
                foreach (var r in res) s.ResourceRequirements.Add(new ServiceResourceRequirement { ResourceType = r });
                svc[name] = s;
            }

            S("Men's Haircut", "Hair", 45, 35, 0, 5, 0, "Classic cut, wash and style.", ResourceType.HairStation);
            S("Women's Haircut & Style", "Hair", 60, 65, 0, 10, 0, "Consultation, cut and blow-dry.", ResourceType.HairStation);
            S("Hair Color", "Hair", 120, 140, 10, 10, 40, "Full colour with toner.", ResourceType.HairStation);
            S("Beard Trim", "Hair", 30, 20, 0, 5, 0, "Beard shaping and hot towel.", ResourceType.HairStation);
            S("Blow Dry", "Hair", 45, 45, 0, 5, 0, "Wash and blow-dry styling.", ResourceType.HairStation);
            S("Gel Manicure", "Nails", 60, 45, 0, 10, 0, "Long-lasting gel polish.", ResourceType.NailStation);
            S("Classic Pedicure", "Nails", 60, 50, 0, 10, 0, "Soak, scrub and polish.", ResourceType.NailStation);
            S("Nail Art", "Nails", 45, 35, 0, 10, 0, "Custom nail designs.", ResourceType.NailStation);
            S("Nail Repair", "Nails", 30, 20, 0, 5, 0, "Single nail repair.", ResourceType.NailStation);
            S("Facial Treatment", "Facial Care", 45, 90, 5, 10, 0, "Cleansing facial tailored to skin type.", ResourceType.TreatmentRoom);
            S("Skin Consultation", "Facial Care", 30, 40, 0, 5, 0, "Skin analysis and treatment plan.", ResourceType.TreatmentRoom);
            S("Chemical Peel", "Facial Care", 60, 120, 10, 10, 30, "Exfoliating peel.", ResourceType.TreatmentRoom);
            S("Hydrafacial", "Facial Care", 60, 150, 5, 10, 50, "Hydrating multi-step facial.", ResourceType.TreatmentRoom);
            S("Anti-Aging Facial", "Facial Care", 60, 130, 5, 10, 0, "Firming facial treatment.", ResourceType.TreatmentRoom);
            S("Microneedling", "Facial Care", 60, 180, 10, 15, 50, "Collagen induction therapy.", ResourceType.TreatmentRoom);
            S("Acne Treatment", "Facial Care", 45, 85, 5, 10, 0, "Targeted acne care.", ResourceType.TreatmentRoom);
            S("Deep Tissue Massage", "Massage", 60, 120, 0, 15, 0, "Firm pressure massage.", ResourceType.MassageRoom);
            S("Swedish Massage", "Massage", 60, 100, 0, 15, 0, "Relaxing full-body massage.", ResourceType.MassageRoom);
            S("Hot Stone Massage", "Massage", 75, 135, 10, 15, 30, "Heated basalt stone massage.", ResourceType.MassageRoom);
            S("Back & Neck Massage", "Massage", 45, 75, 0, 10, 0, "Focused upper-body massage.", ResourceType.MassageRoom);
            S("Sports Massage", "Massage", 60, 110, 0, 15, 0, "Recovery-focused massage.", ResourceType.MassageRoom);
            S("Body Scrub", "Spa", 45, 80, 5, 15, 0, "Exfoliating body treatment.", ResourceType.TreatmentRoom, ResourceType.MassageRoom);
            S("Aromatherapy", "Spa", 60, 95, 5, 15, 0, "Essential oil massage.", ResourceType.MassageRoom);
            S("Physiotherapy Session", "Wellness", 45, 95, 0, 10, 0, "Assessment and treatment.", ResourceType.TreatmentRoom);
            S("LED Light Therapy", "Wellness", 30, 60, 5, 5, 0, "Light therapy for skin health.", ResourceType.TreatmentRoom);

            // Resources
            var resources = new List<Resource>
            {
                R("Treatment Room 1", ResourceType.TreatmentRoom, downtown), R("Treatment Room 2", ResourceType.TreatmentRoom, downtown),
                R("Massage Room A", ResourceType.MassageRoom, downtown), R("Massage Room B", ResourceType.MassageRoom, downtown),
                R("Hair Station 1", ResourceType.HairStation, downtown), R("Hair Station 2", ResourceType.HairStation, downtown),
                R("Nail Station 1", ResourceType.NailStation, downtown), R("Treatment Room 3", ResourceType.TreatmentRoom, riverside),
                R("Spa Suite", ResourceType.MassageRoom, riverside), R("Nail Station 2", ResourceType.NailStation, riverside),
            };

            // Staff
            var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
            var tueSat = new[] { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday };
            var staff = new List<StaffMember>
            {
                St("Sarah Kim", "Massage Therapist", "#008C95", downtown, weekdays, 9, 18, 12, ["Deep Tissue Massage", "Swedish Massage", "Hot Stone Massage", "Back & Neck Massage", "Aromatherapy", "Body Scrub"]),
                St("Michael Brown", "Hair Stylist", "#3689E6", downtown, tueSat, 10, 19, 13, ["Men's Haircut", "Women's Haircut & Style", "Hair Color", "Beard Trim", "Blow Dry"]),
                St("Emily Davis", "Nail Technician", "#D946EF", downtown, tueSat, 9, 17, 12, ["Gel Manicure", "Classic Pedicure", "Nail Art", "Nail Repair"]),
                St("Ashley Carter", "Esthetician", "#E45662", downtown, weekdays, 9, 18, 13, ["Facial Treatment", "Skin Consultation", "Anti-Aging Facial", "Acne Treatment", "LED Light Therapy", "Hydrafacial", "Body Scrub"]),
                St("Chris Wilson", "Physiotherapist", "#15A77F", downtown, weekdays, 8, 16, 12, ["Physiotherapy Session", "Sports Massage", "Back & Neck Massage", "Deep Tissue Massage"]),
                St("Olivia Reed", "Skin Specialist", "#8B5CF6", riverside, tueSat, 9, 17, 12, ["Facial Treatment", "Skin Consultation", "Chemical Peel", "Microneedling", "Hydrafacial", "Anti-Aging Facial"]),
                St("Daniel Park", "Barber", "#E7A23A", riverside, weekdays, 10, 19, 14, ["Men's Haircut", "Beard Trim", "Blow Dry", "Women's Haircut & Style"]),
                St("Mia Turner", "Spa Therapist", "#0EA5E9", riverside, tueSat, 9, 17, 13, ["Gel Manicure", "Classic Pedicure", "Body Scrub", "Aromatherapy", "Swedish Massage"]),
            };

            // Saturday closes at 17:00: trim Saturday shifts accordingly.
            foreach (var sh in staff.SelectMany(s => s.Shifts).Where(x => x.DayOfWeek == DayOfWeek.Saturday && x.EndTime > new TimeOnly(17, 0)))
            {
                sh.StartTime = new TimeOnly(9, 0);
                sh.EndTime = new TimeOnly(17, 0);
                sh.BreakStart = new TimeOnly(12, 0);
                sh.BreakEnd = new TimeOnly(12, 30);
            }

            // Time off and maintenance (relative dates)
            var today = now.Date;
            var nextMonday = today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7 + 7);
            staff[2].TimeOff.Add(new StaffTimeOff { Start = nextMonday.AddDays(2), End = nextMonday.AddDays(4), Type = TimeOffType.Leave, Reason = "Annual leave" });
            staff[4].TimeOff.Add(new StaffTimeOff { Start = today.AddDays(-9), End = today.AddDays(-8), Type = TimeOffType.Sick, Reason = "Sick day" });
            foreach (var off in staff.SelectMany(s => s.TimeOff.Select(t => (s, t))))
                Busy(_staffBusy, off.s, new TimeInterval(off.t.Start, off.t.End));
            var tomorrow = today.AddDays(1);
            resources[3].Maintenance.Add(new ResourceMaintenance { Start = tomorrow.AddHours(12.5), End = tomorrow.AddHours(14), Reason = "Equipment maintenance" });
            resources[7].Maintenance.Add(new ResourceMaintenance { Start = nextMonday.AddDays(1).AddHours(9), End = nextMonday.AddDays(1).AddHours(13), Reason = "Deep cleaning" });
            foreach (var r in resources)
            foreach (var m in r.Maintenance) Busy(_resourceBusy, r, new TimeInterval(m.Start, m.End));

            // Customers
            string[] first = ["Ava", "Liam", "Noah", "Emma", "Olivia", "James", "Sophia", "Lucas", "Isabella", "Mason", "Amelia", "Ethan", "Harper", "Logan", "Evelyn",
                "Jack", "Abigail", "Henry", "Ella", "Samuel", "Grace", "Caleb", "Chloe", "Owen", "Zoe"];
            string[] last = ["Thompson", "Johnson", "Garcia", "Martin", "Parker", "Lee", "Walker", "Young", "Harris", "Clark", "Lewis", "Robinson", "Wright",
                "Scott", "Green", "Baker", "Adams", "Nelson", "Hill", "Campbell", "Mitchell", "Roberts", "Turner", "Phillips", "Evans"];
            string[] tagPool = ["Prefers morning", "Sensitive skin", "Text reminders", "Loyal customer", "Prefers quiet room", "Allergic to nuts"];
            var customers = new List<Customer>();
            for (var i = 0; i < 50; i++)
            {
                var name = $"{first[i % first.Length]} {last[(i * 7 + i / first.Length) % last.Length]}";
                var created = i < 8 ? now.AddDays(-_rng.Next(1, 20)) : now.AddDays(-_rng.Next(60, 400));
                var c = Add(new Customer
                {
                    FullName = name,
                    Phone = $"(555) 01{i / 10}-{1000 + i * 37:0000}",
                    Email = $"{name.ToLowerInvariant().Replace(' ', '.').Replace("'", "")}{i}@example.com",
                    AllowEmail = true, AllowSms = i % 3 == 0, CreatedAt = created, UpdatedAt = created, Version = 1, IsDemo = true,
                    Notes = i % 5 == 0 ? "Fictional demo customer." : null,
                    PreferredStaff = i % 6 == 0 ? staff[i % staff.Count] : null,
                });
                c.NormalizedPhone = Normalization.Phone(c.Phone);
                c.NormalizedEmail = Normalization.Email(c.Email);
                if (i % 9 == 0) c.Tags.Add(new CustomerTag { Tag = "VIP" });
                if (i % 4 == 0) c.Tags.Add(new CustomerTag { Tag = tagPool[i % tagPool.Length] });
                if (i % 7 == 0) c.Tags.Add(new CustomerTag { Tag = tagPool[(i + 3) % tagPool.Length] });
                customers.Add(c);
            }

            await db.SaveChangesAsync(ct);
            foreach (var c in customers)
                db.AuditLogs.Add(new AuditLog { Timestamp = c.CreatedAt, UserName = "Front Desk", Action = "Customer.Created", EntityType = "Customer", EntityId = c.Id, Summary = $"Customer {c.FullName} added", CustomerId = c.Id, IsDemo = true });

            // Appointments: fill shifts from 4 weeks ago to 2 weeks ahead without any staff/resource overlap.
            var businessName = settings.Business.Name;
            var count = 0;
            for (var day = today.AddDays(-28); day <= today.AddDays(14); day = day.AddDays(1))
            {
                var bh = hours.FirstOrDefault(h => h.DayOfWeek == day.DayOfWeek);
                if (bh is null || !bh.IsOpen) continue;
                var density = day < today ? 0.78 : day == today ? 0.85 : Math.Max(0.25, 0.75 - (day - today).TotalDays * 0.035);
                foreach (var st in staff)
                {
                    var shift = st.Shifts.FirstOrDefault(s => s.DayOfWeek == day.DayOfWeek);
                    if (shift is null) continue;
                    var cursor = day + shift.StartTime.ToTimeSpan() + TimeSpan.FromMinutes(15 * _rng.Next(0, 3));
                    var shiftEnd = day + shift.EndTime.ToTimeSpan();
                    while (cursor < shiftEnd)
                    {
                        var qualified = st.Skills.Select(k => k.Service!).ToList();
                        var first1 = qualified[_rng.Next(qualified.Count)];
                        var plan = new List<Service> { first1 };
                        if (_rng.NextDouble() < 0.12)
                        {
                            var second = qualified[_rng.Next(qualified.Count)];
                            if (second != first1) plan.Add(second);
                        }

                        DateTime? placed = _rng.NextDouble() < density
                            ? await TryBookAsync(plan, st, cursor + TimeSpan.FromMinutes(plan[0].PrepMinutes), shift, customers, resources, businessName, ct)
                            : null;
                        if (placed is { } end)
                        {
                            count++;
                            cursor = end + TimeSpan.FromMinutes(15 * _rng.Next(0, 3));
                        }
                        else cursor += TimeSpan.FromMinutes(30);
                    }
                }
            }

            db.Notifications.Add(new Notification
            {
                CreatedAt = now, Kind = NotificationKind.Info, IsDemo = true, Title = "Demo workspace ready",
                Message = $"{count} fictional appointments, 50 customers, 8 staff members and 25 services were created. Reset or remove demo data in Settings → Data.",
            });
            await db.SaveChangesAsync(ct);
        }

        private async Task<DateTime?> TryBookAsync(List<Service> plan, StaffMember st, DateTime start, StaffShift shift, List<Customer> customers,
            List<Resource> resources, string businessName, CancellationToken ct)
        {
            var planned = plan.Select(s => new PlannedService(s.Id, s.Name, s.DurationMinutes, s.PrepMinutes, s.CleanupMinutes, st.Id, null)).ToList();
            var segs = SegmentPlanner.Plan(start, planned);
            var day = start.Date;
            var shiftIv = new TimeInterval(day + shift.StartTime.ToTimeSpan(), day + shift.EndTime.ToTimeSpan());
            var breakIv = shift.BreakStart is { } bs && shift.BreakEnd is { } be ? new TimeInterval(day + bs.ToTimeSpan(), day + be.ToTimeSpan()) : (TimeInterval?)null;
            var chosen = new List<Resource?>();
            for (var i = 0; i < segs.Count; i++)
            {
                var iv = segs[i].Blocked;
                if (!shiftIv.Contains(iv) || (breakIv is { } b && b.Overlaps(iv)) || IsBusy(_staffBusy, st, iv)) return null;
                var types = plan[i].ResourceRequirements.Select(r => r.ResourceType).ToList();
                if (types.Count == 0)
                {
                    chosen.Add(null);
                    continue;
                }

                var res = resources.FirstOrDefault(r => types.Contains(r.Type) && r.Location == st.Location && !IsBusy(_resourceBusy, r, iv));
                if (res is null) return null;
                chosen.Add(res);
            }

            var apptCreated = Min(segs[0].Start.AddDays(-_rng.Next(1, 15)).AddHours(-_rng.Next(0, 8)), now.AddMinutes(-_rng.Next(30, 600)));
            var pool = customers.Where(c => c.CreatedAt <= apptCreated).ToList();
            if (pool.Count == 0) return null;
            var customer = pool[_rng.Next(pool.Count)];

            var status = PickStatus(segs[0].Start, segs[^1].End);
            var appt = new Appointment
            {
                Reference = await Sequences.NextAsync(db, "AP", now.Year, 4, ct),
                SubmissionKey = Guid.NewGuid(),
                Customer = customer,
                Location = st.Location,
                Status = status,
                Source = (BookingSource)_rng.Next(0, 4),
                TaxRateBasisPoints = _s.TaxRateBasisPoints,
                RemindByEmail = _rng.NextDouble() < 0.5,
                RemindBySms = false,
                ReminderLeadHours = _s.ReminderLeadHours,
                CreatedAt = apptCreated,
                UpdatedAt = apptCreated,
                CreatedByName = "Front Desk",
                Version = 1,
                IsDemo = true,
                CustomerNotes = _rng.NextDouble() < 0.15 ? "Customer prefers medium pressure." : null,
                InternalNotes = _rng.NextDouble() < 0.1 ? "Returning client — offer membership options at checkout." : null,
                CancellationReason = status == AppointmentStatus.Cancelled ? "Customer requested cancellation" : null,
            };
            for (var i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                appt.Segments.Add(new AppointmentSegment
                {
                    SortOrder = i, Service = plan[i], ServiceName = plan[i].Name, DurationMinutes = plan[i].DurationMinutes, PrepMinutes = plan[i].PrepMinutes,
                    CleanupMinutes = plan[i].CleanupMinutes, UnitPriceMinor = plan[i].PriceMinor, DepositMinor = plan[i].DepositMinor,
                    DiscountMinor = _rng.NextDouble() < 0.06 ? plan[i].PriceMinor / 10 : 0, StaffMember = st, Resource = chosen[i],
                    Start = s.Start, End = s.End, BlockStart = s.BlockStart, BlockEnd = s.BlockEnd, IsBlocking = AppointmentLifecycle.IsBlocking(status),
                });
                if (AppointmentLifecycle.IsBlocking(status) || status == AppointmentStatus.NoShow)
                {
                    Busy(_staffBusy, st, s.Blocked);
                    if (chosen[i] is { } r) Busy(_resourceBusy, r, s.Blocked);
                }
            }

            appt.RecalculateFromSegments();
            db.Appointments.Add(appt);
            AddHistory(appt, customer, apptCreated);
            await AddPaymentsAsync(appt, customer, apptCreated, ct);
            if (appt.Start > now && AppointmentLifecycle.IsBlocking(status))
                ReminderPlanner.OnBooked(db, appt, customer, _s, businessName, now);
            return segs[^1].BlockEnd;
        }

        private AppointmentStatus PickStatus(DateTime start, DateTime end)
        {
            var r = _rng.NextDouble();
            if (end <= now) return r < 0.86 ? AppointmentStatus.Completed : r < 0.94 ? AppointmentStatus.Cancelled : AppointmentStatus.NoShow;
            if (start <= now) return AppointmentStatus.InProgress;
            if (start.Date == now.Date && start - now < TimeSpan.FromMinutes(20)) return AppointmentStatus.CheckedIn;
            return r < 0.68 ? AppointmentStatus.Confirmed : r < 0.95 ? AppointmentStatus.Pending : AppointmentStatus.Cancelled;
        }

        private void AddHistory(Appointment a, Customer c, DateTime created)
        {
            void E(DateTime at, AppointmentEventType type, string desc, AppointmentStatus? from = null, AppointmentStatus? to = null) =>
                a.Events.Add(new AppointmentEvent { Timestamp = at, Type = type, Description = desc, FromStatus = from, ToStatus = to, UserName = "Front Desk" });
            void Log(DateTime at, string action, string summary) =>
                db.AuditLogs.Add(new AuditLog { Timestamp = at, UserName = "Front Desk", Action = action, EntityType = "Appointment", Summary = summary, CustomerId = c.Id, IsDemo = true });

            var services = string.Join(", ", a.Segments.Select(s => s.ServiceName));
            E(created, AppointmentEventType.Created, $"Booked via {a.Source.ToString().ToLowerInvariant()} as Pending", null, AppointmentStatus.Pending);
            Log(created, "Appointment.Created", $"Booked {services} for {a.Start:MMM d, h:mm tt}");
            var confirmedAt = Min(created.AddHours(2), now);
            if (a.Status is AppointmentStatus.Confirmed or AppointmentStatus.CheckedIn or AppointmentStatus.InProgress or AppointmentStatus.Completed or AppointmentStatus.NoShow)
                E(confirmedAt, AppointmentEventType.StatusChanged, "Pending → Confirmed", AppointmentStatus.Pending, AppointmentStatus.Confirmed);
            if (a.Status is AppointmentStatus.CheckedIn or AppointmentStatus.InProgress or AppointmentStatus.Completed)
                E(Min(a.Start.AddMinutes(-5), now), AppointmentEventType.StatusChanged, "Confirmed → Checked In", AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn);
            if (a.Status is AppointmentStatus.InProgress or AppointmentStatus.Completed)
                E(Min(a.Start, now), AppointmentEventType.StatusChanged, "Checked In → In Progress", AppointmentStatus.CheckedIn, AppointmentStatus.InProgress);
            switch (a.Status)
            {
                case AppointmentStatus.Completed:
                    E(a.End, AppointmentEventType.StatusChanged, "In Progress → Completed", AppointmentStatus.InProgress, AppointmentStatus.Completed);
                    Log(a.End, "Appointment.Completed", $"Completed: {a.Reference}");
                    break;
                case AppointmentStatus.Cancelled:
                    var at = Min(created.AddHours(20), now);
                    E(at, AppointmentEventType.StatusChanged, "Cancelled: Customer requested cancellation", AppointmentStatus.Pending, AppointmentStatus.Cancelled);
                    Log(at, "Appointment.Cancelled", $"Cancelled: {a.Reference}");
                    break;
                case AppointmentStatus.NoShow:
                    E(a.Start.AddMinutes(20), AppointmentEventType.StatusChanged, "Customer did not arrive", AppointmentStatus.Confirmed, AppointmentStatus.NoShow);
                    break;
            }
        }

        private async Task AddPaymentsAsync(Appointment a, Customer c, DateTime created, CancellationToken ct)
        {
            async Task Pay(PaymentKind kind, long amount, DateTime at)
            {
                if (amount <= 0) return;
                var method = (PaymentMethod)_rng.Next(0, 3);
                var p = new Payment
                {
                    SubmissionKey = Guid.NewGuid(), ReceiptNumber = await Sequences.NextAsync(db, "RCT", now.Year, 5, ct), Customer = c, Kind = kind,
                    Method = method, AmountMinor = amount, RecordedAt = at, RecordedByName = "Front Desk", IsDemo = true,
                };
                a.Payments.Add(p);
                a.Events.Add(new AppointmentEvent
                {
                    Timestamp = at, Type = AppointmentEventType.PaymentRecorded, UserName = "Front Desk",
                    Description = $"{kind} of {settings.Currency.Format(amount)} via {PaymentService.MethodName(method)} ({p.ReceiptNumber})",
                });
                db.AuditLogs.Add(new AuditLog { Timestamp = at, UserName = "Front Desk", Action = "Payment.Recorded", EntityType = "Payment", Summary = $"{settings.Currency.Format(amount)} payment for {a.Reference}", CustomerId = c.Id, IsDemo = true });
            }

            var deposit = a.DepositRequiredMinor;
            var paidDeposit = 0L;
            if (deposit > 0 && a.Status != AppointmentStatus.Pending && _rng.NextDouble() < 0.8)
            {
                await Pay(PaymentKind.Deposit, deposit, Min(created.AddMinutes(10), now));
                paidDeposit = deposit;
            }

            if (a.Status != AppointmentStatus.Completed) return;
            var remaining = a.TotalMinor - paidDeposit;
            var partial = _rng.NextDouble() < 0.07;
            await Pay(PaymentKind.Payment, partial ? remaining / 2 : remaining, a.End.AddMinutes(5) > now ? now : a.End.AddMinutes(5));

            var invoice = new Invoice
            {
                Number = await Sequences.NextAsync(db, "INV", now.Year, 4, ct), CustomerId = c.Id, IssuedAt = a.End, CustomerName = c.FullName, CustomerEmail = c.Email,
                CustomerPhone = c.Phone, SubtotalMinor = a.SubtotalMinor, DiscountMinor = a.DiscountMinor, TaxRateBasisPoints = a.TaxRateBasisPoints,
                TaxMinor = a.TaxMinor, TotalMinor = a.TotalMinor, Status = partial ? InvoiceStatus.PartiallyPaid : InvoiceStatus.Paid, IsDemo = true,
                Lines = a.Segments.Select(s => new InvoiceLine
                {
                    Description = s.ServiceName, StaffName = s.StaffMember?.FullName, Quantity = 1, UnitPriceMinor = s.UnitPriceMinor,
                    DiscountMinor = s.DiscountMinor, LineTotalMinor = s.UnitPriceMinor - s.DiscountMinor,
                }).ToList(),
            };
            a.Invoices.Add(invoice);
        }

        private T Add<T>(T entity) where T : class
        {
            ((Microsoft.EntityFrameworkCore.DbContext)db).Add(entity);
            return entity;
        }

        private Resource R(string name, ResourceType type, Location loc) =>
            Add(new Resource { Name = name, Type = type, Location = loc, Capacity = 1, IsActive = true, IsDemo = true, Version = 1 });

        private StaffMember St(string name, string position, string color, Location loc, DayOfWeek[] days, int from, int to, int breakHour, string[] skills)
        {
            var s = Add(new StaffMember
            {
                FullName = name, Position = position, ColorHex = color, Location = loc, EmploymentStatus = EmploymentStatus.Active, IsDemo = true,
                Phone = $"(555) 020-{name.Length * 113 % 9000 + 1000:0000}", Email = $"{name.ToLowerInvariant().Replace(' ', '.')}@example.com",
                CreatedAt = now.AddDays(-300), Version = 1,
            });
            foreach (var d in days)
                s.Shifts.Add(new StaffShift { DayOfWeek = d, StartTime = new TimeOnly(from, 0), EndTime = new TimeOnly(to, 0), BreakStart = new TimeOnly(breakHour, 0), BreakEnd = new TimeOnly(breakHour + 1, 0) });
            foreach (var skill in skills)
                s.Skills.Add(new StaffSkill { Service = db.Services.Local.First(x => x.Name == skill) });
            return s;
        }

        private static void Busy(Dictionary<object, List<TimeInterval>> map, object owner, TimeInterval iv)
        {
            if (!map.TryGetValue(owner, out var list)) map[owner] = list = [];
            list.Add(iv);
        }

        private static bool IsBusy(Dictionary<object, List<TimeInterval>> map, object owner, TimeInterval iv) =>
            map.TryGetValue(owner, out var list) && list.Any(x => x.Overlaps(iv));

        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    }
}
