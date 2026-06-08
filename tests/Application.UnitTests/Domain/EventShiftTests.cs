using EventWOS.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Domain;

/// <summary>
/// Pins the invariants on <see cref="EventShift"/>. The shift is the new
/// source of truth for event staffing capacity (Phase B of Scope-of-Work),
/// so the basic rules — non-empty IDs, end-after-start, positive crew count,
/// can't shrink below seats occupied, can't archive while in use — need to
/// stay green or the whole capacity model wobbles.
///
/// Lives alongside ScopeOfWorkTests in the same project — same reason as
/// noted there (no need for a dedicated Domain test project until there
/// are enough rules to warrant the isolation).
/// </summary>
public sealed class EventShiftTests
{
    private static readonly Guid EventId  = Guid.NewGuid();
    private static readonly Guid ScopeId  = Guid.NewGuid();
    private static readonly Guid Creator  = Guid.NewGuid();
    private static readonly DateTime Start = new(2026, 7, 1, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End   = Start.AddHours(5);

    private static EventShift NewShift(int crewCount = 5, DateTime? endAt = null) =>
        new(EventId, ScopeId, crewCount, Start, endAt ?? End, Creator);

    // ── Constructor ──────────────────────────────────────────────────────────
    [Fact]
    public void Constructor_sets_required_fields()
    {
        var s = NewShift();
        s.EventId.Should().Be(EventId);
        s.ScopeOfWorkId.Should().Be(ScopeId);
        s.CrewCount.Should().Be(5);
        s.StartAt.Should().Be(Start);
        s.EndAt.Should().Be(End);
        s.CreatedByUserId.Should().Be(Creator);
        s.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Constructor_accepts_null_end_at()
    {
        // EndAt is intentionally nullable — Phase D crew portal handles the
        // "contact vendor" fallback when no end time is set. Construct
        // directly here so the helper's default fallback doesn't mask the
        // null we're testing.
        var s = new EventShift(EventId, ScopeId, 1, Start, endAt: null, Creator);
        s.EndAt.Should().BeNull();
    }

    [Fact]
    public void Constructor_rejects_empty_event_id()
    {
        Action act = () => new EventShift(Guid.Empty, ScopeId, 1, Start, End, Creator);
        act.Should().Throw<ArgumentException>().WithMessage("*EventId*");
    }

    [Fact]
    public void Constructor_rejects_empty_scope_id()
    {
        Action act = () => new EventShift(EventId, Guid.Empty, 1, Start, End, Creator);
        act.Should().Throw<ArgumentException>().WithMessage("*ScopeOfWorkId*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_rejects_zero_or_negative_crew_count(int bad)
    {
        Action act = () => new EventShift(EventId, ScopeId, bad, Start, End, Creator);
        act.Should().Throw<ArgumentException>().WithMessage("*crew count*at least 1*");
    }

    [Fact]
    public void Constructor_rejects_end_at_equal_to_start()
    {
        // A zero-length window is meaningless — and the DB CHECK constraint
        // says end > start, not end >= start. Keep the domain in sync.
        Action act = () => new EventShift(EventId, ScopeId, 1, Start, Start, Creator);
        act.Should().Throw<ArgumentException>().WithMessage("*end time*after start*");
    }

    [Fact]
    public void Constructor_rejects_end_at_before_start()
    {
        Action act = () => new EventShift(EventId, ScopeId, 1, Start, Start.AddHours(-1), Creator);
        act.Should().Throw<ArgumentException>().WithMessage("*end time*after start*");
    }

    // ── Update ───────────────────────────────────────────────────────────────
    [Fact]
    public void Update_changes_fields_and_bumps_updated_at()
    {
        var s = NewShift(crewCount: 5);
        var newStart = Start.AddDays(1);
        var newEnd   = newStart.AddHours(8);

        s.Update(crewCount: 10, startAt: newStart, endAt: newEnd, currentSeatsOccupied: 0);

        s.CrewCount.Should().Be(10);
        s.StartAt.Should().Be(newStart);
        s.EndAt.Should().Be(newEnd);
        s.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void Update_blocks_shrinking_below_seats_occupied()
    {
        // Mirrors Event.Update's capacity-floor guard — you cannot pull the
        // rug out from under crew who are already approved on the shift.
        var s = NewShift(crewCount: 10);
        Action act = () => s.Update(crewCount: 3, startAt: Start, endAt: End, currentSeatsOccupied: 5);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*reduce shift capacity below 5*");
    }

    [Fact]
    public void Update_allows_shrinking_down_to_seats_occupied_exactly()
    {
        // 5 seats taken → capacity can drop to 5. Strictly less is rejected.
        var s = NewShift(crewCount: 10);
        s.Update(crewCount: 5, startAt: Start, endAt: End, currentSeatsOccupied: 5);
        s.CrewCount.Should().Be(5);
    }

    [Fact]
    public void Update_invariants_re_run_on_edit()
    {
        // Sanity: the validation block in Update reuses ValidateInvariants,
        // so changing start/end into an invalid combo throws too.
        var s = NewShift();
        Action act = () => s.Update(crewCount: 5, startAt: Start, endAt: Start.AddMinutes(-1), currentSeatsOccupied: 0);
        act.Should().Throw<ArgumentException>().WithMessage("*end time*after start*");
    }

    // ── ChangeScope ──────────────────────────────────────────────────────────
    [Fact]
    public void ChangeScope_repoints_and_bumps_updated_at()
    {
        var s = NewShift();
        var newScope = Guid.NewGuid();
        s.ChangeScope(newScope);
        s.ScopeOfWorkId.Should().Be(newScope);
        s.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void ChangeScope_rejects_empty_id()
    {
        var s = NewShift();
        Action act = () => s.ChangeScope(Guid.Empty);
        act.Should().Throw<ArgumentException>().WithMessage("*ScopeOfWorkId*");
    }

    // ── Archive ──────────────────────────────────────────────────────────────
    [Fact]
    public void Archive_soft_deletes_when_no_seats_occupied()
    {
        var s = NewShift();
        var actor = Guid.NewGuid();
        s.Archive(actor, currentSeatsOccupied: 0);
        s.IsDeleted.Should().BeTrue();
        s.DeletedAt.Should().NotBeNull();
        s.DeletedBy.Should().Be(actor);
    }

    [Fact]
    public void Archive_throws_when_assignments_active()
    {
        // The capacity rules say "cannot orphan crew". Handler enforces by
        // querying OccupiesSeatOnShift; the domain re-checks via the count
        // it's given. Tested at both layers — belt and braces.
        var s = NewShift();
        Action act = () => s.Archive(Guid.NewGuid(), currentSeatsOccupied: 3);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*3 crew*already approved*");
    }

    [Fact]
    public void Archive_is_idempotent()
    {
        var s = NewShift();
        var actor = Guid.NewGuid();
        s.Archive(actor, 0);
        var firstDeletedAt = s.DeletedAt;

        // Second archive on an already-archived shift is a no-op. Mirrors
        // the ScopeOfWork.Archive contract — repeated calls don't reset
        // the deletion audit fields.
        s.Archive(Guid.NewGuid(), 0);
        s.IsDeleted.Should().BeTrue();
        s.DeletedAt.Should().Be(firstDeletedAt);
        s.DeletedBy.Should().Be(actor);
    }
}
