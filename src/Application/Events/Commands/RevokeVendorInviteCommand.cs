using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
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
