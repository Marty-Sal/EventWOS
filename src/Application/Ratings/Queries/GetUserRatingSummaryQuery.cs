using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Rules;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Ratings.Queries;

/// <summary>One rated event, for the "recent feedback" list on a dashboard.</summary>
public sealed record RatingHistoryItemDto(
    Guid      RatingId,
    Guid      EventId,
    string    EventName,
    DateTime? EventDate,
    int       Performance,
    int       Cooperation,
    decimal   Score,
    string?   Comment,
    string?   RaterName,
    DateTime  RatedAt,
    bool      IsLegacySingleScore);

/// <summary>
/// A user's reputation, broken out rather than flattened to one number.
///
/// The two axes are reported separately because they lead to different actions:
/// a crew member who is technically excellent but hard to work with averages the
/// same 3.0 as someone mediocre at both, and those two people should not look
/// identical on a dashboard.
/// </summary>
public sealed record UserRatingSummaryDto(
    Guid     UserId,
    string   Role,
    decimal? Average,
    decimal? AveragePerformance,
    decimal? AverageCooperation,
    int      RatedEventCount,
    /// <summary>Count per star bucket, 1-5, so a lopsided spread is visible.</summary>
    IReadOnlyDictionary<int, int> Distribution,
    IReadOnlyList<RatingHistoryItemDto> Recent);

public sealed record GetUserRatingSummaryQuery(Guid UserId, int RecentCount = 10)
    : IRequest<Result<UserRatingSummaryDto>>;

public sealed class GetUserRatingSummaryHandler
    : IRequestHandler<GetUserRatingSummaryQuery, Result<UserRatingSummaryDto>>
{
    private readonly IAppDbContext _db;
    public GetUserRatingSummaryHandler(IAppDbContext db) => _db = db;

    public async Task<Result<UserRatingSummaryDto>> Handle(
        GetUserRatingSummaryQuery req, CancellationToken ct)
    {
        var user = await _db.Users
            .Where(u => u.Id == req.UserId && !u.IsDeleted)
            .Select(u => new { u.Id, u.Role })
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return Result.Failure<UserRatingSummaryDto>(new Error("User.NotFound", "User not found."));

        // Which capacity to report depends on what the person is. A rating is
        // filed under the capacity it was given in, so read that same axis back.
        var subjectType = user.Role == UserRole.Vendor
            ? RatingSubjectType.Vendor
            : RatingSubjectType.Crew;

        var rows = await _db.Ratings
            .Where(r => r.SubjectUserId == req.UserId && r.SubjectType == subjectType)
            .OrderByDescending(r => r.RatedAt)
            .Select(r => new
            {
                r.Id, r.EventId, r.Performance, r.Cooperation, r.Comment,
                r.RatedAt, r.IsLegacySingleScore,
                EventName = r.Event!.Title,
                EventDate = r.Event!.StartAt,
                RaterName = r.Rater!.FullName
            })
            .ToListAsync(ct);

        // Averaged through ReputationRules so this read agrees exactly with the
        // cached value written by RatingWriter -- two independent implementations
        // of "the average" is how a dashboard ends up contradicting a list.
        var average = ReputationRules.Average(rows.Select(r => (r.Performance, r.Cooperation)));
        var perf    = ReputationRules.Average(rows.Select(r => (r.Performance, r.Performance)));
        var coop    = ReputationRules.Average(rows.Select(r => (r.Cooperation, r.Cooperation)));

        // Every bucket present, including the empty ones, so a caller can render
        // a 5-bar chart without inventing the gaps.
        var distribution = Enumerable.Range(ReputationRules.MinScore, ReputationRules.MaxScore)
            .ToDictionary(
                star => star,
                star => rows.Count(r =>
                    (int)Math.Round(ReputationRules.ScoreOf(r.Performance, r.Cooperation),
                                    MidpointRounding.AwayFromZero) == star));

        var recent = rows
            .Take(Math.Clamp(req.RecentCount, 1, 50))
            .Select(r => new RatingHistoryItemDto(
                r.Id, r.EventId, r.EventName, r.EventDate,
                r.Performance, r.Cooperation,
                ReputationRules.ScoreOf(r.Performance, r.Cooperation),
                r.Comment, r.RaterName, r.RatedAt, r.IsLegacySingleScore))
            .ToList();

        return Result.Success(new UserRatingSummaryDto(
            req.UserId, user.Role.ToString(), average, perf, coop,
            rows.Count, distribution, recent));
    }
}
