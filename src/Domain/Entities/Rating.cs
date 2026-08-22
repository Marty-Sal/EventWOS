using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// One person's performance on one event, scored on two axes.
///
/// Two flows write here:
///   * Admin/Manager rates a VENDOR when the event is marked Completed.
///   * Vendor rates a CREW member when that crew member checks out.
///
/// This table is the SINGLE SOURCE OF TRUTH for reputation. The averages
/// cached on User (User.Rating for vendors, User.CrewRating/CrewRatingCount
/// for crew) are derived from these rows and must be recomputed from them by
/// full aggregation -- never nudged incrementally.
///
/// That distinction is the whole reason this entity exists. The previous model
/// could not survive ordinary corrections:
///   * User.SetRating OVERWROTE a vendor's score, so rating a vendor for their
///     second event destroyed the first. No history, no count, no average.
///   * User.AddCrewRating folded each new star into a running mean. Once folded
///     in, a rating could not be corrected, withdrawn, or recomputed, because
///     the individual scores were gone. (The same incremental-cache pattern is
///     what produced the max_crew drift bug already fixed in this codebase.)
///   * Crew scores hung off EventAssignment, which is per-SHIFT. A crew member
///     working three shifts at one event was rated three times and skewed their
///     own average -- an event should count once.
///
/// Keeping the individual scores means an average is always recoverable, and a
/// wrong rating can be revised or soft-deleted without corrupting anyone.
///
/// Uniqueness: ONE rating per (EventId, SubjectUserId, SubjectType) among live
/// rows -- per capacity, since averages are computed per capacity and that is the
/// grain where a double vote would actually inflate a score. Rating
/// the same person twice for the same event is a revision (see
/// <see cref="Revise"/>), not a second vote.
/// </summary>
public sealed class Rating : BaseEntity
{
    private Rating() { }

    public Rating(
        Guid              eventId,
        Guid              subjectUserId,
        RatingSubjectType subjectType,
        Guid              raterUserId,
        int               performance,
        int               cooperation,
        string?           comment      = null,
        Guid?             assignmentId = null)
    {
        if (eventId       == Guid.Empty) throw new ArgumentException("EventId is required.",       nameof(eventId));
        if (subjectUserId == Guid.Empty) throw new ArgumentException("SubjectUserId is required.", nameof(subjectUserId));
        if (raterUserId   == Guid.Empty) throw new ArgumentException("RaterUserId is required.",   nameof(raterUserId));

        // Nobody rates themselves. Cheap to check here, and a self-rating would
        // quietly inflate a real average rather than failing loudly.
        if (subjectUserId == raterUserId)
            throw new ArgumentException("A user cannot rate themselves.", nameof(raterUserId));

        GuardScore(performance, nameof(performance));
        GuardScore(cooperation, nameof(cooperation));

        EventId       = eventId;
        SubjectUserId = subjectUserId;
        SubjectType   = subjectType;
        RaterUserId   = raterUserId;
        Performance   = performance;
        Cooperation   = cooperation;
        Comment       = Normalise(comment);
        AssignmentId  = assignmentId;
        RatedAt       = DateTime.UtcNow;
    }

    public Guid              EventId       { get; private set; }
    public Guid              SubjectUserId { get; private set; }
    public RatingSubjectType SubjectType   { get; private set; }
    public Guid              RaterUserId   { get; private set; }

    /// <summary>Quality of the work delivered, 1-5.</summary>
    public int Performance { get; private set; }

    /// <summary>Conduct, communication, willingness to work with others, 1-5.</summary>
    public int Cooperation { get; private set; }

    /// <summary>Optional free-text note from the rater. Trimmed; blank becomes null.</summary>
    public string? Comment { get; private set; }

    /// <summary>
    /// The EventAssignment this rating came from, for crew ratings. Provenance
    /// only -- a crew member with several shifts at one event still gets ONE
    /// rating, so this records which checkout prompted it and is never used for
    /// uniqueness. Null for vendor ratings, which are event-wide.
    /// </summary>
    public Guid? AssignmentId { get; private set; }

    public DateTime  RatedAt   { get; private set; }
    public DateTime? RevisedAt { get; private set; }

    /// <summary>
    /// True for rows imported from the old single-star
    /// EventAssignment.VendorRating. Those ratings never distinguished
    /// performance from cooperation, so both axes hold the same imported number.
    /// Flagged rather than silently presented as a genuine two-axis rating,
    /// because reporting on "cooperation" across legacy rows would otherwise be
    /// reading a value nobody ever supplied.
    /// </summary>
    public bool IsLegacySingleScore { get; private set; }

    // Navigation
    public Event? Event   { get; private set; }
    public User?  Subject { get; private set; }
    public User?  Rater   { get; private set; }

    /// <summary>
    /// The rating as a single number: the mean of the two axes. Not persisted --
    /// deriving it on read keeps it honest if either axis is ever revised.
    /// </summary>
    public decimal Score => Math.Round((Performance + Cooperation) / 2m, 2);

    /// <summary>
    /// Correct an existing rating in place. Preferred over deleting and
    /// re-creating, which would trip the one-per-event unique index and lose
    /// the original RatedAt.
    /// </summary>
    public void Revise(int performance, int cooperation, string? comment, Guid revisedByUserId)
    {
        GuardScore(performance, nameof(performance));
        GuardScore(cooperation, nameof(cooperation));
        if (revisedByUserId == Guid.Empty)
            throw new ArgumentException("RevisedBy is required.", nameof(revisedByUserId));

        Performance = performance;
        Cooperation = cooperation;
        Comment     = Normalise(comment);
        RaterUserId = revisedByUserId;
        RevisedAt   = DateTime.UtcNow;

        // A revision supplies both axes explicitly, so the row is no longer a
        // single score stretched across two columns.
        IsLegacySingleScore = false;
    }

    /// <summary>
    /// Rehydrates a pre-existing single-star rating into this table. Used only
    /// by the one-shot backfill of EventAssignment.VendorRating; ordinary code
    /// goes through the constructor.
    /// </summary>
    public static Rating FromLegacySingleScore(
        Guid     eventId,
        Guid     subjectUserId,
        Guid     raterUserId,
        int      score,
        Guid?    assignmentId,
        DateTime ratedAt)
    {
        var r = new Rating(eventId, subjectUserId, RatingSubjectType.Crew,
                           raterUserId, score, score, null, assignmentId)
        {
            IsLegacySingleScore = true,
            RatedAt             = ratedAt
        };
        return r;
    }

    private static void GuardScore(int value, string name)
    {
        if (value < 1 || value > 5)
            throw new ArgumentOutOfRangeException(name, value, "Rating must be between 1 and 5.");
    }

    private static string? Normalise(string? comment)
        => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
}
