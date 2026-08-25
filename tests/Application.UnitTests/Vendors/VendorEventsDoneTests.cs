using EventOpsOracle.Application.Events.Common;
using EventOpsOracle.Domain.Enums;
using FluentAssertions;
using Xunit;

using Row = EventOpsOracle.Application.Events.Common.VendorEventParticipationRules.ParticipationRow;

namespace EventOpsOracle.Application.UnitTests.Vendors;

/// <summary>
/// Pins the vendor "Total Events Done" tile. The original bug was that the
/// number came from a stored counter nothing ever incremented, so a vendor
/// with a completed event still saw 0.
/// </summary>
public class VendorEventsDoneTests
{
    private static readonly Guid Vendor  = Guid.NewGuid();
    private static readonly Guid Vendor2 = Guid.NewGuid();
    private static readonly Guid EventA  = Guid.NewGuid();
    private static readonly Guid EventB  = Guid.NewGuid();

    [Fact]
    public void Completed_event_counts_as_done()
    {
        var rows = new[] { new Row(Vendor, EventA, AssignmentStatus.Attended, EventStatus.Completed) };

        VendorEventParticipationRules.CountEventsDone(rows).Should().Be(1);
    }

    [Fact]
    public void Multiple_crew_on_one_event_counts_once()
    {
        // A vendor normally has several rows per event: the seat-quota
        // placeholder plus one per crew member placed.
        var rows = new[]
        {
            new Row(Vendor, EventA, AssignmentStatus.Invited,  EventStatus.Completed),
            new Row(Vendor, EventA, AssignmentStatus.Attended, EventStatus.Completed),
            new Row(Vendor, EventA, AssignmentStatus.Attended, EventStatus.Completed)
        };

        VendorEventParticipationRules.CountEventsDone(rows).Should().Be(1);
    }

    [Fact]
    public void Completed_event_counts_even_when_nobody_attended()
    {
        // The tile counts events delivered, not heads checked in. Per-head
        // attendance is crew-side (User.EventsAttended).
        var rows = new[] { new Row(Vendor, EventA, AssignmentStatus.NoShow, EventStatus.Completed) };

        VendorEventParticipationRules.CountEventsDone(rows).Should().Be(1);
    }

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Published)]
    [InlineData(EventStatus.InProgress)]
    [InlineData(EventStatus.Cancelled)]
    public void Only_completed_events_count(EventStatus status)
    {
        var rows = new[] { new Row(Vendor, EventA, AssignmentStatus.Confirmed, status) };

        VendorEventParticipationRules.CountEventsDone(rows).Should().Be(0);
    }

    [Theory]
    [InlineData(AssignmentStatus.Declined)]
    [InlineData(AssignmentStatus.RejectedByVendor)]
    [InlineData(AssignmentStatus.RejectedByManager)]
    public void Rejected_or_declined_vendors_get_no_credit(AssignmentStatus status)
    {
        var rows = new[] { new Row(Vendor, EventA, status, EventStatus.Completed) };

        VendorEventParticipationRules.CountEventsDone(rows).Should().Be(0);
        VendorEventParticipationRules.IsActiveParticipation(status).Should().BeFalse();
    }

    [Fact]
    public void Rejection_on_one_event_does_not_hide_another_completed_event()
    {
        var rows = new[]
        {
            new Row(Vendor, EventA, AssignmentStatus.RejectedByManager, EventStatus.Completed),
            new Row(Vendor, EventB, AssignmentStatus.Attended,          EventStatus.Completed)
        };

        VendorEventParticipationRules.CountEventsDone(rows).Should().Be(1);
    }

    [Fact]
    public void Per_vendor_batch_keeps_vendors_separate()
    {
        var rows = new[]
        {
            new Row(Vendor,  EventA, AssignmentStatus.Attended, EventStatus.Completed),
            new Row(Vendor,  EventB, AssignmentStatus.Attended, EventStatus.Completed),
            new Row(Vendor2, EventA, AssignmentStatus.Attended, EventStatus.Completed),
            new Row(Vendor2, EventB, AssignmentStatus.Declined, EventStatus.Completed)
        };

        var counts = VendorEventParticipationRules.CountEventsDonePerVendor(rows);

        counts[Vendor].Should().Be(2);
        counts[Vendor2].Should().Be(1);
    }

    [Fact]
    public void Vendor_with_no_rows_is_absent_from_batch_and_defaults_to_zero()
    {
        var counts = VendorEventParticipationRules.CountEventsDonePerVendor(Array.Empty<Row>());

        counts.GetValueOrDefault(Vendor, 0).Should().Be(0);
    }

    [Fact]
    public void Active_statuses_all_keep_participation()
    {
        // Guard: if a new AssignmentStatus is added it defaults to "active",
        // which is the safe direction -- it can only ever be excluded by being
        // added to InactiveStatuses explicitly.
        foreach (var status in Enum.GetValues<AssignmentStatus>())
        {
            var expected = !VendorEventParticipationRules.InactiveStatuses.Contains(status);
            VendorEventParticipationRules.IsActiveParticipation(status).Should().Be(expected);
        }
    }
}
