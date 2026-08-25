using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Rules;
using EventOpsOracle.Shared.Result;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Ratings;

/// <summary>
/// Shared write path for both rating flows (Admin rates Vendor, Vendor rates
/// Crew), plus the recompute that keeps the cached averages on User honest.
///
/// Both flows need identical care -- upsert instead of double-insert, then
/// recompute the cache from scratch -- and duplicating that in two handlers is
/// how the two sides drift apart.
///
/// The recompute is deliberately a FULL aggregation over the ratings table
/// rather than an incremental nudge of the existing average. Incremental was the
/// old design and it could not be corrected: once a star was folded into a mean,
/// the individual scores were gone. Full recompute costs one indexed GROUP BY
/// per rating written -- cheap, given a rating happens once per person per event
/// -- and makes the cache correct by construction and self-healing.
/// </summary>
public sealed class RatingWriter
{
    private readonly IAppDbContext _db;

    public RatingWriter(IAppDbContext db) => _db = db;

    /// <summary>
    /// Creates the rating, or revises it if this person was already rated for
    /// this event in this capacity.
    ///
    /// Upsert rather than insert-only because the alternative is worse in both
    /// directions: rejecting the second call strands a rater who mis-clicked a
    /// star with no way to fix it, and inserting blindly trips the partial
    /// unique index and surfaces as a raw 500.
    /// </summary>
    public async Task<Result<Rating>> UpsertAsync(
        Guid              eventId,
        Guid              subjectUserId,
        RatingSubjectType subjectType,
        Guid              raterUserId,
        int               performance,
        int               cooperation,
        string?           comment,
        Guid?             assignmentId,
        CancellationToken ct)
    {
        if (!ReputationRules.IsValidScore(performance))
            return Result.Failure<Rating>(new Error("Rating.OutOfRange",
                $"Performance must be between {ReputationRules.MinScore} and {ReputationRules.MaxScore}."));
        if (!ReputationRules.IsValidScore(cooperation))
            return Result.Failure<Rating>(new Error("Rating.OutOfRange",
                $"Cooperation must be between {ReputationRules.MinScore} and {ReputationRules.MaxScore}."));
        if (subjectUserId == raterUserId)
            return Result.Failure<Rating>(new Error("Rating.SelfRating",
                "You cannot rate yourself."));

        var existing = await _db.Ratings.FirstOrDefaultAsync(
            r => r.EventId       == eventId
              && r.SubjectUserId == subjectUserId
              && r.SubjectType   == subjectType, ct);

        Rating rating;
        if (existing is null)
        {
            rating = new Rating(eventId, subjectUserId, subjectType, raterUserId,
                                performance, cooperation, comment, assignmentId);
            _db.Ratings.Add(rating);
        }
        else
        {
            existing.Revise(performance, cooperation, comment, raterUserId);
            rating = existing;
        }

        return Result.Success(rating);
    }

    /// <summary>
    /// Rewrites the cached average on User from the ratings table.
    ///
    /// Must be called AFTER the rating is in the change tracker but is computed
    /// against the tracked set, so a rating saved in the same unit of work is
    /// included. Reading the average with a server-side aggregate here would
    /// miss the pending row -- exactly the mistake that produced the max_crew
    /// drift bug, where SUM() saw only committed rows and baked a stale total.
    /// </summary>
    public async Task RecomputeCacheAsync(
        Guid subjectUserId, RatingSubjectType subjectType, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == subjectUserId, ct);
        if (user is null) return;

        // Committed rows for this subject...
        var persisted = await _db.Ratings
            .Where(r => r.SubjectUserId == subjectUserId && r.SubjectType == subjectType)
            .Select(r => new { r.Id, r.Performance, r.Cooperation })
            .ToListAsync(ct);

        // ...merged with anything still pending in this unit of work, so a rating
        // written moments ago counts. Local wins on Id, since a revision changes
        // the axes of a row that is already persisted with its old values.
        var local = _db.Ratings.Local
            .Where(r => r.SubjectUserId == subjectUserId
                     && r.SubjectType   == subjectType
                     && !r.IsDeleted)
            .Select(r => new { r.Id, r.Performance, r.Cooperation })
            .ToList();

        var merged = persisted
            .Where(p => local.All(l => l.Id != p.Id))
            .Concat(local)
            .Select(r => (r.Performance, r.Cooperation))
            .ToList();

        var average = ReputationRules.Average(merged);
        var count   = merged.Count;

        if (subjectType == RatingSubjectType.Crew)
            user.SetCrewReputation(average, count);
        else
            user.SetVendorReputation(average, count);
    }
}
