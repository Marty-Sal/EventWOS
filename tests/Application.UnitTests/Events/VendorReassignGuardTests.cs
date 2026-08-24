using EventWOS.Application.Events.Commands;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EventWOS.Application.UnitTests.Events;

/// <summary>
/// Pressing "Assign to Event" in Vendor-only mode on a vendor who is ALREADY working
/// the shift.
///
/// Reported: the admin picked Sameer Khan -- already ManagerApproved on Box Office
/// with two crew placed -- and pressed Assign. A second placeholder anchor was
/// inserted, and the vendor's My Events flipped to "AWAITING RESPONSE ... Accept the
/// shift to start adding your crew" for a shift he had already accepted and staffed.
///
/// The vendor branch used to skip the duplicate check on purpose: back when every
/// placeholder WAS a reserved seat, a second anchor meant a second slot and the
/// capacity guard bounded it. Seats now come from the vendor's quota, so an extra
/// anchor adds nothing to the seat math -- and with nothing left to refuse it, the
/// insert went through. One vendor holds ONE invitation per shift.
/// </summary>
public class VendorReassignGuardTests
{
    [Fact]
    public async Task Assigning_a_vendor_who_is_already_on_the_shift_is_refused()
    {
        using var db = NewContext();
        var (ev, shift, vendor) = SeedEventWithVendorAtWork(db);
        var before = db.EventAssignments.Count(a => a.CrewId == null);

        var result = await NewHandler(db).Handle(
            new AssignCrewCommand(ev.Id, null, vendor.Id, ev.CreatedByUserId, shift.Id), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Assignment.VendorAlreadyOnShift");
        result.Error.Message.Should().Contain("Sameer Khan").And.Contain("Vendor Quotas");

        db.EventAssignments.Count(a => a.CrewId == null).Should().Be(before,
            "no second invitation may be inserted");
    }

    [Fact]
    public async Task An_unanswered_invitation_is_not_re_sent()
    {
        using var db = NewContext();
        var (ev, shift, vendor) = SeedEventWithVendorAtWork(db, vendorStatus: AssignmentStatus.Invited);

        var result = await NewHandler(db).Handle(
            new AssignCrewCommand(ev.Id, null, vendor.Id, ev.CreatedByUserId, shift.Id), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("not responded yet");
    }

    [Fact]
    public async Task A_vendor_who_rejected_the_shift_is_re_invited_on_the_same_row()
    {
        using var db = NewContext();
        var (ev, shift, vendor) = SeedEventWithVendorAtWork(
            db, vendorStatus: AssignmentStatus.RejectedByVendor, withCrew: false);

        var anchorId = db.EventAssignments.Single(a => a.CrewId == null).Id;

        var result = await NewHandler(db).Handle(
            new AssignCrewCommand(ev.Id, null, vendor.Id, ev.CreatedByUserId, shift.Id), default);

        result.IsSuccess.Should().BeTrue();
        db.EventAssignments.Count(a => a.CrewId == null).Should().Be(1, "resurrect, do not duplicate");

        var anchor = db.EventAssignments.Single(a => a.CrewId == null);
        anchor.Id.Should().Be(anchorId);
        anchor.Status.Should().Be(AssignmentStatus.Invited);
        anchor.RejectionReason.Should().BeNull("the old rejection audit is cleared");
    }

    [Fact]
    public async Task A_vendor_new_to_the_shift_is_still_invited_normally()
    {
        using var db = NewContext();
        var (ev, shift, _) = SeedEventWithVendorAtWork(db, withAnchor: false, withCrew: false);
        var fresh = db.Users.Single(u => u.Role == UserRole.Vendor);

        var result = await NewHandler(db).Handle(
            new AssignCrewCommand(ev.Id, null, fresh.Id, ev.CreatedByUserId, shift.Id), default);

        result.IsSuccess.Should().BeTrue();
        db.EventAssignments.Count(a => a.CrewId == null && a.Status == AssignmentStatus.Invited)
          .Should().Be(1);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"vendor-reassign-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(), new AnonymousDbUser());

    private static AssignCrewHandler NewHandler(AppDbContext db) =>
        new(db, new PassThroughUnitOfWork(db), new NoOpPusher(), new NoOpDispatcher());

    private static (Event ev, EventShift shift, User vendor) SeedEventWithVendorAtWork(
        AppDbContext db,
        AssignmentStatus vendorStatus = AssignmentStatus.ManagerApproved,
        bool withCrew = true,
        bool withAnchor = true)
    {
        var admin = Guid.NewGuid();
        var start = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        var ev = new Event("The MIX", null, "Jio Garden", null, start, start.AddHours(5), admin, maxCrew: 3);
        db.Events.Add(ev);

        var scope = new EventWOS.Domain.Entities.ScopeOfWork("Box Office", null, admin);
        db.ScopesOfWork.Add(scope);

        var shift = new EventShift(ev.Id, scope.Id, 3, start, start.AddHours(3), admin);
        db.EventShifts.Add(shift);

        var vendor = new User("1122334455", "Sameer Khan", UserRole.Vendor);
        db.Users.Add(vendor);
        db.SaveChanges();

        db.VendorShiftAllocations.Add(new VendorShiftAllocation(shift.Id, vendor.Id, 3, admin));

        if (withAnchor)
        {
            var anchor = new EventAssignment(ev.Id, shift.Id, crewId: null, vendorId: vendor.Id, admin);
            DriveAnchorTo(anchor, vendorStatus);
            db.EventAssignments.Add(anchor);
        }

        if (withCrew)
        {
            for (var i = 0; i < 2; i++)
            {
                var crew = new User($"123123123{i}", $"Crew {i}", UserRole.Crew);
                db.Users.Add(crew);

                var row = new EventAssignment(ev.Id, shift.Id, crew.Id, vendor.Id, vendor.Id);
                row.CrewAccept();
                row.VendorApprove();
                row.ManagerApprove();
                db.EventAssignments.Add(row);
            }
        }

        db.SaveChanges();
        return (ev, shift, vendor);
    }

    /// <summary>Walks a vendor anchor through the real transitions to the wanted state.</summary>
    private static void DriveAnchorTo(EventAssignment anchor, AssignmentStatus target)
    {
        switch (target)
        {
            case AssignmentStatus.Invited:
                break;
            case AssignmentStatus.RejectedByVendor:
                anchor.VendorRejectInvite("Not available");
                break;
            default:
                anchor.VendorAcceptInvite();
                if (anchor.Status != target)
                    typeof(EventAssignment).GetProperty(nameof(EventAssignment.Status))!
                        .SetValue(anchor, target);
                break;
        }
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

    private sealed class NoOpDispatcher : INotificationDispatcher
    {
        public void Enqueue(NotificationRequest request) { }
        public void Enqueue(IEnumerable<NotificationRequest> requests) { }
        public void EnqueueFanOut(NotificationFanOutRequest request) { }
    }

    private sealed class NoOpPusher : INotificationPusher
    {
        public Task PushToUserAsync(Guid userId, string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushToRoleAsync(string role, string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushToAllAsync(string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
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
