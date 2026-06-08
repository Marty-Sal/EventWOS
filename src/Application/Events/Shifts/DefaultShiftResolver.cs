using EventWOS.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Shifts;

/// <summary>
/// Phase B scaffolding: until the assignment UI gains a shift picker
/// (Phase C), every new assignment auto-attaches to the event's single
/// active shift. This helper resolves that shift; if an event somehow
/// ends up with zero or multiple active shifts in this window we surface
/// a clear error rather than picking one at random.
///
/// MULTI-SHIFT note: when Phase C lands, AssignCrew / VendorAssignCrew
/// will accept an explicit shiftId. This helper will then ONLY be used
/// as the legacy fallback for events created before Phase C. New events
/// flow through the explicit path.
/// </summary>
public static class DefaultShiftResolver
{
    /// <summary>
    /// Returns the Id of the single active shift on this event.
    ///   • Zero active shifts → null (caller surfaces "event has no shifts").
    ///   • Exactly one        → its Id.
    ///   • More than one      → null with <paramref name="ambiguous"/> = true
    ///                          (caller must require explicit shiftId).
    /// </summary>
    public static async Task<Guid?> ResolveAsync(
        IAppDbContext db,
        Guid eventId,
        CancellationToken ct,
        Action<bool>? ambiguous = null)
    {
        var shiftIds = await db.EventShifts
            .Where(s => s.EventId == eventId)
            .Select(s => s.Id)
            .Take(2)
            .ToListAsync(ct);

        switch (shiftIds.Count)
        {
            case 1:
                ambiguous?.Invoke(false);
                return shiftIds[0];
            case 0:
                ambiguous?.Invoke(false);
                return null;
            default:
                ambiguous?.Invoke(true);
                return null;
        }
    }
}
