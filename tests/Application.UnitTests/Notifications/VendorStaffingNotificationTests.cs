using EventWOS.Application.Common;
using EventWOS.Application.CrewGroups.Commands;
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
using Microsoft.Extensions.Options;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// The vendor's own staffing paths: inviting one of their crew, inviting a whole
/// group, and forwarding somebody straight to the manager.
///
/// The recurring hazard in this file is the in-place resurrection. Re-inviting a crew
/// member who declined flips the SAME assignment row back to Invited rather than
/// inserting a new one -- so any notification key built from the assignment id alone
/// makes the second invitation look like a duplicate of the first, and the platform
/// drops it. The crew member is then never told they are wanted again, and the vendor
/// has no way to see that.
/// </summary>
public class VendorStaffingNotificationTests
{
    private sealed class RecordingDispatcher : INotificationDispatcher
    {
        public List<NotificationRequest> Requests { get; } = new();
        public void Enqueue(NotificationRequest request) => Requests.Add(request);
        public void Enqueue(IEnumerable<NotificationRequest> requests) => Requests.AddRange(requests);
        public void EnqueueFanOut(NotificationFanOutRequest request) { }
    }

    private sealed class SnapshottingUnitOfWork : IUnitOfWork
    {
        private readonly RecordingDispatcher _dispatcher;
        private readonly AppDbContext _db;
        public SnapshottingUnitOfWork(RecordingDispatcher d, AppDbContext db) { _dispatcher = d; _db = db; }
        public int RequestsStagedAtSaveTime { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            RequestsStagedAtSaveTime = _dispatcher.Requests.Count;
            return _db.SaveChangesAsync(ct);
        }
        public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
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

    private sealed class AnonymousUser : ICurrentUser
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

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"vendor-staffing-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

    private static IOptions<AppUrlOptions> Urls() =>
        Options.Create(new AppUrlOptions { BaseUrl = "https://eventwos.app" });

    private sealed record Scene(Event Event, EventShift Shift, User Vendor, List<User> Crew, Guid ManagerId);

    /// <summary>
    /// An event with one shift, a vendor who has already accepted the manager's
    /// invitation, and crew on that vendor's roster. No VendorShiftAllocation rows on
    /// purpose: with zero allocations the quota gate reports NotEnforcedYet and lets
    /// the assignment through, which is the legacy-event path.
    /// </summary>
    private static Scene SeedScene(AppDbContext db, int crewCount = 2)
    {
        var manager = Guid.NewGuid();

        var vendor = new User("9700000001", "Sameer Khan", UserRole.Vendor);
        vendor.Approve(manager);
        db.Users.Add(vendor);

        var crew = new List<User>();
        for (var i = 0; i < crewCount; i++)
        {
            var c = new User($"97000001{i:D2}", $"Crew Member {(char)('A' + i)}", UserRole.Crew);
            c.Approve(manager);
            c.JoinVendor(vendor.Id);
            crew.Add(c);
        }
        db.Users.AddRange(crew);

        var scope = new EventWOS.Domain.Entities.ScopeOfWork("Stage Rigging", null, manager);
        db.ScopesOfWork.Add(scope);

        var ev = new Event("Sunburn Arena", null, "Vagator Grounds", null,
            new DateTime(2026, 12, 20, 16, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 20, 23, 0, 0, DateTimeKind.Utc), manager);
        db.Events.Add(ev);

        var shift = new EventShift(ev.Id, scope.Id, 10,
            new DateTime(2026, 12, 20, 16, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 20, 23, 0, 0, DateTimeKind.Utc), manager);
        db.EventShifts.Add(shift);

        // The vendor's own invitation, already accepted -- VendorAssignCrew refuses to
        // staff crew until the vendor is past the Invited stage.
        var placeholder = new EventAssignment(ev.Id, null, vendor.Id, manager);
        placeholder.AttachToShift(shift.Id);
        placeholder.VendorAcceptInvite();
        db.EventAssignments.Add(placeholder);

        db.SaveChanges();
        return new Scene(ev, shift, vendor, crew, manager);
    }

    private static VendorAssignCrewHandler AssignCrewHandler(
        AppDbContext db, RecordingDispatcher d, IUnitOfWork uow) =>
        new(db, uow, new NoOpPusher(), d, Urls());

    [Fact]
    public async Task Inviting_one_crew_member_names_the_vendor_and_uses_every_channel()
    {
        using var db = NewContext();
        var scene = SeedScene(db, crewCount: 1);
        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        var result = await AssignCrewHandler(db, dispatcher, uow).Handle(
            new VendorAssignCrewCommand(scene.Event.Id, scene.Crew[0].Id, scene.Vendor.Id, scene.Shift.Id), default);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        var request = dispatcher.Requests.Should().ContainSingle().Subject;

        // CREW_INVITATION, not CREW_ASSIGNMENT: crew who work for several vendors need
        // to know which one is offering.
        request.TemplateCode.Should().Be("CREW_INVITATION");
        request.RecipientUserId.Should().Be(scene.Crew[0].Id);
        request.Data!["VendorName"].Should().Be("Sameer Khan");
        request.Data!["VenueName"].Should().Be("Vagator Grounds");
        request.Channels.Should().BeNull();

        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task Re_inviting_a_crew_member_who_declined_still_reaches_them()
    {
        using var db = NewContext();
        var scene = SeedScene(db, crewCount: 1);

        var first = new RecordingDispatcher();
        await AssignCrewHandler(db, first, new SnapshottingUnitOfWork(first, db)).Handle(
            new VendorAssignCrewCommand(scene.Event.Id, scene.Crew[0].Id, scene.Vendor.Id, scene.Shift.Id), default);

        // Crew says no, vendor asks again -- which resurrects the same row in place.
        var row = await db.EventAssignments.FirstAsync(a => a.CrewId == scene.Crew[0].Id);
        row.CrewDecline("Busy.");
        await db.SaveChangesAsync();

        var second = new RecordingDispatcher();
        var result = await AssignCrewHandler(db, second, new SnapshottingUnitOfWork(second, db)).Handle(
            new VendorAssignCrewCommand(scene.Event.Id, scene.Crew[0].Id, scene.Vendor.Id, scene.Shift.Id), default);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        // Same assignment id both times. If the key did not carry the moment of
        // invitation, this second message would be de-duplicated into silence.
        second.Requests.Should().ContainSingle();
        second.Requests[0].BusinessEventKey.Should().NotBe(first.Requests[0].BusinessEventKey);
    }

    [Fact]
    public async Task Forwarding_crew_to_the_manager_notifies_every_manager_and_not_the_crew()
    {
        using var db = NewContext();
        var scene = SeedScene(db, crewCount: 1);

        var managerOne = new User("9800000001", "Manager One", UserRole.Manager);
        var managerTwo = new User("9800000002", "Manager Two", UserRole.Admin);
        managerOne.Approve(scene.ManagerId);
        managerTwo.Approve(scene.ManagerId);
        db.Users.AddRange(managerOne, managerTwo);

        var assignment = new EventAssignment(scene.Event.Id, scene.Crew[0].Id, scene.Vendor.Id, scene.ManagerId);
        assignment.AttachToShift(scene.Shift.Id);
        db.EventAssignments.Add(assignment);
        await db.SaveChangesAsync();

        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        var result = await new VendorDirectForwardHandler(db, uow, new NoOpPusher(), dispatcher, Urls())
            .Handle(new VendorDirectForwardCommand(assignment.Id, scene.Vendor.Id), default);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        // Both managers, each with their own idempotent key.
        dispatcher.Requests.Should().HaveCount(2);
        dispatcher.Requests.Select(r => r.TemplateCode).Should().AllBe("ASSIGNMENT_PENDING_APPROVAL");
        dispatcher.Requests.Select(r => r.RecipientUserId)
            .Should().BeEquivalentTo(new[] { managerOne.Id, managerTwo.Id });
        dispatcher.Requests.Select(r => r.BusinessEventKey).Should().OnlyHaveUniqueItems();

        // The crew member is told nothing at this stage. Bypassing their acceptance is
        // still only a move into the manager's queue, and the one message they get is
        // the approval itself -- announcing the intermediate step invites "am I on?".
        dispatcher.Requests.Should().NotContain(r => r.RecipientUserId == scene.Crew[0].Id);

        uow.RequestsStagedAtSaveTime.Should().Be(2);
    }

    [Fact]
    public async Task Assigning_a_group_notifies_only_the_members_who_actually_got_a_row()
    {
        using var db = NewContext();
        var scene = SeedScene(db, crewCount: 3);

        var group = new CrewGroup(scene.Vendor.Id, "Rigging Team", null, scene.Vendor.Id);
        db.CrewGroups.Add(group);
        foreach (var c in scene.Crew)
            db.CrewGroupMembers.Add(new CrewGroupMember(group.Id, c.Id, scene.Vendor.Id));

        // One member is already invited to this exact shift, so the group assign should
        // skip them -- and must NOT send them a second invitation for a shift they are
        // already holding an invite for.
        var alreadyOn = new EventAssignment(scene.Event.Id, scene.Crew[0].Id, scene.Vendor.Id, scene.Vendor.Id);
        alreadyOn.AttachToShift(scene.Shift.Id);
        db.EventAssignments.Add(alreadyOn);
        await db.SaveChangesAsync();

        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        var result = await new VendorAssignGroupHandler(db, uow, new NoOpPusher(), dispatcher, Urls())
            .Handle(new VendorAssignGroupCommand(scene.Event.Id, group.Id, scene.Vendor.Id, scene.Shift.Id), default);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.Invited.Should().Be(2);
        result.Value!.SkippedAlreadyOnEvent.Should().Be(1);

        dispatcher.Requests.Should().HaveCount(2);
        dispatcher.Requests.Select(r => r.RecipientUserId)
            .Should().BeEquivalentTo(new[] { scene.Crew[1].Id, scene.Crew[2].Id });

        // Every member gets their own key, so one member's delivery cannot suppress
        // another's.
        dispatcher.Requests.Select(r => r.BusinessEventKey).Should().OnlyHaveUniqueItems();

        // Staged before the batch's single save: if that save throws, these outbox rows
        // roll back with it and nobody is told they are booked for a batch that failed.
        uow.RequestsStagedAtSaveTime.Should().Be(2);
    }
}
