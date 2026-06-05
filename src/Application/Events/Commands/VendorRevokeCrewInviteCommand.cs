using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Vendor revokes a pending crew invitation they sent. Soft-deletes the
/// assignment row so the crew stops seeing it in their My Assignments page,
/// and frees the slot for someone else. Mirrors the manager's
/// RevokeVendorInviteCommand pattern — only valid while Status == Invited.
/// </summary>
public sealed record VendorRevokeCrewInviteCommand(
    Guid EventId,
    Guid CrewId,
    Guid VendorUserId
) : IRequest<Result>;

public sealed class VendorRevokeCrewInviteHandler
    : IRequestHandler<VendorRevokeCrewInviteCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public VendorRevokeCrewInviteHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db = db; _uow = uow; _push = push;
    }

    public async Task<Result> Handle(VendorRevokeCrewInviteCommand req, CancellationToken ct)
    {
        // Find the active (non-deleted) invite row for this (event, crew)
        // pair owned by the acting vendor.
        var a = await _db.EventAssignments
            .Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.EventId == req.EventId
                                   && x.CrewId  == req.CrewId
                                   && x.VendorId == req.VendorUserId, ct);
        if (a is null)
            return Result.Failure(new Error("Invitation.NotFound", "Invitation not found."));

        if (a.Status != AssignmentStatus.Invited)
            return Result.Failure(new Error("Invitation.AlreadyResponded",
                "The crew has already responded — you can't revoke a non-pending invite."));

        try
        {
            a.VendorRevokeCrewInvite(req.VendorUserId);
        }
        catch (System.InvalidOperationException ex)
        {
            return Result.Failure(new Error("Invitation.NotRevokable", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);

        await _push.PushToUserAsync(req.CrewId, "CrewInviteRevoked", new
        {
            assignmentId = a.Id,
            eventId      = a.EventId,
            eventTitle   = a.Event.Title
        }, ct);

        return Result.Success();
    }
}
