namespace EventOpsOracle.Domain.Rules;

/// <summary>
/// The one definition of "what a rating is worth" and "what an average means".
///
/// Kept as pure functions in the Domain so the write path, the read path, the
/// SQL recompute and the tests all agree. Averaging is easy to get subtly and
/// permanently wrong -- rounding at the wrong step, or averaging pre-rounded
/// per-event scores instead of the underlying axes -- and every copy of the
/// arithmetic is another chance to diverge.
/// </summary>
public static class ReputationRules
{
    public const int MinScore = 1;
    public const int MaxScore = 5;

    /// <summary>Displayed averages are rounded to 2 dp, matching the DB precision.</summary>
    public const int DisplayDecimals = 2;

    public static bool IsValidScore(int score) => score >= MinScore && score <= MaxScore;

    /// <summary>A single rating as one number: the mean of its two axes.</summary>
    public static decimal ScoreOf(int performance, int cooperation)
        => (performance + cooperation) / 2m;

    /// <summary>
    /// Average of many ratings, computed from the RAW axes rather than from
    /// per-rating scores that have already been rounded. Rounding only at the
    /// end keeps the result stable no matter how the ratings are batched --
    /// averaging rounded values would let the same set of ratings produce
    /// different answers depending on grouping.
    ///
    /// Returns null for an empty set, deliberately: a person with no ratings has
    /// no average, and reporting 0.0 would render as zero stars and read as
    /// "rated terribly" rather than "not yet rated".
    /// </summary>
    public static decimal? Average(IEnumerable<(int Performance, int Cooperation)> ratings)
    {
        ArgumentNullException.ThrowIfNull(ratings);

        long   count = 0;
        decimal sum  = 0m;
        foreach (var (performance, cooperation) in ratings)
        {
            sum += performance + cooperation;
            count++;
        }

        if (count == 0) return null;
        return Math.Round(sum / (count * 2m), DisplayDecimals, MidpointRounding.AwayFromZero);
    }
}
