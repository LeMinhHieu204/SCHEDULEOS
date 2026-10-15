using System.Security.Cryptography;

namespace ScheduleOS.Application.Security;

/// <summary>Salted PBKDF2-HMAC-SHA256 password hashing.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (string Hash, string Salt, int Iterations) Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string hashBase64, string saltBase64, int iterations)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "admin123", "123456", "12345678", "password", "password1", "qwerty", "letmein", "scheduleos",
    };

    /// <summary>Returns validation errors for a new password (empty when acceptable).</summary>
    public static IReadOnlyList<string> Validate(string? password, string? username = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < 8) errors.Add("Password must be at least 8 characters long.");
        else
        {
            if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) errors.Add("Password must contain letters and numbers.");
            if (Forbidden.Contains(password)) errors.Add("This password is too common.");
            if (username is not null && password.Equals(username, StringComparison.OrdinalIgnoreCase)) errors.Add("Password must differ from the username.");
        }

        return errors;
    }
}
