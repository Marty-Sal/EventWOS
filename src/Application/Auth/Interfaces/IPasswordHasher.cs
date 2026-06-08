namespace EventWOS.Application.Auth.Interfaces;

/// <summary>
/// Hashes and verifies user passwords. Implementation chooses the algorithm
/// (BCrypt is the default in Infrastructure). Plaintext passwords NEVER leave
/// the boundary — handlers call Hash() before persisting and Verify() during
/// login. The domain layer only ever sees the hash.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}
