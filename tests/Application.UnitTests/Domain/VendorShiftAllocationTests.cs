using EventWOS.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Domain;

/// <summary>
/// Pins the invariants on <see cref="VendorShiftAllocation"/>. Phase C of
/// the Scope-of-Work feature. The allocation row is the gating mechanism
/// for vendor-side staffing — if the basic rules slip (quota &lt; 1,
/// shrink-below-occupied, archive-while-active, idempotent archive) the
/// whole quota model wobbles.
///
/// House style note: handler-level tests (over-commit shift,
/// duplicate-vendor-on-shift) are not added in this commit — the project
/// doesn't yet have an in-memory EF test harness, and standing one up
/// just for these handlers wasn't justified for step 2. The handlers
/// surface those failures via narrow error codes (VendorAllocation.OverCommitsShift,
/// VendorAllocation.Duplicate, VendorAllocation.WouldOrphanCrew) which
/// are easy to validate manually and will be covered by integration
/// tests in a later phase.
/// </summary>
public sealed class VendorShiftAllocationTests
{
    private static readonly Guid ShiftId  = Guid.NewGuid();
    private static readonly Guid VendorId = Guid.NewGuid();
    private static readonly Guid Creator  = Guid.NewGuid();

    private static VendorShiftAllocation NewAlloc(int quota = 5) =>
        new(ShiftId, VendorId, quota, Creator);

    // ── Constructor ──────────────────────────────────────────────────────────
    [Fact]
    public void Constructor_sets_required_fields()
    {
        var a = NewAlloc(7);
        a.ShiftId.Should().Be(ShiftId);
        a.VendorId.Should().Be(VendorId);
        a.Quota.Should().Be(7);
        a.CreatedByUserId.Should().Be(Creator);
        a.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Constructor_rejects_empty_shift_id()
    {
        Action act = () => new VendorShiftAllocation(Guid.Empty, VendorId, 1, Creator);
        act.Should().Throw<ArgumentException>().WithMessage("*ShiftId*");
    }

    [Fact]
    public void Constructor_rejects_empty_vendor_id()
    {
        Action act = () => new VendorShiftAllocation(ShiftId, Guid.Empty, 1, Creator);
        act.Should().Throw<ArgumentException>().WithMessage("*VendorId*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_rejects_zero_or_negative_quota(int bad)
    {
        Action act = () => new VendorShiftAllocation(ShiftId, VendorId, bad, Creator);
        act.Should().Throw<ArgumentException>().WithMessage("*Quota*at least 1*");
    }

    // ── UpdateQuota ──────────────────────────────────────────────────────────
    [Fact]
    public void UpdateQuota_changes_quota_and_bumps_updated_at()
    {
        var a = NewAlloc(5);
        a.UpdateQuota(newQuota: 10, currentSeatsOccupied: 0);
        a.Quota.Should().Be(10);
        a.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void UpdateQuota_blocks_shrinking_below_seats_occupied()
    {
        // Mirrors EventShift.Update's floor guard — you cannot pull the
        // rug out from under crew already approved under this vendor on
        // this shift.
        var a = NewAlloc(10);
        Action act = () => a.UpdateQuota(newQuota: 3, currentSeatsOccupied: 5);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*reduce quota below 5*");
    }

    [Fact]
    public void UpdateQuota_allows_shrinking_to_exactly_seats_occupied()
    {
        // 5 seats taken → quota can drop to 5. Strictly less is rejected.
        var a = NewAlloc(10);
        a.UpdateQuota(newQuota: 5, currentSeatsOccupied: 5);
        a.Quota.Should().Be(5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void UpdateQuota_rejects_zero_or_negative(int bad)
    {
        var a = NewAlloc(5);
        Action act = () => a.UpdateQuota(newQuota: bad, currentSeatsOccupied: 0);
        act.Should().Throw<ArgumentException>().WithMessage("*Quota*at least 1*");
    }

    // ── Archive ──────────────────────────────────────────────────────────────
    [Fact]
    public void Archive_soft_deletes_when_no_seats_occupied()
    {
        var a = NewAlloc();
        var actor = Guid.NewGuid();
        a.Archive(actor, currentSeatsOccupied: 0);
        a.IsDeleted.Should().BeTrue();
        a.DeletedAt.Should().NotBeNull();
        a.DeletedBy.Should().Be(actor);
    }

    [Fact]
    public void Archive_throws_when_crew_currently_occupy_seats()
    {
        // Belt-and-braces: the handler checks via OccupiesSeatOnShift,
        // the domain double-checks via the count it's given. Either
        // catches the orphan-crew bug.
        var a = NewAlloc();
        Action act = () => a.Archive(Guid.NewGuid(), currentSeatsOccupied: 3);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*3 crew*already approved*");
    }

    [Fact]
    public void Archive_is_idempotent()
    {
        var a = NewAlloc();
        var actor = Guid.NewGuid();
        a.Archive(actor, 0);
        var firstDeletedAt = a.DeletedAt;

        // Second archive on already-archived allocation is a no-op —
        // matches the ScopeOfWork.Archive / EventShift.Archive contract.
        a.Archive(Guid.NewGuid(), 0);
        a.IsDeleted.Should().BeTrue();
        a.DeletedAt.Should().Be(firstDeletedAt);
        a.DeletedBy.Should().Be(actor);
    }
}
