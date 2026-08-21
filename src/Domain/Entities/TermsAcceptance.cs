using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Append-only audit record: "this User accepted this exact Version of
/// this Audience's Terms &amp; Conditions, at this time." Never mutated or
/// deleted — CreatedAt (from BaseEntity) IS the acceptance timestamp.
///
/// Written in two places:
///   1. RegisterVendorHandler / RegisterCrewHandler — at self-registration,
///      using the new user's own Id (same "write before the User row is
///      saved, same SaveChanges call" pattern already used for file
///      uploads in those handlers).
///   2. AcceptTermsHandler — when an existing user is prompted to
///      re-accept after Admin publishes a new Version (post-login gate).
///
/// A user's "have they accepted the CURRENT version" check is:
///   EXISTS (SELECT 1 FROM terms_acceptances
///           WHERE user_id = X AND audience = Y AND version = currentVersion)
/// </summary>
public sealed class TermsAcceptance : BaseEntity
{
    private TermsAcceptance() { }

    public TermsAcceptance(Guid userId, TermsAudience audience, int version)
    {
        UserId   = userId;
        Audience = audience;
        Version  = version;
    }

    public Guid           UserId   { get; private set; }
    public TermsAudience  Audience { get; private set; }
    public int            Version  { get; private set; }
}
