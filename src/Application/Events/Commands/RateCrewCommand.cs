using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Vendor rates a crew member after an event (1–5 stars).
/// Must be called on an Attended assignment.
/// Updates both the assignment's VendorRating and the crew user's rolling CrewRating.
/// </summary>
public sealed record RateCrewCommand(
    Guid    AssignmentId,
    Guid    VendorUserId,
    decimal Rating         // 1–5
) : IRequest<Result>;

public sealed class RateCrewHandler : IRequestHandler<RateCrewCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public RateCrewHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db  = db;
        _uow = uow;
    }

    public async Task<Result> Handle(RateCrewCommand req, CancellationToken ct)
    {
        if (req.Rating < 1 || req.Rating > 5)
            return Result.Failure(new Error("Rating.OutOfRange", "Rating must be between 1 and 5."));

        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && !a.IsDeleted, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        // Only the vendor who made the assignment may rate
        if (assignment.VendorId != req.VendorUserId)
            return Result.Failure(new Error("Assignment.Forbidden",
                "You can only rate crew members assigned by you."));

        try { assignment.RateCrewMember(req.Rating); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Rating.Invalid", ex.Message)); }

        // Update crew member's rolling average
        assignment.Crew.AddCrewRating(req.Rating);

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
