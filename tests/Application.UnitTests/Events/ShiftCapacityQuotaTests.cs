using EventWOS.Application.Events.Commands;
using EventWOS.Application.Events.Queries;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Rules;
using EventWOS.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EventWOS.Application.UnitTests.Events;

/// <summary>
/// Editing a shift's capacity, and what that does to the vendors on it.
///
/// The reported bug: a manager raised a shift from 2 to 3, the crew number moved
/// and nothing else did. The vendor who owned that shift kept a quota of 2 and
/// their Assign Crew modal still refused them ("your allocation is full (2/2)"),
/// while the admin's own screen simultaneously claimed the shift was fully
/// staffed AND showed 1 free seat. Two separate faults:
///
///   1. a resize never touched VendorShiftAllocation, though the creation paths
///      grant a vendor picked on a shift the whole shift (Quota == CrewCount);
///   2. capacity math charged a vendor's placeholder anchor a seat on top of the
///      quota that anchor stands for, so the shift looked one seat fuller than it
///      was for every vendor on it.
///
/// The agreed rule for shrinking, which these tests pin: only seats a vendor has
/// NOT filled can be taken away.
/// </summary>
public class ShiftCapacityQuotaTests
{
    // ── the seat math ───────────────────────────────────────────────────────

    [Fact]
    public void A_vendors_anchor_is_not_charged_on_top_of_their_quota()
    {
        // The exact reported shape: capacity 3, one vendor with quota 2 who has
        // placed 2 crew, plus their invite anchor. Committed is 2 -- one seat free.
        var vendor = Guid.NewGuid();

        var committed = AssignmentCapacityRules.CommittedSeatsOnShift(
            allocations: new[] { (vendor, 2) },
            activeRows: new (Guid?, bool)[]
            {
                (vendor, true),   // placeholder anchor
                (vendor, false),  // crew
                (vendor, false),  // crew
            });

        committed.Should().Be(2, "the anchor and the quota describe the same seats");
    }

    [Fact]
    public void Unfilled_quota_still_holds_its_seats()
    {
        var vendor = Guid.NewGuid();

        var committed = AssignmentCapacityRules.CommittedSeatsOnShift(
            allocations: new[] { (vendor, 3) },
            activeRows: new (Guid?, bool)[] { (vendor, true) });

        committed.Should().Be(3, "the vendor is entitled to all three, they just haven't staffed them");
    }

    [Fact]
    public void Directly_assigned_crew_take_a_seat_each()
    {
        var committed = AssignmentCapacityRules.CommittedSeatsOnShift(
            allocations: Array.Empty<(Guid, int)>(),
            activeRows: new (Guid?, bool)[] { (null, false), (null, false) });

        committed.Should().Be(2);
    }

    [Fact]
    public void Placeholders_from_a_vendor_with_no_quota_still_count()
    {
        // Legacy invites made before quotas existed: the anchor is the only record
        // of the seat, so it must keep counting -- this is what stops placeholders
        // being stacked past capacity on an un-quota'd shift.
        var vendor = Guid.NewGuid();

        var committed = AssignmentCapacityRules.CommittedSeatsOnShift(
            allocations: Array.Empty<(Guid, int)>(),
            activeRows: new (Guid?, bool)[] { (vendor, true), (vendor, true), (vendor, true) });

        committed.Should().Be(3);
    }

    [Fact]
    public void Crew_beyond_a_shrunken_quota_are_still_counted()
    {
        var vendor = Guid.NewGuid();

        var committed = AssignmentCapacityRules.CommittedSeatsOnShift(
            allocations: new[] { (vendor, 1) },
            activeRows: new (Guid?, bool)[] { (vendor, false), (vendor, false), (vendor, false) });

        committed.Should().Be(3, "three people are really on the shift, whatever the quota says");
    }

    // ── growing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Raising_capacity_raises_the_quota_of_the_vendor_who_owns_the_shift()
    {
        using var db = NewContext();
        var (ev, shift, scope) = SeedEventWithShifts(db, firstShiftCrew: 2);
        var vendor = SeedVendor(db);
        var alloc = Allocate(db, shift, vendor, quota: 2);
        PlaceCrew(db, ev, shift, vendor, count: 2);

        var result = await NewUpdateHandler(db).Handle(
            new UpdateEventShiftCommand(shift.Id, scope.Id, 3, shift.StartAt, shift.EndAt), default);

        result.IsSuccess.Should().BeTrue();
        alloc.Quota.Should().Be(3, "the vendor owned the whole shift, so the new seat is theirs");
        result.Value.CommittedCrew.Should().Be(3);
    }

    [Fact]
    public async Task The_new_seat_stays_unallocated_when_two_vendors_share_the_shift()
    {
        using var db = NewContext();
        var (ev, shift, scope) = SeedEventWithShifts(db, firstShiftCrew: 4);
        var a = SeedVendor(db, "9000000001");
        var b = SeedVendor(db, "9000000002");
        var allocA = Allocate(db, shift, a, quota: 2);
        var allocB = Allocate(db, shift, b, quota: 2);

        var result = await NewUpdateHandler(db).Handle(
            new UpdateEventShiftCommand(shift.Id, scope.Id, 6, shift.StartAt, shift.EndAt), default);

        result.IsSuccess.Should().BeTrue();
        allocA.Quota.Should().Be(2, "splitting the new seats is the admin's call, not ours");
        allocB.Quota.Should().Be(2);
    }

    [Fact]
    public async Task A_quota_deliberately_set_below_capacity_is_left_alone()
    {
        using var db = NewContext();
        var (ev, shift, scope) = SeedEventWithShifts(db, firstShiftCrew: 5);
        var vendor = SeedVendor(db);
        var alloc = Allocate(db, shift, vendor, quota: 2);

        var result = await NewUpdateHandler(db).Handle(
            new UpdateEventShiftCommand(shift.Id, scope.Id, 6, shift.StartAt, shift.EndAt), default);

        result.IsSuccess.Should().BeTrue();
        alloc.Quota.Should().Be(2, "the admin capped this vendor on purpose");
    }

    // ── shrinking ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Shrinking_takes_seats_the_vendor_has_not_filled()
    {
        using var db = NewContext();
        var (ev, shift, scope) = SeedEventWithShifts(db, firstShiftCrew: 3);
        var vendor = SeedVendor(db);
        var alloc = Allocate(db, shift, vendor, quota: 3);
        PlaceCrew(db, ev, shift, vendor, count: 1);

        var result = await NewUpdateHandler(db).Handle(
            new UpdateEventShiftCommand(shift.Id, scope.Id, 2, shift.StartAt, shift.EndAt), default);

        result.IsSuccess.Should().BeTrue();
        alloc.Quota.Should().Be(2);
    }

    [Fact]
    public async Task Shrinking_is_refused_when_only_filled_seats_are_left_to_take()
    {
        using var db = NewContext();
        var (ev, shift, scope) = SeedEventWithShifts(db, firstShiftCrew: 3);
        var vendor = SeedVendor(db);
        var alloc = Allocate(db, shift, vendor, quota: 3);
        PlaceCrew(db, ev, shift, vendor, count: 2);

        var result = await NewUpdateHandler(db).Handle(
            new UpdateEventShiftCommand(shift.Id, scope.Id, 1, shift.StartAt, shift.EndAt), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Shift.WouldOrphanVendorCrew");
        result.Error.Message.Should().Contain("2");
        alloc.Quota.Should().Be(3, "a refused edit changes nothing");
    }

    [Fact]
    public async Task Shrinking_trims_the_vendor_sitting_on_the_most_empty_seats_first()
    {
        using var db = NewContext();
        var (ev, shift, scope) = SeedEventWithShifts(db, firstShiftCrew: 6);
        var a = SeedVendor(db, "9000000001");
        var b = SeedVendor(db, "9000000002");
        var allocA = Allocate(db, shift, a, quota: 4);
        var allocB = Allocate(db, shift, b, quota: 2);
        PlaceCrew(db, ev, shift, a, count: 1);

        var result = await NewUpdateHandler(db).Handle(
            new UpdateEventShiftCommand(shift.Id, scope.Id, 4, shift.StartAt, shift.EndAt), default);

        result.IsSuccess.Should().BeTrue();
        allocA.Quota.Should().Be(2, "A had three empty seats, B had two");
        allocB.Quota.Should().Be(2);
    }

    [Fact]
    public async Task Shrinking_refuses_rather_than_quietly_dropping_a_vendor()
    {
        using var db = NewContext();
        var (ev, shift, scope) = SeedEventWithShifts(db, firstShiftCrew: 4);
        var a = SeedVendor(db, "9000000001");
        var b = SeedVendor(db, "9000000002");
        Allocate(db, shift, a, quota: 2);
        Allocate(db, shift, b, quota: 2);

        var result = await NewUpdateHandler(db).Handle(
            new UpdateEventShiftCommand(shift.Id, scope.Id, 1, shift.StartAt, shift.EndAt), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Shift.WouldDropVendor");
    }

    // ── deleting ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_shift_whose_vendor_has_staffed_nobody_can_be_deleted()
    {
        using var db = NewContext();
        var (ev, shift, _) = SeedEventWithShifts(db, firstShiftCrew: 3);
        var vendor = SeedVendor(db);
        var alloc = Allocate(db, shift, vendor, quota: 3);
        var anchor = AddAnchor(db, ev, shift, vendor);

        var result = await NewArchiveHandler(db).Handle(
            new ArchiveEventShiftCommand(shift.Id, ev.CreatedByUserId), default);

        result.IsSuccess.Should().BeTrue();
        shift.IsDeleted.Should().BeTrue();
        alloc.IsDeleted.Should().BeTrue("the quota cannot outlive the shift it was for");
        anchor.IsDeleted.Should().BeTrue("otherwise the vendor keeps the event in My Events with nowhere to staff");
    }

    [Fact]
    public async Task A_shift_with_crew_on_it_still_cannot_be_deleted()
    {
        using var db = NewContext();
        var (ev, shift, _) = SeedEventWithShifts(db, firstShiftCrew: 3);
        var vendor = SeedVendor(db);
        var alloc = Allocate(db, shift, vendor, quota: 3);
        PlaceCrew(db, ev, shift, vendor, count: 1);

        var result = await NewArchiveHandler(db).Handle(
            new ArchiveEventShiftCommand(shift.Id, ev.CreatedByUserId), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Shift.HasActiveCrew");
        shift.IsDeleted.Should().BeFalse();
        alloc.IsDeleted.Should().BeFalse();
    }

    // ── what the vendor can still see afterwards ────────────────────────────

    [Fact]
    public async Task Deleting_the_vendors_only_shift_takes_the_event_off_their_dashboard()
    {
        using var db = NewContext();
        var (ev, shift, _) = SeedEventWithShifts(db, firstShiftCrew: 3);
        var vendor = SeedVendor(db);
        Allocate(db, shift, vendor, quota: 3);
        AddAnchor(db, ev, shift, vendor);

        (await VendorEventIds(db, vendor)).Should().Contain(ev.Id, "invited, with a shift to staff");

        var result = await NewArchiveHandler(db).Handle(
            new ArchiveEventShiftCommand(shift.Id, ev.CreatedByUserId), default);
        result.IsSuccess.Should().BeTrue();

        (await VendorEventIds(db, vendor)).Should().NotContain(ev.Id,
            "the shift they were invited to is gone, so the event offers them nothing");
        (await VendorInvitationCount(db, vendor)).Should().Be(0,
            "and it must stop nagging from the dashboard action centre");
    }

    [Fact]
    public async Task The_event_stays_when_the_vendor_still_holds_another_shift_on_it()
    {
        using var db = NewContext();
        var (ev, shift, scope) = SeedEventWithShifts(db, firstShiftCrew: 3);
        var second = new EventShift(ev.Id, scope.Id, 2, shift.StartAt, shift.EndAt, ev.CreatedByUserId);
        db.EventShifts.Add(second);
        db.SaveChanges();

        var vendor = SeedVendor(db);
        Allocate(db, shift, vendor, quota: 3);
        AddAnchor(db, ev, shift, vendor);
        Allocate(db, second, vendor, quota: 2);
        AddAnchor(db, ev, second, vendor);

        var result = await NewArchiveHandler(db).Handle(
            new ArchiveEventShiftCommand(shift.Id, ev.CreatedByUserId), default);
        result.IsSuccess.Should().BeTrue();

        (await VendorEventIds(db, vendor)).Should().Contain(ev.Id, "they still have work on the other shift");
        (await VendorInvitationCount(db, vendor)).Should().Be(1);
    }

    [Fact]
    public async Task Rows_from_before_shifts_existed_stay_visible()
    {
        // Phase A history: assignments with no ShiftId at all. Hiding these would
        // erase a vendor's past events.
        using var db = NewContext();
        var (ev, _, _) = SeedEventWithShifts(db, firstShiftCrew: 2);
        var vendor = SeedVendor(db);

        var legacy = new EventAssignment(ev.Id, crewId: null, vendorId: vendor.Id, ev.CreatedByUserId);
        db.EventAssignments.Add(legacy);
        db.SaveChanges();

        (await VendorEventIds(db, vendor)).Should().Contain(ev.Id);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"shift-quota-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousDbUser());

    private static UpdateEventShiftHandler NewUpdateHandler(AppDbContext db) =>
        new(db, new PassThroughUnitOfWork(db), new SilentDispatcher());

    private static ArchiveEventShiftHandler NewArchiveHandler(AppDbContext db) =>
        new(db, new PassThroughUnitOfWork(db), new SilentDispatcher());

    /// <summary>
    /// These tests are about seat math, not messages -- EventAndShiftChangeWiringTests
    /// covers what the shift edit and archive paths now tell people.
    /// </summary>
    private sealed class SilentDispatcher : EventWOS.Application.Notifications.Abstractions.INotificationDispatcher
    {
        public void Enqueue(EventWOS.Application.Notifications.Contracts.NotificationRequest request) { }
        public void Enqueue(IEnumerable<EventWOS.Application.Notifications.Contracts.NotificationRequest> requests) { }
        public void EnqueueFanOut(EventWOS.Application.Notifications.Contracts.NotificationFanOutRequest request) { }
    }

    /// <summary>
    /// An event with TWO shifts — the second one exists only so the archive tests
    /// aren't stopped by the "an event must keep one shift" guard.
    /// </summary>
    private static (Event ev, EventShift shift, EventWOS.Domain.Entities.ScopeOfWork scope) SeedEventWithShifts(
        AppDbContext db, int firstShiftCrew)
    {
        var admin = Guid.NewGuid();
        var start = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        var ev = new Event("The MIX", null, "Mumbai", null, start, start.AddHours(10), admin,
            maxCrew: firstShiftCrew + 1);
        db.Events.Add(ev);

        var scope = new EventWOS.Domain.Entities.ScopeOfWork("Box Office", null, admin);
        db.ScopesOfWork.Add(scope);

        var shift = new EventShift(ev.Id, scope.Id, firstShiftCrew, start, start.AddHours(3), admin);
        var spare = new EventShift(ev.Id, scope.Id, 1, start, start.AddHours(3), admin);
        db.EventShifts.AddRange(shift, spare);

        db.SaveChanges();
        return (ev, shift, scope);
    }

    private static User SeedVendor(AppDbContext db, string mobile = "9000000000")
    {
        var vendor = new User(mobile, "Sameer Khan", UserRole.Vendor);
        db.Users.Add(vendor);
        db.SaveChanges();
        return vendor;
    }

    private static VendorShiftAllocation Allocate(
        AppDbContext db, EventShift shift, User vendor, int quota)
    {
        var alloc = new VendorShiftAllocation(shift.Id, vendor.Id, quota, shift.CreatedByUserId);
        db.VendorShiftAllocations.Add(alloc);
        db.SaveChanges();
        return alloc;
    }

    /// <summary>The vendor-only invite anchor: a row with no crew on it.</summary>
    private static EventAssignment AddAnchor(
        AppDbContext db, Event ev, EventShift shift, User vendor)
    {
        var anchor = new EventAssignment(ev.Id, shift.Id, crewId: null, vendorId: vendor.Id,
            ev.CreatedByUserId);
        db.EventAssignments.Add(anchor);
        db.SaveChanges();
        return anchor;
    }

    /// <summary>Crew the vendor has actually placed, approved so they occupy seats.</summary>
    private static void PlaceCrew(
        AppDbContext db, Event ev, EventShift shift, User vendor, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var crew = new User($"88800000{i:D2}", $"Crew {i}", UserRole.Crew);
            db.Users.Add(crew);

            // The real path to a seat: crew accepts, vendor forwards, manager approves.
            var row = new EventAssignment(ev.Id, shift.Id, crew.Id, vendor.Id, vendor.Id);
            row.CrewAccept();
            row.VendorApprove();
            row.ManagerApprove();
            db.EventAssignments.Add(row);
        }
        db.SaveChanges();
    }

    private static async Task<List<Guid>> VendorEventIds(AppDbContext db, User vendor)
    {
        var result = await new GetMyEventsHandler(db).Handle(
            new GetMyEventsQuery(vendor.Id, UserRole.Vendor, 1, 50), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value.Items.Select(i => i.Id).ToList();
    }

    private static async Task<int> VendorInvitationCount(AppDbContext db, User vendor)
    {
        var result = await new GetVendorAssignmentsHandler(db).Handle(
            new GetVendorAssignmentsQuery(vendor.Id, VendorAssignmentMode.Invitations, 1, 50), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value.TotalCount;
    }

    private sealed class PassThroughUnitOfWork : IUnitOfWork
    {
        private readonly AppDbContext _db;
        public PassThroughUnitOfWork(AppDbContext db) => _db = db;
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
        public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class NoOpMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AnonymousDbUser : ICurrentUser
    {
        public Guid? UserId => null;
        public string? Mobile => null;
        public UserRole? Role => null;
        public IReadOnlyList<string> Permissions => Array.Empty<string>();
        public Guid? SessionId => null;
        public string? DeviceId => null;
        public string? IpAddress => null;
        public bool IsAuthenticated => false;
        public bool IsInRole(UserRole role) => false;
        public bool HasPermission(string permission) => false;
    }
}
