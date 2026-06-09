using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Rules;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Manager revokes a vendor invitation that the vendor hasn't responded to yet.
/// Soft-deletes the placeholder row so the vendor stops seeing the event in
/// their pending invitations. Only valid while Status == Invited and the row
/// is a placeholder (CrewId == null).
/// </summary>
public sealed record RevokeVendorInviteCommand(
    Guid AssignmentId,
    Guid RevokedByUserId
) : IRequest<Result>;

public sealed class RevokeVendorInviteHandler
    : IRequestHandler<RevokeVendorInviteCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public RevokeVendorInviteHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db = db; _uow = uow; _push = push;
    }

    public async Task<Result> Handle(RevokeVendorInviteCommand req, CancellationToken ct)
    {
        var a = await _db.EventAssignments
            .Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == req.AssignmentId, ct);
        if (a is null)
            return Result.Failure(new Error("Invitation.NotFound", "Invitation not found."));

        if (a.CrewId is not null)
            return Result.Failure(new Error("Invitation.NotAPlaceholder",
                "Only vendor invitation rows can be revoked here."));
        if (a.Status != AssignmentStatus.Invited)
            return Result.Failure(new Error("Invitation.AlreadyResponded",
                "The vendor has already responded to this invitation."));

        a.IsDeleted = true;
        a.DeletedAt = DateTime.UtcNow;
        a.DeletedBy = req.RevokedByUserId;

        // Phase D step 6: keep VendorShiftAllocation in sync with reality.
        // The placeholder we're revoking was counted toward the vendor's
        // quota when the admin created it. If we leave the quota intact,
        // the Vendor Quotas panel will read "Allocated 5" when only 4
        // placeholders actually exist — same kind of lie this step is
        // designed to kill.
        //
        // Rules:
        //   - Skip when the row has no ShiftId or no VendorId (legacy).
        //   - If the vendor has an active allocation row for this shift:
        //       Quota > 1 -> decrement by 1
        //       Quota == 1 -> archive the allocation outright (UpdateQuota
        //                     forbids 0). currentSeatsOccupied is 0 for a
        //                     placeholder row, so Archive is safe.
        if (a.ShiftId.HasValue && a.VendorId.HasValue)
        {
            var alloc = await _db.VendorShiftAllocations
                .FirstOrDefaultAsync(x =>
                    x.ShiftId  == a.ShiftId.Value &&
                    x.VendorId == a.VendorId.Value &&
                    !x.IsDeleted, ct);

            if (alloc is not null)
            {
                // Occupied seats for THIS vendor on THIS shift, AFTER the
                // revoke commits. Since we're revoking a placeholder (no
                // crew), the count is unchanged from now, but we still
                // compute it from the DB so future callers stay correct.
                var occupiedAfter = await _db.EventAssignments
                    .Where(AssignmentCapacityRules.OccupiesSeatOnShift(a.ShiftId.Value))
                    .CountAsync(x => x.VendorId == a.VendorId.Value, ct);

                try
                {
                    if (alloc.Quota > 1)
                        alloc.UpdateQuota(alloc.Quota - 1, occupiedAfter);
                    else
                        alloc.Archive(req.RevokedByUserId, occupiedAfter);
                }
                catch (Exception)
                {
                    // Belt-and-braces — if a real crew somehow blocks the
                    // shrink, swallow it: revoke still proceeds, admin can
                    // reconcile from the Quotas panel. Better than failing
                    // the whole revoke.
                }
            }
        }

        await _uow.SaveChangesAsync(ct);

        if (a.VendorId.HasValue)
        {
            await _push.PushToUserAsync(a.VendorId.Value, "VendorInviteRevoked", new
            {
                assignmentId = a.Id,
                eventId      = a.EventId,
                eventTitle   = a.Event.Title
            }, ct);
        }

        return Result.Success();
    }
}
