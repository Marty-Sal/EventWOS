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
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public RespondAssignmentHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(RespondAssignmentCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && a.CrewId == req.CrewId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        try
        {
            if (req.Response.Equals("confirm", StringComparison.OrdinalIgnoreCase))
                assignment.CrewAccept();
            else if (req.Response.Equals("decline", StringComparison.OrdinalIgnoreCase))
                assignment.CrewDecline(req.Reason);
            else
                return Result.Failure(new Error("Assignment.InvalidResponse", "Response must be 'confirm' or 'decline'."));
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
