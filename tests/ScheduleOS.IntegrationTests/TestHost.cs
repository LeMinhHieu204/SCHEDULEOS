using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ScheduleOS.Application;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Catalog;
using ScheduleOS.Application.Customers;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Application.Staff;
using ScheduleOS.Domain;
using ScheduleOS.Infrastructure;
using ScheduleOS.Infrastructure.Persistence;

namespace ScheduleOS.IntegrationTests;

public sealed class FakeClock(DateTime now) : IClock
{
    public DateTime Now { get; set; } = now;
}

/// <summary>A fully wired application (real SQLite file in a temp directory) without the WPF layer.</summary>
public sealed class TestHost : IAsyncDisposable
{
    /// <summary>Monday 4 March 2030, 08:00.</summary>
    public static readonly DateTime DefaultNow = new(2030, 3, 4, 8, 0, 0);

    public const string OwnerUser = "owner";
    public readonly string OwnerPassword = "Owner-" + Guid.NewGuid().ToString("N")[..10] + "7";

    private TestHost(string root, ServiceProvider services, FakeClock clock)
    {
        Root = root;
        Services = services;
        Clock = clock;
    }

    public string Root { get; }
    public ServiceProvider Services { get; }
    public FakeClock Clock { get; }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static async Task<TestHost> CreateAsync(DateTime? now = null, bool onboard = true, bool demo = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "ScheduleOS.Tests", Guid.NewGuid().ToString("N"));
        var host = await BuildAsync(root, new FakeClock(now ?? DefaultNow));
        if (onboard)
        {
            await host.Get<AuthService>().CompleteOnboardingAsync(new OnboardingRequest("Test Spa", "Test Owner", OwnerUser, host.OwnerPassword, "USD", 2, demo));
            var login = await host.Get<AuthService>().LoginAsync(OwnerUser, host.OwnerPassword);
            Assert.Equal(LoginOutcome.Success, login.Outcome);
        }

        return host;
    }

    /// <summary>Second, independent application instance on the same database file (separate connections, executor and caches).</summary>
    public async Task<TestHost> CreatePeerAsync()
    {
        var peer = await BuildAsync(Root, Clock, ownsDirectory: false);
        var login = await peer.Get<AuthService>().LoginAsync(OwnerUser, OwnerPassword);
        Assert.Equal(LoginOutcome.Success, login.Outcome);
        return peer;
    }

    private bool _ownsDirectory = true;

    private static async Task<TestHost> BuildAsync(string root, FakeClock clock, bool ownsDirectory = true)
    {
        var paths = new AppPaths(root);
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddScheduleOSApplication();
        sc.AddScheduleOSInfrastructure(paths);
        sc.AddSingleton<IClock>(clock);
        var sp = sc.BuildServiceProvider();
        var init = await sp.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        Assert.True(init.Success, init.Message);
        await sp.GetRequiredService<SettingsService>().LoadAsync();
        return new TestHost(root, sp, clock) { _ownsDirectory = ownsDirectory };
    }

    public async Task<T> SqlScalarAsync<T>(string sql)
    {
        await using var conn = new SqliteConnection(Get<SqliteDbContextFactory>().ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(v!, typeof(T));
    }

    public async Task SqlExecAsync(string sql)
    {
        await using var conn = new SqliteConnection(Get<SqliteDbContextFactory>().ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (!_ownsDirectory) return;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }
}

/// <summary>Standard catalogue used by most tests, created through the real application services.</summary>
public sealed class Fixture
{
    public int Alex, Blair, Casey;
    public int Massage, Facial, Consult;
    public int RoomA, RoomB, Treatment1;
    public int Customer1, Customer2;

    public static async Task<Fixture> CreateAsync(TestHost h)
    {
        var f = new Fixture();
        var catalog = h.Get<CatalogService>();
        f.Massage = await catalog.CreateAsync(new ServiceEditDto
        {
            Name = "Massage", DurationMinutes = 60, CleanupMinutes = 15, PriceMinor = 12_000, DepositMinor = 3_000,
            RequiredResourceTypes = [ResourceType.MassageRoom],
        });
        f.Facial = await catalog.CreateAsync(new ServiceEditDto
        {
            Name = "Facial", DurationMinutes = 45, PrepMinutes = 5, CleanupMinutes = 10, PriceMinor = 9_000,
            RequiredResourceTypes = [ResourceType.TreatmentRoom],
        });
        f.Consult = await catalog.CreateAsync(new ServiceEditDto { Name = "Consult", DurationMinutes = 30, CleanupMinutes = 0, PriceMinor = 4_000 });

        var resources = h.Get<ResourceService>();
        f.RoomA = await resources.CreateAsync(new ResourceEditDto { Name = "Massage Room A", Type = ResourceType.MassageRoom });
        f.RoomB = await resources.CreateAsync(new ResourceEditDto { Name = "Massage Room B", Type = ResourceType.MassageRoom });
        f.Treatment1 = await resources.CreateAsync(new ResourceEditDto { Name = "Treatment Room 1", Type = ResourceType.TreatmentRoom });

        var staff = h.Get<StaffService>();
        var shifts = StaffService.DefaultShifts(); // Mon–Fri 09:00–18:00, break 12:00–13:00
        f.Alex = await staff.CreateAsync(new StaffEditDto { FullName = "Alex Therapist", Position = "Therapist", ServiceIds = [f.Massage, f.Facial, f.Consult] }, shifts);
        f.Blair = await staff.CreateAsync(new StaffEditDto { FullName = "Blair Esthetician", Position = "Esthetician", ServiceIds = [f.Massage, f.Facial, f.Consult] }, shifts);
        f.Casey = await staff.CreateAsync(new StaffEditDto { FullName = "Casey Stylist", Position = "Stylist", ServiceIds = [f.Consult] }, shifts);

        var customers = h.Get<CustomerService>();
        f.Customer1 = await customers.CreateAsync(new CustomerEditDto { FullName = "Test Customer One", Email = "one@example.com", Phone = "(555) 010-0001" }, false);
        f.Customer2 = await customers.CreateAsync(new CustomerEditDto { FullName = "Test Customer Two", Email = "two@example.com", Phone = "(555) 010-0002" }, false);
        return f;
    }

    public CreateAppointmentRequest Booking(DateTime start, int staff, int? resource, int? service = null, int? customer = null,
        AppointmentStatus status = AppointmentStatus.Confirmed, bool email = false) => new()
    {
        CustomerId = customer ?? Customer1,
        Start = start,
        Services = [new ServiceLineRequest(service ?? Massage, staff, resource)],
        InitialStatus = status,
        RemindByEmail = email,
    };
}
