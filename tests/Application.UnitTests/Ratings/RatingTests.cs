using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Ratings;

public sealed class RatingTests
{
    private static readonly Guid Event   = Guid.NewGuid();
    private static readonly Guid Subject = Guid.NewGuid();
    private static readonly Guid Rater   = Guid.NewGuid();

    private static Rating Make(int performance = 4, int cooperation = 5)
        => new(Event, Subject, RatingSubjectType.Crew, Rater, performance, cooperation);

    [Fact]
    public void Score_is_the_mean_of_the_two_axes()
        => Make(4, 5).Score.Should().Be(4.5m);

    [Theory]
    [InlineData(0, 3)]
    [InlineData(6, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 6)]
    public void Out_of_range_axes_are_rejected(int performance, int cooperation)
    {
        var act = () => Make(performance, cooperation);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_user_cannot_rate_themselves()
    {
        // A self-rating would quietly inflate a real average rather than failing.
        var act = () => new Rating(Event, Subject, RatingSubjectType.Crew, Subject, 5, 5);
        act.Should().Throw<ArgumentException>().WithMessage("*cannot rate themselves*");
    }

    [Fact]
    public void Blank_comments_become_null_rather_than_empty_strings()
    {
        new Rating(Event, Subject, RatingSubjectType.Crew, Rater, 4, 4, "   ")
            .Comment.Should().BeNull();
    }

    [Fact]
    public void Comments_are_trimmed()
    {
        new Rating(Event, Subject, RatingSubjectType.Crew, Rater, 4, 4, "  solid work  ")
            .Comment.Should().Be("solid work");
    }

    [Fact]
    public void Revise_replaces_both_axes_and_stamps_when()
    {
        var rating = Make(2, 2);
        var reviser = Guid.NewGuid();

        rating.Revise(5, 4, "misclicked earlier", reviser);

        rating.Performance.Should().Be(5);
        rating.Cooperation.Should().Be(4);
        rating.Comment.Should().Be("misclicked earlier");
        rating.RaterUserId.Should().Be(reviser);
        rating.RevisedAt.Should().NotBeNull();
    }

    [Fact]
    public void Revise_rejects_an_out_of_range_axis_without_touching_the_stored_rating()
    {
        var rating = Make(4, 4);

        var act = () => rating.Revise(9, 4, null, Guid.NewGuid());

        act.Should().Throw<ArgumentOutOfRangeException>();
        rating.Performance.Should().Be(4, "a rejected revision must not partially apply");
        rating.RevisedAt.Should().BeNull();
    }

    [Fact]
    public void Legacy_import_carries_the_single_score_on_both_axes_and_is_flagged()
    {
        // The old vendor_rating column never separated the axes, so a legacy row
        // must not masquerade as a genuine two-axis rating.
        var ratedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var rating  = Rating.FromLegacySingleScore(Event, Subject, Rater, 3, null, ratedAt);

        rating.Performance.Should().Be(3);
        rating.Cooperation.Should().Be(3);
        rating.IsLegacySingleScore.Should().BeTrue();
        rating.RatedAt.Should().Be(ratedAt, "the original rating date is the honest one");
    }

    [Fact]
    public void Revising_a_legacy_row_clears_the_legacy_flag()
    {
        var rating = Rating.FromLegacySingleScore(Event, Subject, Rater, 3, null, DateTime.UtcNow);

        rating.Revise(5, 2, null, Rater);

        rating.IsLegacySingleScore.Should().BeFalse(
            "a revision supplies both axes explicitly, so it is no longer one score stretched across two columns");
    }
}
