using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>Crew confirms or declines their assignment invitation.</summary>
public sealed record RespondAssignmentCommand(
    Guid    AssignmentId,
    Guid    CrewId,
    string  Response,    // "confirm" | "decline"
    string? Reason = null
) : IRequest<Result>;

public sealed class RespondAssignmentHandler : IRequestHandler<RespondAssignmentCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public RespondAssignmentHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result> Handle(RespondAssignmentCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && a.CrewId == req.CrewId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        bool accepted;
        try
        {
            if (req.Response.Equals("confirm", StringComparison.OrdinalIgnoreCase))
            {
                assignment.CrewAccept();
                accepted = true;
            }
            else if (req.Response.Equals("decline", StringComparison.OrdinalIgnoreCase))
            {
                assignment.CrewDecline(req.Reason);
                accepted = false;
            }
            else
                return Result.Failure(new Error("Assignment.InvalidResponse", "Response must be 'confirm' or 'decline'."));
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);

        // Notify vendor of crew response
        var notifEvent = accepted ? "CrewAccepted" : "CrewDeclined";
        await _push.PushToUserAsync(assignment.VendorId, notifEvent, new
        {
            assignmentId = assignment.Id,
            crewName     = assignment.Crew?.FullName ?? "Crew member",
            reason       = req.Reason
        }, ct);

        return Result.Success();
    }
}
