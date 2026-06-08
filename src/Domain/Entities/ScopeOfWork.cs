using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Admin-maintained global catalog of work categories used to staff events.
/// Examples: "Box Office", "Gates", "Accreditation", "F&amp;B", "Inventory Management".
///
/// One catalog for the whole platform — anyone creating an event picks from
/// this list. NOT per-vendor, NOT per-event-template, NOT per-organisation.
/// Confirmed scope with the user before this entity was modelled (see commit
/// trail and memory rule on Phase A of the Scope-of-Work feature).
///
/// Lifecycle:
///   - Created by Admin (scope_of_work:write).
///   - Soft-deletable via BaseEntity.IsDeleted. We never hard-delete because
///     historical events / shifts will reference these rows by FK after
///     Phase B, and yanking the row out from under them would destroy
///     auditability. "Archive" in the UI is just IsDeleted = true.
///   - Restorable (un-archive) by clearing IsDeleted.
///
/// Uniqueness:
///   - Name is unique across the catalog (case-insensitive) AMONG ACTIVE
///     rows only — archiving "F&amp;B" and creating a new "F&amp;B" must work.
///     Enforced by a filtered unique index in the migration (matches the
///     pattern used by crew_group_members per memory rule #25).
/// </summary>
public sealed class ScopeOfWork : BaseEntity
{
    private ScopeOfWork() { }

    public ScopeOfWork(string name, string? description, Guid createdByUserId)
    {
        SetName(name);
        Description     = NormaliseDescription(description);
        CreatedByUserId = createdByUserId;
    }

    public string  Name            { get; private set; } = default!;
    public string? Description     { get; private set; }
    public Guid    CreatedByUserId { get; private set; }

    // ── Behaviours ────────────────────────────────────────────────────────────

    public void Update(string name, string? description)
    {
        if (IsDeleted)
            throw new InvalidOperationException("Cannot edit an archived scope of work — restore it first.");

        SetName(name);
        Description = NormaliseDescription(description);
        UpdatedAt   = DateTime.UtcNow;
    }

    /// <summary>Soft-delete. Idempotent — calling on an already-archived row is a no-op.</summary>
    public void Archive(Guid actingUserId)
    {
        if (IsDeleted) return;
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
        DeletedBy = actingUserId;
    }

    /// <summary>Un-archive. Idempotent on already-active rows.</summary>
    public void Restore()
    {
        if (!IsDeleted) return;
        IsDeleted = false;
        DeletedAt = null;
        DeletedBy = null;
        UpdatedAt = DateTime.UtcNow;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Scope of work name is required.", nameof(name));
        var trimmed = name.Trim();
        if (trimmed.Length > 80)
            throw new ArgumentException("Scope of work name must be 80 characters or fewer.", nameof(name));
        Name = trimmed;
    }

    private static string? NormaliseDescription(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return null;
        var trimmed = desc.Trim();
        if (trimmed.Length > 500)
            throw new ArgumentException("Description must be 500 characters or fewer.", nameof(desc));
        return trimmed;
    }
}
