using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Vendor accepts or rejects the Manager's invitation to staff an event.
/// Operates on the placeholder row (CrewId == null, Status == Invited).
/// Does NOT touch the Manager approval queue — this is a vendor↔manager
/// decision about the event itself, not about any specific crew member.
/// </summary>
public sealed record VendorRespondToInviteCommand(
    Guid    AssignmentId,
    Guid    VendorUserId,
    string  Response,    // "accept" | "reject"
    string? Reason = null
) : IRequest<Result>;

public sealed class VendorRespondToInviteHandler
    : IRequestHandler<VendorRespondToInviteCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public VendorRespondToInviteHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result> Handle(VendorRespondToInviteCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a =>
                a.Id        == req.AssignmentId &&
                a.VendorId  == req.VendorUserId &&
                a.CrewId    == null, ct);

        if (assignment is null)
            return Result.Failure(new Error("Invitation.NotFound",
                "Invitation not found or not addressed to you."));

        bool accepted;
        try
        {
            if (req.Response.Equals("accept", StringComparison.OrdinalIgnoreCase))
            {
                assignment.VendorAcceptInvite();
                accepted = true;
            }
            else if (req.Response.Equals("reject", StringComparison.OrdinalIgnoreCase))
            {
                assignment.VendorRejectInvite(req.Reason ?? "No reason provided");
                accepted = false;
            }
            else
            {
                return Result.Failure(new Error("Invitation.InvalidResponse",
                    "Response must be 'accept' or 'reject'."));
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Invitation.InvalidTransition", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure(new Error("Invitation.InvalidInput", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);

        // Notify the manager who originally assigned the vendor to the event.
        var notif = accepted ? "VendorAcceptedEvent" : "VendorRejectedEvent";
        await _push.PushToUserAsync(assignment.AssignedByUserId, notif, new
        {
            assignmentId = assignment.Id,
            eventId      = assignment.EventId,
            eventTitle   = assignment.Event.Title,
            reason       = req.Reason
        }, ct);

        return Result.Success();
    }
}
