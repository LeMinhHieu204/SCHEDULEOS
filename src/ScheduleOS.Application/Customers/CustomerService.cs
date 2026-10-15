using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Customers;

public sealed record CustomerStats(int Completed, int Cancelled, int NoShows, int Upcoming, DateTime? LastVisit, long TotalPaidMinor);

public static class CustomerSegmentation
{
    public const string New = "New";
    public const string Returning = "Returning";
    public const string Vip = "VIP";
    public const string Inactive = "Inactive";
    public const string FrequentCancellations = "Frequent cancellations";

    public static readonly IReadOnlyList<string> All = [New, Returning, Vip, Inactive, FrequentCancellations];

    public static async Task<Dictionary<int, CustomerStats>> LoadStatsAsync(IAppDbContext db, IReadOnlyCollection<int>? customerIds, DateTime now, CancellationToken ct)
    {
        var appts = db.Appointments.AsNoTracking();
        var pays = db.Payments.AsNoTracking();
        if (customerIds is not null)
        {
            appts = appts.Where(a => customerIds.Contains(a.CustomerId));
            pays = pays.Where(p => customerIds.Contains(p.CustomerId));
        }

        var apptStats = await appts.GroupBy(a => a.CustomerId).Select(g => new
        {
            g.Key,
            Completed = g.Count(a => a.Status == AppointmentStatus.Completed),
            Cancelled = g.Count(a => a.Status == AppointmentStatus.Cancelled),
            NoShows = g.Count(a => a.Status == AppointmentStatus.NoShow),
            Upcoming = g.Count(a => a.Start > now && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed)),
            LastVisit = g.Where(a => a.Status == AppointmentStatus.Completed).Max(a => (DateTime?)a.Start),
        }).ToListAsync(ct);
        var payStats = await pays.GroupBy(p => p.CustomerId)
            .Select(g => new { g.Key, Total = g.Sum(p => p.Kind == PaymentKind.Deposit || p.Kind == PaymentKind.Payment ? p.AmountMinor : -p.AmountMinor) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);

        var result = apptStats.ToDictionary(x => x.Key, x => new CustomerStats(x.Completed, x.Cancelled, x.NoShows, x.Upcoming, x.LastVisit,
            payStats.GetValueOrDefault(x.Key)));
        foreach (var (id, total) in payStats)
            result.TryAdd(id, new CustomerStats(0, 0, 0, 0, null, total));
        return result;
    }

    public static IReadOnlyList<string> Classify(Customer c, CustomerStats? st, AppSettings s, DateTime now)
    {
        st ??= new CustomerStats(0, 0, 0, 0, null, 0);
        var result = new List<string>();
        var isVip = st.Completed >= s.VipMinVisits || st.TotalPaidMinor >= s.VipMinSpentMinor ||
                    c.Tags.Any(t => t.Tag.Equals("VIP", StringComparison.OrdinalIgnoreCase));
        if (isVip) result.Add(Vip);
        if (c.CreatedAt >= now.AddDays(-s.NewCustomerDays) && st.Completed <= 1) result.Add(New);
        else if (st.Completed >= s.ReturningMinVisits) result.Add(Returning);
        var lastActivity = st.LastVisit ?? c.CreatedAt;
        if (st.Upcoming == 0 && lastActivity < now.AddDays(-s.InactiveDays)) result.Add(Inactive);
        if (st.Cancelled + st.NoShows >= s.FrequentCancellationCount) result.Add(FrequentCancellations);
        return result;
    }
}

public sealed record CustomerListItem(
    int Id,
    string FullName,
    string Initials,
    string? Phone,
    string? Email,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Segments,
    int CompletedVisits,
    int Upcoming,
    DateTime? LastVisit,
    long TotalPaidMinor,
    bool IsArchived,
    DateTime CreatedAt);

public enum CustomerSort { Name, Newest, LastVisit, TotalSpent }

public sealed record CustomerQuery
{
    public string? Search { get; init; }
    public string? Segment { get; init; }
    public string? Tag { get; init; }
    public bool IncludeArchived { get; init; }
    public CustomerSort Sort { get; init; } = CustomerSort.Name;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed class CustomerEditDto
{
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public DateOnly? Birthday { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public List<string> Tags { get; set; } = [];
    public int? PreferredStaffId { get; set; }
    public bool AllowEmail { get; set; } = true;
    public bool AllowSms { get; set; } = true;
}

public sealed record CustomerDetailDto(
    int Id,
    int Version,
    CustomerEditDto Profile,
    string Initials,
    string? PreferredStaffName,
    bool IsArchived,
    bool IsAnonymized,
    bool ContactHidden,
    DateTime CreatedAt,
    CustomerStats Stats,
    IReadOnlyList<string> Segments,
    IReadOnlyList<AppointmentListItem> Appointments,
    IReadOnlyList<PaymentDto> Payments,
    IReadOnlyList<InvoiceSummaryDto> Invoices);

public sealed class DuplicateCustomerException(IReadOnlyList<Lookup> matches)
    : DomainException("Possible duplicate customer: " + string.Join(", ", matches.Select(m => $"{m.Name} ({m.Detail})")))
{
    public IReadOnlyList<Lookup> Matches { get; } = matches;
}

public sealed class CustomerService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    SettingsService settings,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier)
{
    public async Task<PagedResult<CustomerListItem>> SearchAsync(CustomerQuery q, CancellationToken ct = default)
    {
        user.Require(Permission.ViewCustomers, "view customers");
        await using var db = dbFactory.CreateReadContext();
        var now = clock.Now;
        var query = db.Customers.AsNoTracking().Include(c => c.Tags).AsQueryable();
        if (!q.IncludeArchived) query = query.Where(c => !c.IsArchived);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var like = Normalization.LikePattern(q.Search);
            var phone = Normalization.Phone(q.Search);
            query = query.Where(c => EF.Functions.Like(c.FullName, like, "\\") || EF.Functions.Like(c.Email ?? "", like, "\\") ||
                                     (phone != null && phone.Length >= 3 && c.NormalizedPhone != null && c.NormalizedPhone.Contains(phone)));
        }

        if (!string.IsNullOrWhiteSpace(q.Tag)) query = query.Where(c => c.Tags.Any(t => t.Tag == q.Tag));

        List<Customer> customers;
        Dictionary<int, CustomerStats> stats;
        int total;
        var needsStatsForAll = q.Segment is not null || q.Sort is CustomerSort.LastVisit or CustomerSort.TotalSpent;
        if (needsStatsForAll)
        {
            var all = await query.ToListAsync(ct);
            stats = await CustomerSegmentation.LoadStatsAsync(db, null, now, ct);
            IEnumerable<Customer> filtered = all;
            if (q.Segment is not null)
                filtered = filtered.Where(c => CustomerSegmentation.Classify(c, stats.GetValueOrDefault(c.Id), settings.Current, now).Contains(q.Segment));
            filtered = q.Sort switch
            {
                CustomerSort.LastVisit => filtered.OrderByDescending(c => stats.GetValueOrDefault(c.Id)?.LastVisit ?? DateTime.MinValue),
                CustomerSort.TotalSpent => filtered.OrderByDescending(c => stats.GetValueOrDefault(c.Id)?.TotalPaidMinor ?? 0),
                CustomerSort.Newest => filtered.OrderByDescending(c => c.CreatedAt),
                _ => filtered.OrderBy(c => c.FullName),
            };
            var list = filtered.ToList();
            total = list.Count;
            customers = list.Skip((Math.Max(1, q.Page) - 1) * q.PageSize).Take(q.PageSize).ToList();
        }
        else
        {
            query = q.Sort switch
            {
                CustomerSort.Newest => query.OrderByDescending(c => c.CreatedAt),
                CustomerSort.Name when !string.IsNullOrWhiteSpace(q.Search) =>
                    query.OrderByDescending(c => EF.Functions.Like(c.FullName, Normalization.LikePattern(q.Search), "\\"))
                        .ThenBy(c => c.FullName),
                _ => query.OrderBy(c => c.FullName),
            };
            total = await query.CountAsync(ct);
            customers = await query.Skip((Math.Max(1, q.Page) - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
            stats = await CustomerSegmentation.LoadStatsAsync(db, customers.Select(c => c.Id).ToList(), now, ct);
        }

        var hide = !user.Has(Permission.ViewCustomerContact);
        var items = customers.Select(c =>
        {
            var st = stats.GetValueOrDefault(c.Id);
            return new CustomerListItem(c.Id, c.FullName, c.Initials, hide ? null : c.Phone, hide ? null : c.Email,
                c.Tags.Select(t => t.Tag).OrderBy(t => t).ToList(), CustomerSegmentation.Classify(c, st, settings.Current, now),
                st?.Completed ?? 0, st?.Upcoming ?? 0, st?.LastVisit, st?.TotalPaidMinor ?? 0, c.IsArchived, c.CreatedAt);
        }).ToList();
        return new PagedResult<CustomerListItem>(items, total, Math.Max(1, q.Page), q.PageSize);
    }

    public async Task<IReadOnlyList<Lookup>> LookupAsync(string? term, int take = 30, CancellationToken ct = default)
    {
        user.Require(Permission.ViewCustomers, "search customers");
        await using var db = dbFactory.CreateReadContext();
        var query = db.Customers.AsNoTracking().Where(c => !c.IsArchived);
        if (!string.IsNullOrWhiteSpace(term))
        {
            var like = Normalization.LikePattern(term);
            var phone = Normalization.Phone(term);
            query = query.Where(c => EF.Functions.Like(c.FullName, like, "\\") || EF.Functions.Like(c.Email ?? "", like, "\\") ||
                                     (phone != null && phone.Length >= 3 && c.NormalizedPhone != null && c.NormalizedPhone.Contains(phone)));
        }

        var hide = !user.Has(Permission.ViewCustomerContact);
        return await query.OrderBy(c => c.FullName).Take(take)
            .Select(c => new Lookup(c.Id, c.FullName, hide ? null : (c.Phone ?? c.Email))).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        return await db.CustomerTags.AsNoTracking().Select(t => t.Tag).Distinct().OrderBy(t => t).ToListAsync(ct);
    }

    public async Task<CustomerDetailDto> GetAsync(int id, CancellationToken ct = default)
    {
        user.Require(Permission.ViewCustomers, "view customers");
        await using var db = dbFactory.CreateReadContext();
        var c = await db.Customers.AsNoTracking().Include(x => x.Tags).Include(x => x.PreferredStaff)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer", id);
        var now = clock.Now;
        var stats = (await CustomerSegmentation.LoadStatsAsync(db, [id], now, ct)).GetValueOrDefault(id) ?? new CustomerStats(0, 0, 0, 0, null, 0);
        var hide = !user.Has(Permission.ViewCustomerContact);

        var appointmentsQuery = db.Appointments.AsNoTracking().Where(a => a.CustomerId == id);
        if (!user.Has(Permission.ViewAllAppointments))
        {
            var own = user.StaffMemberId ?? -1;
            appointmentsQuery = appointmentsQuery.Where(a => a.Segments.Any(s => s.StaffMemberId == own));
        }

        var appts = await appointmentsQuery.OrderByDescending(a => a.Start).Take(200).Select(a => new
        {
            a.Id, a.Reference, a.Start, a.End, a.Status, a.TotalMinor, Location = a.Location != null ? a.Location.Name : null,
            Paid = a.Payments.Sum(p => p.Kind == PaymentKind.Deposit || p.Kind == PaymentKind.Payment ? p.AmountMinor : -p.AmountMinor),
            Segs = a.Segments.OrderBy(s => s.SortOrder).Select(s => new { s.ServiceName, Staff = s.StaffMember!.FullName, Res = s.Resource != null ? s.Resource.Name : null }).ToList(),
        }).ToListAsync(ct);
        var appointmentItems = appts.Select(r => new AppointmentListItem(r.Id, r.Reference, r.Start, r.End, r.Status, id, c.FullName,
            string.Join(", ", r.Segs.Select(s => s.ServiceName)), string.Join(", ", r.Segs.Select(s => s.Staff).Distinct()),
            string.Join(", ", r.Segs.Where(s => s.Res != null).Select(s => s.Res).Distinct()), r.TotalMinor, r.Paid, r.Location)).ToList();

        IReadOnlyList<PaymentDto> payments = [];
        IReadOnlyList<InvoiceSummaryDto> invoices = [];
        if (user.Has(Permission.ViewPayments))
        {
            var pays = await db.Payments.AsNoTracking().Include(p => p.Appointment).Where(p => p.CustomerId == id).OrderByDescending(p => p.RecordedAt).ToListAsync(ct);
            payments = PaymentService.ToDtos(pays, null, c.FullName);
            invoices = await db.Invoices.AsNoTracking().Where(i => i.CustomerId == id).OrderByDescending(i => i.IssuedAt)
                .Select(i => new InvoiceSummaryDto(i.Id, i.Number, i.IssuedAt, i.Status, i.TotalMinor)).ToListAsync(ct);
        }

        var profile = new CustomerEditDto
        {
            FullName = c.FullName,
            Phone = hide ? null : c.Phone,
            Email = hide ? null : c.Email,
            Birthday = hide ? null : c.Birthday,
            Address = hide ? null : c.Address,
            Notes = hide ? null : c.Notes,
            Tags = c.Tags.Select(t => t.Tag).OrderBy(t => t).ToList(),
            PreferredStaffId = c.PreferredStaffId,
            AllowEmail = c.AllowEmail,
            AllowSms = c.AllowSms,
        };
        return new CustomerDetailDto(c.Id, c.Version, profile, c.Initials, c.PreferredStaff?.FullName, c.IsArchived, c.IsAnonymized, hide,
            c.CreatedAt, stats, CustomerSegmentation.Classify(c, stats, settings.Current, now), appointmentItems, payments, invoices);
    }

    /// <summary>Returns existing customers sharing the normalised phone or email.</summary>
    public async Task<IReadOnlyList<Lookup>> FindDuplicatesAsync(string? phone, string? email, int? excludeId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        return await FindDuplicatesAsync(db, phone, email, excludeId, ct);
    }

    private static async Task<IReadOnlyList<Lookup>> FindDuplicatesAsync(IAppDbContext db, string? phone, string? email, int? excludeId, CancellationToken ct)
    {
        var p = Normalization.Phone(phone);
        var e = Normalization.Email(email);
        if (p is null && e is null) return [];
        return await db.Customers.AsNoTracking()
            .Where(c => c.Id != excludeId && !c.IsAnonymized && ((p != null && c.NormalizedPhone == p) || (e != null && c.NormalizedEmail == e)))
            .Select(c => new Lookup(c.Id, c.FullName, c.NormalizedEmail == e && e != null ? "same email" : "same phone"))
            .ToListAsync(ct);
    }

    public async Task<int> CreateAsync(CustomerEditDto dto, bool ignoreDuplicates, CancellationToken ct = default)
    {
        user.Require(Permission.ManageCustomers, "add customers");
        Validate(dto);
        var id = await writer.ExecuteAsync(async (db, c) =>
        {
            if (!ignoreDuplicates)
            {
                var dups = await FindDuplicatesAsync(db, dto.Phone, dto.Email, null, c);
                if (dups.Count > 0) throw new DuplicateCustomerException(dups);
            }

            var now = clock.Now;
            var customer = new Customer { CreatedAt = now, Version = 1 };
            Apply(customer, dto, now);
            db.Customers.Add(customer);
            await db.SaveChangesAsync(c);
            Audit.Add(db, user, clock, "Customer.Created", "Customer", customer.Id, $"Customer {customer.FullName} added", customer.Id);
            await db.SaveChangesAsync(c);
            return customer.Id;
        }, ct);
        notifier.Publish(DataArea.Customers);
        return id;
    }

    public async Task UpdateAsync(int id, int expectedVersion, CustomerEditDto dto, bool ignoreDuplicates, CancellationToken ct = default)
    {
        user.Require(Permission.ManageCustomers, "edit customers");
        user.Require(Permission.ViewCustomerContact, "edit customer contact details");
        Validate(dto);
        await writer.ExecuteAsync(async (db, c) =>
        {
            var customer = await db.Customers.Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Customer", id);
            if (customer.Version != expectedVersion) throw new ConcurrencyConflictException("This customer was changed by someone else. Reload and try again.");
            if (customer.IsAnonymized) throw new ValidationException("Anonymised customers cannot be edited.");
            if (!ignoreDuplicates)
            {
                var dups = await FindDuplicatesAsync(db, dto.Phone, dto.Email, id, c);
                if (dups.Count > 0) throw new DuplicateCustomerException(dups);
            }

            db.CustomerTags.RemoveRange(customer.Tags);
            customer.Tags.Clear();
            Apply(customer, dto, clock.Now);
            customer.Version++;
            Audit.Add(db, user, clock, "Customer.Updated", "Customer", id, $"Customer {customer.FullName} updated", id);
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Customers | DataArea.Appointments);
    }

    public async Task SetArchivedAsync(int id, bool archived, CancellationToken ct = default)
    {
        user.Require(Permission.ManageCustomers, archived ? "archive customers" : "restore customers");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var customer = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Customer", id);
            if (customer.IsAnonymized && !archived) throw new ValidationException("Anonymised customers cannot be restored.");
            customer.IsArchived = archived;
            customer.UpdatedAt = clock.Now;
            customer.Version++;
            Audit.Add(db, user, clock, archived ? "Customer.Archived" : "Customer.Restored", "Customer", id,
                $"Customer {customer.FullName} {(archived ? "archived" : "restored")}", id);
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Customers);
    }

    /// <summary>
    /// Removes personal data while keeping appointment and payment records needed for accounting.
    /// Issued invoices keep the name captured at issue time as a mandatory accounting record.
    /// </summary>
    public async Task AnonymizeAsync(int id, CancellationToken ct = default)
    {
        user.Require(Permission.AnonymizeCustomers, "anonymise customers");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var customer = await db.Customers.Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Customer", id);
            var now = clock.Now;
            if (await db.Appointments.AnyAsync(a => a.CustomerId == id && a.Start > now &&
                                                    (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed), c))
                throw new ValidationException("Cancel this customer's upcoming appointments before anonymising.");
            customer.FullName = $"Anonymised customer #{id}";
            customer.Phone = customer.NormalizedPhone = customer.Email = customer.NormalizedEmail = null;
            customer.Address = customer.Notes = null;
            customer.Birthday = null;
            customer.AllowEmail = customer.AllowSms = false;
            customer.PreferredStaffId = null;
            db.CustomerTags.RemoveRange(customer.Tags);
            customer.IsAnonymized = true;
            customer.IsArchived = true;
            customer.UpdatedAt = now;
            customer.Version++;
            var appts = await db.Appointments.Where(a => a.CustomerId == id).ToListAsync(c);
            foreach (var a in appts) a.CustomerNotes = a.InternalNotes = null;
            var pending = await db.Reminders.Where(r => r.CustomerId == id && r.Status == ReminderStatus.Scheduled).ToListAsync(c);
            foreach (var r in pending) Notifications.ReminderPlanner.Supersede(r, "Customer anonymised.");
            Audit.Add(db, user, clock, "Customer.Anonymised", "Customer", id, $"Customer #{id} anonymised", id);
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Customers | DataArea.Appointments);
    }

    /// <summary>Hard delete is only possible for customers without any appointment or payment history.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        user.Require(Permission.AnonymizeCustomers, "delete customers");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var customer = await db.Customers.Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Customer", id);
            if (await db.Appointments.AnyAsync(a => a.CustomerId == id, c) || await db.Payments.AnyAsync(p => p.CustomerId == id, c))
                throw new ValidationException("This customer has appointment or payment history. Archive or anonymise instead.");
            db.Customers.Remove(customer);
            Audit.Add(db, user, clock, "Customer.Deleted", "Customer", id, "Customer without history deleted");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Customers);
    }

    /// <summary>Exports the personal data held for a customer (data portability request).</summary>
    public async Task<string> ExportPersonalDataJsonAsync(int id, CancellationToken ct = default)
    {
        user.Require(Permission.ExportCustomerData, "export customer data");
        var detail = await GetAsync(id, ct);
        await writer.ExecuteAsync(async (db, c) =>
        {
            Audit.Add(db, user, clock, "Customer.Exported", "Customer", id, "Personal data exported");
            await db.SaveChangesAsync(c);
        }, ct);
        return JsonSerializer.Serialize(new
        {
            exportedAt = clock.Now,
            customer = detail.Profile,
            createdAt = detail.CreatedAt,
            appointments = detail.Appointments.Select(a => new { a.Reference, a.Start, a.End, Status = a.Status.ToString(), a.Services, a.Staff, a.TotalMinor, a.PaidMinor }),
            payments = detail.Payments.Select(p => new { p.ReceiptNumber, Kind = p.Kind.ToString(), Method = p.Method.ToString(), p.AmountMinor, p.RecordedAt }),
            invoices = detail.Invoices.Select(i => new { i.Number, i.IssuedAt, Status = i.Status.ToString(), i.TotalMinor }),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void Validate(CustomerEditDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.FullName)) errors.Add("Full name is required.");
        else if (dto.FullName.Trim().Length > 120) errors.Add("Full name is too long.");
        if (string.IsNullOrWhiteSpace(dto.Phone) && string.IsNullOrWhiteSpace(dto.Email)) errors.Add("Enter a phone number or an email address.");
        if (!string.IsNullOrWhiteSpace(dto.Email) && !Normalization.IsValidEmail(dto.Email)) errors.Add("The email address is not valid.");
        if (!string.IsNullOrWhiteSpace(dto.Phone) && (Normalization.Phone(dto.Phone)?.TrimStart('+').Length ?? 0) < 6) errors.Add("The phone number looks too short.");
        if (dto.Birthday is { } b && (b > DateOnly.FromDateTime(DateTime.Today) || b.Year < 1900)) errors.Add("Birthday is not valid.");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private static void Apply(Customer c, CustomerEditDto dto, DateTime now)
    {
        c.FullName = dto.FullName.Trim();
        c.Phone = Normalization.Clean(dto.Phone);
        c.NormalizedPhone = Normalization.Phone(dto.Phone);
        c.Email = Normalization.Clean(dto.Email);
        c.NormalizedEmail = Normalization.Email(dto.Email);
        c.Birthday = dto.Birthday;
        c.Address = Normalization.Clean(dto.Address);
        c.Notes = Normalization.Clean(dto.Notes);
        c.PreferredStaffId = dto.PreferredStaffId;
        c.AllowEmail = dto.AllowEmail;
        c.AllowSms = dto.AllowSms;
        c.UpdatedAt = now;
        foreach (var tag in dto.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            c.Tags.Add(new CustomerTag { Tag = tag.Length > 40 ? tag[..40] : tag });
    }
}
