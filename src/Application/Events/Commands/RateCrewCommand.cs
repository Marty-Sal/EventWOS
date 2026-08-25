using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Ratings;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Events.Commands;

/// <summary>
/// A Vendor rates one of their crew members after that person has attended --
/// in practice, prompted at checkout.
///
/// Scored on two axes (performance, cooperation) and stored against the EVENT,
/// not the shift. A crew member working three shifts at one event is rated once;
/// re-rating them revises that single rating rather than stacking votes.
/// </summary>
public sealed record RateCrewCommand(
    Guid    AssignmentId,
    Guid    VendorUserId,
    int     Performance,
    int     Cooperation,
    string? Comment = null
) : IRequest<Result>;

public sealed class RateCrewHandler : IRequestHandler<RateCrewCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    private readonly RatingWriter  _ratings;

    public RateCrewHandler(IAppDbContext db, IUnitOfWork uow, RatingWriter ratings)
    {
        _db      = db;
        _uow     = uow;
        _ratings = ratings;
    }

    public async Task<Result> Handle(RateCrewCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && !a.IsDeleted, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        // Only the vendor who owns this assignment may rate its crew member.
        if (assignment.VendorId != req.VendorUserId)
            return Result.Failure(new Error("Assignment.Forbidden",
                "You can only rate crew members assigned by you."));

        if (assignment.CrewId is null)
            return Result.Failure(new Error("Assignment.NoCrew",
                "This is a placeholder invite with no crew member on it yet."));

        if (!assignment.IsRateable)
            return Result.Failure(new Error("Rating.NotAttended",
                "You can only rate a crew member once they have attended."));

        var write = await _ratings.UpsertAsync(
            eventId:       assignment.EventId,
            subjectUserId: assignment.CrewId.Value,
            subjectType:   RatingSubjectType.Crew,
            raterUserId:   req.VendorUserId,
            performance:   req.Performance,
            cooperation:   req.Cooperation,
            comment:       req.Comment,
            assignmentId:  assignment.Id,
            ct:            ct);

        if (write.IsFailure) return Result.Failure(write.Error!);

        // Mirror onto the assignment so the vendor's existing assignment list
        // still shows stars, then rebuild the crew member's cached average from
        // the full rating set.
        assignment.MirrorRatingScore(write.Value!.Score);
        await _ratings.RecomputeCacheAsync(assignment.CrewId.Value, RatingSubjectType.Crew, ct);

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
