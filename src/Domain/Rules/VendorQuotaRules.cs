namespace EventWOS.Domain.Rules;

/// <summary>
/// Outcome of a quota check for "can this vendor invite one more crew
/// onto this shift?". The check happens in the handler (needs DB access)
/// so this enum + helper just centralises the decision shape.
///
/// Phase C step 3 of Scope-of-Work feature.
///
/// IMPORTANT — legacy fallback: events created before Phase C have NO
/// vendor allocations on their shifts. Hard-blocking them would freeze
/// every in-flight event in production. So the gate is "opt-in per
/// shift": if no allocation rows exist at all for the shift, callers
/// fall through to the old behaviour (capacity-only). Once a single
/// allocation exists, every vendor on that shift must have one — the
/// shift has effectively opted in to quota-managed staffing.
/// </summary>
public enum VendorQuotaCheck
{
    /// <summary>Shift has no allocations — fall back to legacy capacity-only check.</summary>
    NotEnforcedYet,

    /// <summary>Shift IS using allocations and this vendor has headroom.</summary>
    Allowed,

    /// <summary>Shift is using allocations but this vendor has none — they need one to staff.</summary>
    NoAllocation,

    /// <summary>Vendor has an allocation but has already filled it.</summary>
    QuotaExhausted,
}

/// <summary>
/// Result tuple from a quota check. <see cref="Quota"/> and
/// <see cref="CurrentlyAssigned"/> are only meaningful when
/// <see cref="Status"/> is Allowed or QuotaExhausted (they carry the
/// numbers the group handler decrements through its loop, and the
/// error message uses them too).
/// </summary>
public readonly record struct VendorQuotaCheckResult(
    VendorQuotaCheck Status,
    int              Quota,
    int              CurrentlyAssigned)
{
    public int Remaining => Quota - CurrentlyAssigned;
}
