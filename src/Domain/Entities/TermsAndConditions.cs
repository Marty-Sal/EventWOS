using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// One version of a Terms &amp; Conditions document for a given audience
/// (Vendor or Crew), managed from Settings → Terms &amp; Conditions.
///
/// Append-only version history — saving an edit in the admin UI creates a
/// NEW row with Version = previous max + 1 rather than mutating an
/// existing row. This is deliberate: once a Version has been shown to and
/// accepted by real users (see <see cref="TermsAcceptance"/>), that exact
/// text must stay retrievable for audit purposes, and bumping the version
/// is exactly the signal that drives the "please re-accept" flow — a user
/// who accepted Version 3 must be prompted again once Version 4 exists.
///
/// "Current" for an audience = the row with the highest Version among
/// non-deleted rows for that Audience.
/// </summary>
public sealed class TermsAndConditions : BaseEntity
{
    private TermsAndConditions() { }

    public TermsAndConditions(TermsAudience audience, int version, string content, Guid createdByUserId)
    {
        if (version < 1)
            throw new ArgumentException("Version must be 1 or greater.", nameof(version));

        Audience = audience;
        Version  = version;
        SetContent(content);
        CreatedBy = createdByUserId;
    }

    public TermsAudience Audience { get; private set; }
    public int            Version  { get; private set; }
    public string          Content  { get; private set; } = default!;

    private void SetContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Terms & Conditions content is required.", nameof(content));
        var trimmed = content.Trim();
        if (trimmed.Length > 20000)
            throw new ArgumentException("Terms & Conditions content must be 20,000 characters or fewer.", nameof(content));
        Content = trimmed;
    }
}
