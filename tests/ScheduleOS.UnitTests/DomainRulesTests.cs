using ScheduleOS.Application.Common;
using ScheduleOS.Application.Customers;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.UnitTests;

public class TimeIntervalTests
{
    private static readonly DateTime T = new(2030, 1, 1, 10, 0, 0);

    [Theory]
    [InlineData(0, 60, 30, 90, true)]
    [InlineData(0, 60, 60, 120, false)] // touching
    [InlineData(0, 60, -60, 0, false)]  // touching on the left
    [InlineData(0, 60, 10, 20, true)]   // contained
    [InlineData(0, 60, 90, 120, false)]
    public void Overlap_uses_half_open_intervals(int s1, int e1, int s2, int e2, bool expected)
    {
        var a = new TimeInterval(T.AddMinutes(s1), T.AddMinutes(e1));
        var b = new TimeInterval(T.AddMinutes(s2), T.AddMinutes(e2));
        Assert.Equal(expected, a.Overlaps(b));
        Assert.Equal(expected, b.Overlaps(a));
    }

    [Fact]
    public void Invalid_interval_is_rejected() => Assert.Throws<ArgumentException>(() => new TimeInterval(T, T.AddMinutes(-1)));
}

public class LifecycleTests
{
    [Theory]
    [InlineData(AppointmentStatus.Pending, AppointmentStatus.Confirmed, true)]
    [InlineData(AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn, true)]
    [InlineData(AppointmentStatus.CheckedIn, AppointmentStatus.InProgress, true)]
    [InlineData(AppointmentStatus.InProgress, AppointmentStatus.Completed, true)]
    [InlineData(AppointmentStatus.Pending, AppointmentStatus.Cancelled, true)]
    [InlineData(AppointmentStatus.Confirmed, AppointmentStatus.Cancelled, true)]
    [InlineData(AppointmentStatus.Confirmed, AppointmentStatus.NoShow, true)]
    [InlineData(AppointmentStatus.Completed, AppointmentStatus.Pending, false)]
    [InlineData(AppointmentStatus.Cancelled, AppointmentStatus.CheckedIn, false)]
    [InlineData(AppointmentStatus.NoShow, AppointmentStatus.Completed, false)]
    [InlineData(AppointmentStatus.Pending, AppointmentStatus.Completed, false)]
    [InlineData(AppointmentStatus.InProgress, AppointmentStatus.Cancelled, false)]
    public void Transitions_follow_the_state_machine(AppointmentStatus from, AppointmentStatus to, bool allowed) =>
        Assert.Equal(allowed, AppointmentLifecycle.CanTransition(from, to));

    [Fact]
    public void Invalid_transition_throws_and_blocking_flag_follows_status()
    {
        var a = new Appointment { Status = AppointmentStatus.Confirmed, Segments = [new AppointmentSegment { IsBlocking = true }] };
        a.ChangeStatus(AppointmentStatus.Cancelled);
        Assert.False(a.Segments[0].IsBlocking);
        Assert.Throws<InvalidTransitionException>(() => a.ChangeStatus(AppointmentStatus.CheckedIn));
    }

    [Fact]
    public void Corrections_are_controlled()
    {
        Assert.Equal(AppointmentStatus.InProgress, AppointmentLifecycle.CorrectionTarget(AppointmentStatus.Completed));
        Assert.Equal(AppointmentStatus.Confirmed, AppointmentLifecycle.CorrectionTarget(AppointmentStatus.NoShow));
        Assert.Equal(AppointmentStatus.Pending, AppointmentLifecycle.CorrectionTarget(AppointmentStatus.Cancelled));
        Assert.Null(AppointmentLifecycle.CorrectionTarget(AppointmentStatus.Confirmed));

        var a = new Appointment { Status = AppointmentStatus.Cancelled, Segments = [new AppointmentSegment()] };
        a.ApplyCorrection();
        Assert.Equal(AppointmentStatus.Pending, a.Status);
        Assert.True(a.Segments[0].IsBlocking);
    }
}

public class PricingTests
{
    private static Appointment Appt(int taxBp, params (long price, long discount, long deposit)[] lines)
    {
        var start = new DateTime(2030, 1, 1, 9, 0, 0);
        var a = new Appointment { TaxRateBasisPoints = taxBp };
        foreach (var (p, d, dep) in lines)
        {
            a.Segments.Add(new AppointmentSegment { UnitPriceMinor = p, DiscountMinor = d, DepositMinor = dep, Start = start, End = start.AddMinutes(60) });
            start = start.AddMinutes(75);
        }

        a.RecalculateFromSegments();
        return a;
    }

    [Fact]
    public void Totals_include_discounts_tax_and_deposit()
    {
        var a = Appt(1000, (12_000, 0, 5_000), (9_000, 1_000, 0)); // 10% tax
        Assert.Equal(21_000, a.SubtotalMinor);
        Assert.Equal(1_000, a.DiscountMinor);
        Assert.Equal(2_000, a.TaxMinor);
        Assert.Equal(22_000, a.TotalMinor);
        Assert.Equal(5_000, a.DepositRequiredMinor);
        Assert.Equal(new DateTime(2030, 1, 1, 9, 0, 0), a.Start);
        Assert.Equal(new DateTime(2030, 1, 1, 11, 15, 0), a.End);
    }

    [Theory]
    [InlineData(1005, 825, 83)]   // 8.25% of 10.05 = 0.829 → 83
    [InlineData(1000, 750, 75)]
    [InlineData(5, 1000, 1)]       // 0.5 rounds away from zero
    [InlineData(0, 1000, 0)]
    public void Basis_points_round_half_away_from_zero(long amount, int bp, long expected) =>
        Assert.Equal(expected, Money.ApplyBasisPoints(amount, bp));

    [Fact]
    public void Payment_balance_uses_signed_ledger()
    {
        var payments = new List<Payment>
        {
            new() { Kind = PaymentKind.Deposit, AmountMinor = 5_000 },
            new() { Kind = PaymentKind.Payment, AmountMinor = 10_000 },
            new() { Kind = PaymentKind.Refund, AmountMinor = 2_000 },
            new() { Kind = PaymentKind.Reversal, AmountMinor = 1_000 },
        };
        Assert.Equal(12_000, PaymentMath.NetPaid(payments));
        var a = new Appointment { TotalMinor = 21_000, Payments = payments };
        Assert.Equal(9_000, a.BalanceMinor);
    }

    [Fact]
    public void Payment_status_labels()
    {
        Assert.Equal("Unpaid", PaymentService.PaymentStatus(10_000, 0, []));
        Assert.Equal("Paid Deposit", PaymentService.PaymentStatus(10_000, 3_000, [new Payment { Kind = PaymentKind.Deposit, AmountMinor = 3_000 }]));
        Assert.Equal("Partially Paid", PaymentService.PaymentStatus(10_000, 0, [new Payment { Kind = PaymentKind.Payment, AmountMinor = 3_000 }]));
        Assert.Equal("Paid", PaymentService.PaymentStatus(10_000, 0, [new Payment { Kind = PaymentKind.Payment, AmountMinor = 10_000 }]));
        Assert.Equal("Refunded", PaymentService.PaymentStatus(10_000, 0,
            [new Payment { Kind = PaymentKind.Payment, AmountMinor = 1_000 }, new Payment { Kind = PaymentKind.Refund, AmountMinor = 1_000, ReversesPaymentId = 1 }]));
    }

    [Fact]
    public void Currency_format_and_parse_are_exact()
    {
        var usd = new CurrencyFormat("USD", 2);
        Assert.Equal("$1,284.00", usd.Format(128_400));
        Assert.Equal("-$5.50", usd.Format(-550));
        Assert.True(usd.TryParse("120.50", out var m));
        Assert.Equal(12_050, m);
        Assert.False(usd.TryParse("1.005", out _)); // more decimals than the currency supports
        var vnd = new CurrencyFormat("VND", 0);
        Assert.Equal("150,000 ₫", vnd.Format(150_000));
        Assert.Equal("$1.3K", usd.FormatCompact(128_400));
    }
}

public class SecurityTests
{
    [Fact]
    public void Password_hash_is_salted_and_verifiable()
    {
        var (h1, s1, it) = PasswordHasher.Hash("Correct-horse-42", 10_000);
        var (h2, s2, _) = PasswordHasher.Hash("Correct-horse-42", 10_000);
        Assert.NotEqual(s1, s2);
        Assert.NotEqual(h1, h2);
        Assert.True(PasswordHasher.Verify("Correct-horse-42", h1, s1, it));
        Assert.False(PasswordHasher.Verify("correct-horse-42", h1, s1, it));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("admin123")]
    [InlineData("123456")]
    [InlineData("short1")]
    [InlineData("lettersonly")]
    public void Weak_or_default_passwords_are_rejected(string password) => Assert.NotEmpty(PasswordHasher.Validate(password));

    [Fact]
    public void Role_permissions_are_least_privilege()
    {
        Assert.True(RolePermissions.Has(UserRole.Owner, Permission.RestoreBackups));
        Assert.False(RolePermissions.Has(UserRole.Manager, Permission.ManageUsers));
        Assert.False(RolePermissions.Has(UserRole.Receptionist, Permission.RefundPayments));
        Assert.True(RolePermissions.Has(UserRole.Receptionist, Permission.RecordPayments));
        Assert.False(RolePermissions.Has(UserRole.Staff, Permission.ManageAppointments));
        Assert.False(RolePermissions.Has(UserRole.Staff, Permission.ViewAllAppointments));
        Assert.False(RolePermissions.Has(UserRole.Staff, Permission.ViewCustomerContact));
    }

    [Fact]
    public void Session_requires_permissions()
    {
        var session = new UserSession();
        Assert.Throws<PermissionDeniedException>(() => session.Require(Permission.ViewDashboard, "x"));
        session.SignIn(1, "rec", "Reception", UserRole.Receptionist, null);
        session.Require(Permission.ManageAppointments, "x");
        Assert.Throws<PermissionDeniedException>(() => session.Require(Permission.RefundPayments, "refund"));
    }
}

public class CustomerRulesTests
{
    [Theory]
    [InlineData("(555) 010-1234", "5550101234")]
    [InlineData("+1 555 010 1234", "+15550101234")]
    [InlineData("  ", null)]
    public void Phone_normalisation(string input, string? expected) => Assert.Equal(expected, Normalization.Phone(input));

    [Fact]
    public void Email_normalisation_and_validation()
    {
        Assert.Equal("a.b@example.com", Normalization.Email("  A.B@Example.com "));
        Assert.True(Normalization.IsValidEmail("a.b@example.com"));
        Assert.False(Normalization.IsValidEmail("not-an-email"));
        Assert.False(Normalization.IsValidEmail("a@b"));
    }

    [Fact]
    public void Segmentation_uses_configurable_definitions()
    {
        var now = new DateTime(2030, 6, 1);
        var s = new AppSettings { VipMinVisits = 5, ReturningMinVisits = 2, InactiveDays = 90, FrequentCancellationCount = 2, NewCustomerDays = 30 };
        var fresh = new Customer { CreatedAt = now.AddDays(-5) };
        Assert.Contains(CustomerSegmentation.New, CustomerSegmentation.Classify(fresh, null, s, now));

        var loyal = new Customer { CreatedAt = now.AddDays(-400) };
        var loyalSegments = CustomerSegmentation.Classify(loyal, new CustomerStats(6, 0, 0, 1, now.AddDays(-10), 50_000), s, now);
        Assert.Contains(CustomerSegmentation.Vip, loyalSegments);
        Assert.Contains(CustomerSegmentation.Returning, loyalSegments);
        Assert.DoesNotContain(CustomerSegmentation.Inactive, loyalSegments);

        var lapsed = CustomerSegmentation.Classify(loyal, new CustomerStats(1, 2, 1, 0, now.AddDays(-200), 1_000), s, now);
        Assert.Contains(CustomerSegmentation.Inactive, lapsed);
        Assert.Contains(CustomerSegmentation.FrequentCancellations, lapsed);

        var tagged = new Customer { CreatedAt = now.AddDays(-400), Tags = [new CustomerTag { Tag = "vip" }] };
        Assert.Contains(CustomerSegmentation.Vip, CustomerSegmentation.Classify(tagged, null, s, now));
    }
}
