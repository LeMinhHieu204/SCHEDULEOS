using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Security;

public sealed record OnboardingRequest(
    string BusinessName,
    string OwnerDisplayName,
    string Username,
    string Password,
    string CurrencyCode,
    int CurrencyDecimals,
    bool CreateDemoWorkspace);

public enum LoginOutcome { Success, InvalidCredentials, LockedOut, Disabled }

public sealed record LoginResult(LoginOutcome Outcome, string Message);

public sealed record UserDto(int Id, string Username, string DisplayName, UserRole Role, int? StaffMemberId, string? StaffName, bool IsActive, DateTime? LastLoginAt, bool IsLockedOut);

public sealed class AuthService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    UserSession session,
    SettingsService settings,
    IClock clock,
    IDataChangeNotifier notifier,
    ILogger<AuthService> logger)
{
    public async Task<bool> HasAnyUserAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        return await db.Users.AnyAsync(ct);
    }

    /// <summary>Creates the business profile and the first Owner account. Only allowed while no users exist.</summary>
    public async Task CompleteOnboardingAsync(OnboardingRequest request, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.BusinessName)) errors.Add("Business name is required.");
        if (string.IsNullOrWhiteSpace(request.OwnerDisplayName)) errors.Add("Your name is required.");
        errors.AddRange(ValidateUsername(request.Username));
        errors.AddRange(PasswordHasher.Validate(request.Password, request.Username));
        if (string.IsNullOrWhiteSpace(request.CurrencyCode) || request.CurrencyCode.Trim().Length != 3) errors.Add("Currency must be a 3-letter ISO code.");
        if (errors.Count > 0) throw new ValidationException(errors);

        var (hash, salt, iterations) = PasswordHasher.Hash(request.Password);
        await writer.ExecuteAsync(async (db, c) =>
        {
            if (await db.Users.AnyAsync(c)) throw new DomainException("ScheduleOS has already been set up. Sign in instead.");

            var business = await db.Businesses.FirstOrDefaultAsync(c);
            if (business is null)
            {
                business = new Business { CreatedAt = clock.Now };
                db.Businesses.Add(business);
            }

            business.Name = request.BusinessName.Trim();
            business.CurrencyCode = request.CurrencyCode.Trim().ToUpperInvariant();
            business.CurrencyDecimals = request.CurrencyDecimals;
            business.IsDemoWorkspace = request.CreateDemoWorkspace;

            // The demo workspace brings its own (demo) locations.
            if (!request.CreateDemoWorkspace && !await db.Locations.AnyAsync(c))
                db.Locations.Add(new Location { Name = "Main Location", IsActive = true });

            if (!await db.BusinessHours.AnyAsync(c))
            {
                foreach (var day in Enum.GetValues<DayOfWeek>())
                    db.BusinessHours.Add(new BusinessHours
                    {
                        DayOfWeek = day,
                        IsOpen = day != DayOfWeek.Sunday,
                        OpenTime = new TimeOnly(9, 0),
                        CloseTime = day == DayOfWeek.Saturday ? new TimeOnly(17, 0) : new TimeOnly(19, 0),
                    });
            }

            db.Users.Add(new AppUser
            {
                Username = request.Username.Trim(),
                NormalizedUsername = request.Username.Trim().ToUpperInvariant(),
                DisplayName = request.OwnerDisplayName.Trim(),
                PasswordHash = hash,
                PasswordSalt = salt,
                PasswordIterations = iterations,
                Role = UserRole.Owner,
                IsActive = true,
                CreatedAt = clock.Now,
            });
            db.AuditLogs.Add(new AuditLog { Timestamp = clock.Now, UserName = request.OwnerDisplayName.Trim(), Action = "Onboarding", EntityType = "Business", Summary = "Workspace created" });
            await db.SaveChangesAsync(c);
        }, ct);
        await settings.LoadAsync(ct);
        logger.LogInformation("Onboarding completed (demo workspace: {Demo})", request.CreateDemoWorkspace);
    }

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var normalized = (username ?? "").Trim().ToUpperInvariant();
        var s = settings.Current;
        var result = await writer.ExecuteAsync(async (db, c) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, c);
            if (u is null)
            {
                // Spend comparable time so usernames cannot be probed by timing.
                PasswordHasher.Verify(password ?? "", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "AAAAAAAAAAAAAAAAAAAAAA==", PasswordHasher.DefaultIterations);
                return (new LoginResult(LoginOutcome.InvalidCredentials, "The username or password is incorrect."), (AppUser?)null);
            }

            if (!u.IsActive) return (new LoginResult(LoginOutcome.Disabled, "This account has been disabled."), null);
            if (u.LockoutUntil is { } until && until > clock.Now)
                return (new LoginResult(LoginOutcome.LockedOut, $"Too many failed attempts. Try again after {until:t}."), null);

            if (!PasswordHasher.Verify(password ?? "", u.PasswordHash, u.PasswordSalt, u.PasswordIterations))
            {
                u.FailedLoginCount++;
                if (u.FailedLoginCount >= Math.Max(1, s.MaxFailedLogins))
                {
                    u.LockoutUntil = clock.Now.AddMinutes(Math.Max(1, s.LockoutMinutes));
                    u.FailedLoginCount = 0;
                    db.AuditLogs.Add(new AuditLog { Timestamp = clock.Now, UserId = u.Id, UserName = u.DisplayName, Action = "Login.Lockout", EntityType = "User", EntityId = u.Id, Summary = "Account temporarily locked after failed sign-in attempts" });
                }

                await db.SaveChangesAsync(c);
                return (new LoginResult(LoginOutcome.InvalidCredentials, "The username or password is incorrect."), null);
            }

            u.FailedLoginCount = 0;
            u.LockoutUntil = null;
            u.LastLoginAt = clock.Now;
            db.AuditLogs.Add(new AuditLog { Timestamp = clock.Now, UserId = u.Id, UserName = u.DisplayName, Action = "Login", EntityType = "User", EntityId = u.Id, Summary = "Signed in" });
            await db.SaveChangesAsync(c);
            return (new LoginResult(LoginOutcome.Success, "Welcome"), u);
        }, ct);

        if (result.Item2 is { } user)
        {
            session.SignIn(user.Id, user.Username, user.DisplayName, user.Role, user.StaffMemberId);
            logger.LogInformation("User {UserId} signed in with role {Role}", user.Id, user.Role);
        }

        return result.Item1;
    }

    /// <summary>Re-verifies the signed-in user's password (used to unlock an idle session).</summary>
    public async Task<bool> VerifyCurrentUserPasswordAsync(string password, CancellationToken ct = default)
    {
        if (session.UserId is not { } id) return false;
        await using var db = dbFactory.CreateReadContext();
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return u is { IsActive: true } && PasswordHasher.Verify(password, u.PasswordHash, u.PasswordSalt, u.PasswordIterations);
    }

    public void Logout() => session.SignOut();

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (session.UserId is not { } id) throw new PermissionDeniedException("change the password");
        var errors = PasswordHasher.Validate(newPassword, session.Username);
        if (errors.Count > 0) throw new ValidationException(errors);
        if (currentPassword == newPassword) throw new ValidationException("The new password must be different.");

        await writer.ExecuteAsync(async (db, c) =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == id, c);
            if (!PasswordHasher.Verify(currentPassword, u.PasswordHash, u.PasswordSalt, u.PasswordIterations))
                throw new ValidationException("The current password is incorrect.");
            (u.PasswordHash, u.PasswordSalt, u.PasswordIterations) = PasswordHasher.Hash(newPassword);
            Audit.Add(db, session, clock, "User.PasswordChanged", "User", id, "Changed own password");
            await db.SaveChangesAsync(c);
        }, ct);
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct = default)
    {
        session.Require(Permission.ManageUsers, "manage user accounts");
        await using var db = dbFactory.CreateReadContext();
        var now = clock.Now;
        return await db.Users.AsNoTracking().OrderBy(u => u.Role).ThenBy(u => u.DisplayName)
            .Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.Role, u.StaffMemberId,
                u.StaffMember != null ? u.StaffMember.FullName : null, u.IsActive, u.LastLoginAt,
                u.LockoutUntil != null && u.LockoutUntil > now))
            .ToListAsync(ct);
    }

    public async Task<int> CreateUserAsync(string username, string displayName, UserRole role, int? staffMemberId, string password, CancellationToken ct = default)
    {
        session.Require(Permission.ManageUsers, "create user accounts");
        var errors = new List<string>(ValidateUsername(username));
        if (string.IsNullOrWhiteSpace(displayName)) errors.Add("Display name is required.");
        errors.AddRange(PasswordHasher.Validate(password, username));
        if (role == UserRole.Staff && staffMemberId is null) errors.Add("Link Staff accounts to a staff profile so they can see their own calendar.");
        if (errors.Count > 0) throw new ValidationException(errors);

        var (hash, salt, iterations) = PasswordHasher.Hash(password);
        var id = await writer.ExecuteAsync(async (db, c) =>
        {
            var normalized = username.Trim().ToUpperInvariant();
            if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, c)) throw new ValidationException("That username is already taken.");
            var u = new AppUser
            {
                Username = username.Trim(), NormalizedUsername = normalized, DisplayName = displayName.Trim(), Role = role,
                StaffMemberId = staffMemberId, PasswordHash = hash, PasswordSalt = salt, PasswordIterations = iterations,
                IsActive = true, CreatedAt = clock.Now,
            };
            db.Users.Add(u);
            await db.SaveChangesAsync(c);
            Audit.Add(db, session, clock, "User.Created", "User", u.Id, $"Created {role} account '{u.Username}'");
            await db.SaveChangesAsync(c);
            return u.Id;
        }, ct);
        notifier.Publish(DataArea.Users);
        return id;
    }

    public async Task UpdateUserAsync(int userId, string displayName, UserRole role, int? staffMemberId, bool isActive, CancellationToken ct = default)
    {
        session.Require(Permission.ManageUsers, "edit user accounts");
        if (string.IsNullOrWhiteSpace(displayName)) throw new ValidationException("Display name is required.");
        if (role == UserRole.Staff && staffMemberId is null) throw new ValidationException("Link Staff accounts to a staff profile.");

        await writer.ExecuteAsync(async (db, c) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, c) ?? throw new NotFoundException("User", userId);
            var losesOwner = u.Role == UserRole.Owner && u.IsActive && (role != UserRole.Owner || !isActive);
            if (losesOwner && await db.Users.CountAsync(x => x.Role == UserRole.Owner && x.IsActive, c) <= 1)
                throw new ValidationException("At least one active Owner account must remain.");
            if (userId == session.UserId && !isActive) throw new ValidationException("You cannot disable your own account.");

            u.DisplayName = displayName.Trim();
            u.Role = role;
            u.StaffMemberId = staffMemberId;
            u.IsActive = isActive;
            Audit.Add(db, session, clock, "User.Updated", "User", u.Id, $"Updated account '{u.Username}' ({role}, {(isActive ? "active" : "disabled")})");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Users);
    }

    public async Task ResetPasswordAsync(int userId, string newPassword, CancellationToken ct = default)
    {
        session.Require(Permission.ManageUsers, "reset passwords");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, c) ?? throw new NotFoundException("User", userId);
            var errors = PasswordHasher.Validate(newPassword, u.Username);
            if (errors.Count > 0) throw new ValidationException(errors);
            (u.PasswordHash, u.PasswordSalt, u.PasswordIterations) = PasswordHasher.Hash(newPassword);
            u.FailedLoginCount = 0;
            u.LockoutUntil = null;
            Audit.Add(db, session, clock, "User.PasswordReset", "User", u.Id, $"Reset password for '{u.Username}'");
            await db.SaveChangesAsync(c);
        }, ct);
    }

    private static IEnumerable<string> ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 3) yield return "Username must be at least 3 characters.";
        else if (!username.Trim().All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or '@'))
            yield return "Username may contain letters, digits, '.', '_', '-' and '@' only.";
    }
}
