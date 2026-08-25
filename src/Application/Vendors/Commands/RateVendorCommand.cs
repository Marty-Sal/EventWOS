using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Ratings;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Vendors.Commands;

/// <summary>
/// An Admin or Manager rates a Vendor's performance and cooperation on a
/// specific event -- prompted when the event is marked Completed.
///
/// Event-scoped on purpose. The previous version rated a vendor globally with a
/// single number that OVERWROTE the last one, so a vendor's second event erased
/// the first and no average was ever possible.
/// </summary>
public sealed record RateVendorCommand(
    Guid    EventId,
    Guid    VendorUserId,
    int     Performance,
    int     Cooperation,
    string? Comment = null
) : IRequest<Result>;

public sealed class RateVendorHandler : IRequestHandler<RateVendorCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    private readonly RatingWriter  _ratings;
    private readonly ICurrentUser  _me;

    public RateVendorHandler(IAppDbContext db, IUnitOfWork uow, RatingWriter ratings, ICurrentUser me)
    {
        _db      = db;
        _uow     = uow;
        _ratings = ratings;
        _me      = me;
    }

    public async Task<Result> Handle(RateVendorCommand req, CancellationToken ct)
    {
        if (_me.UserId is null)
            return Result.Failure(new Error("Auth.Required", "You must be signed in to rate."));

        // Vendors rating themselves or each other would make the score meaningless.
        if (_me.Role is not (UserRole.Admin or UserRole.Manager))
            return Result.Failure(new Error("Rating.Forbidden",
                "Only an Admin or Manager can rate a vendor."));

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId && !e.IsDeleted, ct);
        if (ev is null)
            return Result.Failure(new Error("Event.NotFound", "Event not found."));

        // Rating before the work is finished would score an event still in flight.
        if (ev.Status != EventStatus.Completed)
            return Result.Failure(new Error("Rating.EventNotCompleted",
                "A vendor can only be rated once the event is marked completed."));

        var vendor = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.VendorUserId && !u.IsDeleted, ct);
        if (vendor is null)
            return Result.Failure(new Error("Vendor.NotFound", "Vendor not found."));

        // A vendor who never worked this event must not collect a rating for it --
        // otherwise anyone's average can be moved by events they had no part in.
        var worked = await _db.VendorShiftAllocations
                .AnyAsync(a => a.VendorId == req.VendorUserId
                            && a.Shift!.EventId == req.EventId
                            && !a.IsDeleted, ct)
            || await _db.EventAssignments
                .AnyAsync(a => a.VendorId == req.VendorUserId
                            && a.EventId  == req.EventId
                            && !a.IsDeleted, ct);

        if (!worked)
            return Result.Failure(new Error("Rating.VendorNotOnEvent",
                "That vendor was not assigned to this event."));

        var write = await _ratings.UpsertAsync(
            eventId:       req.EventId,
            subjectUserId: req.VendorUserId,
            subjectType:   RatingSubjectType.Vendor,
            raterUserId:   _me.UserId.Value,
            performance:   req.Performance,
            cooperation:   req.Cooperation,
            comment:       req.Comment,
            assignmentId:  null,          // Vendor ratings are event-wide.
            ct:            ct);

        if (write.IsFailure) return Result.Failure(write.Error!);

        await _ratings.RecomputeCacheAsync(req.VendorUserId, RatingSubjectType.Vendor, ct);
        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
