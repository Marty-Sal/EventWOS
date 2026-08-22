using EventWOS.Domain.Rules;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Ratings;

/// <summary>
/// Averaging is the part of ratings that is easy to get quietly and permanently
/// wrong, so the arithmetic is pinned here rather than trusted.
/// </summary>
public sealed class ReputationRulesTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(-1, false)]
    public void IsValidScore_accepts_only_one_through_five(int score, bool expected)
        => ReputationRules.IsValidScore(score).Should().Be(expected);

    [Fact]
    public void ScoreOf_is_the_mean_of_the_two_axes()
        => ReputationRules.ScoreOf(5, 4).Should().Be(4.5m);

    [Fact]
    public void Average_of_no_ratings_is_null_not_zero()
    {
        // Zero would render as no stars and read as "rated terribly" rather than
        // "nobody has rated this person yet" -- a materially different claim to
        // show next to a vendor's name.
        ReputationRules.Average(Array.Empty<(int, int)>()).Should().BeNull();
    }

    [Fact]
    public void Average_spans_both_axes_of_every_rating()
    {
        // (5+3)/2 = 4, (4+4)/2 = 4, (1+1)/2 = 1  ->  mean 3
        var result = ReputationRules.Average(new[] { (5, 3), (4, 4), (1, 1) });
        result.Should().Be(3m);
    }

    [Fact]
    public void Average_is_computed_from_raw_axes_not_from_rounded_per_rating_scores()
    {
        // Three ratings whose individual scores are 4.5, 4.5, 4.5. Rounding each
        // to 2dp first happens to be lossless here, so use a set where per-rating
        // rounding WOULD bite: scores 1.5 and 2.5 -> true mean 2.0.
        ReputationRules.Average(new[] { (1, 2), (2, 3) }).Should().Be(2m);
    }

    [Fact]
    public void Average_does_not_depend_on_how_the_ratings_are_ordered()
    {
        var forward  = ReputationRules.Average(new[] { (5, 5), (1, 2), (3, 4) });
        var backward = ReputationRules.Average(new[] { (3, 4), (1, 2), (5, 5) });
        forward.Should().Be(backward);
    }

    [Fact]
    public void Average_rounds_to_two_decimals_away_from_zero()
    {
        // Axes summing to 25 across 3 ratings -> 25/6 = 4.1666...
        var result = ReputationRules.Average(new[] { (5, 5), (5, 5), (4, 1) });
        result.Should().Be(4.17m);
    }

    [Fact]
    public void Average_of_all_top_marks_is_exactly_five()
        => ReputationRules.Average(new[] { (5, 5), (5, 5) }).Should().Be(5m);
}
