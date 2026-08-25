namespace EventOpsOracle.Domain.Enums;

/// <summary>
/// Which self-registration flow a Terms &amp; Conditions document applies to.
/// Owner requires two SEPARATE documents — Vendor and Crew must not share
/// a single T&amp;C, since the two roles have different obligations.
/// </summary>
public enum TermsAudience
{
    Vendor = 0,
    Crew   = 1
}
