using EventWOS.Application.Auth.Interfaces;

namespace EventWOS.Infrastructure.Auth;

/// <summary>
/// BCrypt-based password hashing. Work factor 11 is the project default —
/// ~250ms on Railway's standard container, slow enough to deter offline
/// brute force, fast enough not to be a UX problem on login.
/// </summary>
public sealed class BCryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 11;

    public string Hash(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password is required.", nameof(password));
        return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(hash))
            return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // Corrupt / non-BCrypt hash → treat as failed login, never crash.
            return false;
        }
    }
}
