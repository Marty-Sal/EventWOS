using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Announcements;

/// <summary>
/// Shared audience/visibility rules for event announcements, kept in one
/// place because three handlers need exactly the same answers: who receives
/// a broadcast, and who is allowed to read one back later.
/// </summary>
public static class AnnouncementAccess
{
    /// <summary>Does this audience include the given role?</summary>
    public static bool Includes(AnnouncementAudience audience, UserRole role) => role switch
    {
        UserRole.Vendor => audience is AnnouncementAudience.Vendors or AnnouncementAudience.Both,
        UserRole.Crew   => audience is AnnouncementAudience.Crew    or AnnouncementAudience.Both,
        // Admin/Manager aren't "recipients" — they see everything via the
        // event screen instead (handled by the privileged branch below).
        _ => false
    };

    /// <summary>
    /// Resolves the concrete recipient user ids for an event + audience.
    ///
    /// Vendors come from BOTH sources on purpose: an EventAssignment row
    /// (the actual invite) and a VendorShiftAllocation (seat quota granted
    /// without an invite yet) — either one means "this vendor is working
    /// this event" and should hear about it.
    /// </summary>
    public static async Task<List<Guid>> ResolveRecipientIdsAsync(
        IAppDbContext db, Guid eventId, AnnouncementAudience audience, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();

        if (audience is AnnouncementAudience.Vendors or AnnouncementAudience.Both)
        {
            var fromAssignments = await db.EventAssignments
                .Where(a => a.EventId == eventId && !a.IsDeleted && a.VendorId != null)
                .Select(a => a.VendorId!.Value)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in fromAssignments) ids.Add(id);

            var shiftIds = await db.EventShifts
                .Where(s => s.EventId == eventId && !s.IsDeleted)
                .Select(s => s.Id)
                .ToListAsync(ct);

            if (shiftIds.Count > 0)
            {
                var fromQuotas = await db.VendorShiftAllocations
                    .Where(q => shiftIds.Contains(q.ShiftId) && !q.IsDeleted)
                    .Select(q => q.VendorId)
                    .Distinct()
                    .ToListAsync(ct);
                foreach (var id in fromQuotas) ids.Add(id);
            }
        }

        if (audience is AnnouncementAudience.Crew or AnnouncementAudience.Both)
        {
            var crewIds = await db.EventAssignments
                .Where(a => a.EventId == eventId && !a.IsDeleted && a.CrewId != null)
                .Select(a => a.CrewId!.Value)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in crewIds) ids.Add(id);
        }

        return ids.ToList();
    }

    /// <summary>
    /// Is this (non-privileged) user connected to the event at all — i.e. may
    /// they read its announcement history? Audience filtering happens on top
    /// of this, per announcement.
    ///
    /// Note this is evaluated LIVE rather than against a recipient snapshot,
    /// which is what makes "whoever sees this later can see all the
    /// notifications" work for someone assigned after the fact.
    /// </summary>
    public static async Task<bool> IsConnectedToEventAsync(
        IAppDbContext db, Guid eventId, Guid userId, UserRole role, CancellationToken ct)
    {
        if (role == UserRole.Vendor)
        {
            var invited = await db.EventAssignments
                .AnyAsync(a => a.EventId == eventId && !a.IsDeleted && a.VendorId == userId, ct);
            if (invited) return true;

            return await db.VendorShiftAllocations
                .Where(q => !q.IsDeleted && q.VendorId == userId)
                .Join(db.EventShifts.Where(s => s.EventId == eventId && !s.IsDeleted),
                      q => q.ShiftId, s => s.Id, (q, s) => q.Id)
                .AnyAsync(ct);
        }

        if (role == UserRole.Crew)
        {
            return await db.EventAssignments
                .AnyAsync(a => a.EventId == eventId && !a.IsDeleted && a.CrewId == userId, ct);
        }

        return false;
    }
}
