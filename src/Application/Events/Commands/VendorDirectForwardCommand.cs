using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Vendor directly forwards an Invited crew member to Manager approval,
/// bypassing the crew acceptance step. Useful when vendor already has 
/// confirmation offline or needs to proceed without waiting for crew to respond.
/// </summary>
public sealed record VendorDirectForwardCommand(
    Guid AssignmentId,
    Guid VendorUserId
) : IRequest<Result>;

public sealed class VendorDirectForwardHandler : IRequestHandler<VendorDirectForwardCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public VendorDirectForwardHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result> Handle(VendorDirectForwardCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            .FirstOrDefaultAsync(
                a => a.Id == req.AssignmentId && a.VendorId == req.VendorUserId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found or not yours."));

        try
        {
            // Accept on behalf of crew, then immediately forward to manager
            assignment.VendorDirectForward();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);

        // Notify crew that vendor forwarded them (they were bypassed)
        await _push.PushToUserAsync(assignment.CrewId, "VendorApprovedYou", new
        {
            assignmentId = assignment.Id,
            crewName     = assignment.Crew?.FullName ?? "Crew"
        }, ct);

        // Notify all managers about new item in approval queue
        await _push.PushToRoleAsync("manager", "PendingManagerApproval", new
        {
            assignmentId = assignment.Id,
            crewName     = assignment.Crew?.FullName ?? "Crew"
        }, ct);

        return Result.Success();
    }
}
