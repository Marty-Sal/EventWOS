using EventOpsOracle.Application.Common;
using EventOpsOracle.Application.Events.Commands;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// Guards the two-stage crew approval flow.
///
/// The subtle rule these tests exist to protect: a vendor approving a crew member
/// does NOT confirm them. VendorApprove() moves the row to PendingManagerApproval,
/// and a manager can still reject it. So "your assignment is confirmed" must be sent
/// at the MANAGER stage only. Sending it at the vendor stage is the kind of change
/// that looks like an improvement in review -- notify earlier, notify more -- and
/// results in crew travelling to events they were later rejected from.
/// </summary>
public class AssignmentReviewNotificationWiringTests
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

    /// <summary>Swallows pushes: the legacy transient path is not what these tests are about.</summary>
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
                .UseInMemoryDatabase($"assignment-review-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

    private sealed record Scene(EventAssignment Assignment, User Crew, User Vendor, User Manager);

    /// <summary>
    /// Builds a crew assignment sitting at VendorApproved -- i.e. the crew member has
    /// accepted and the vendor has not yet reviewed. Plus one active manager, so the
    /// review-queue notification has somewhere to go.
    /// </summary>
    private static Scene SeedVendorApprovedAssignment(AppDbContext db)
    {
        var crew    = new User("9800000001", "Anita Rao",   UserRole.Crew);
        var vendor  = new User("9800000002", "Sameer Khan", UserRole.Vendor);
        var manager = new User("9800000003", "Priya Nair",  UserRole.Manager);
        var admin = Guid.NewGuid();

        // Approve everyone: new User() starts Pending, and the manager lookup filters
        // on Status == Active. Seeding them Pending made the queue notification vanish
        // and the "does not confirm the crew member" test pass for the wrong reason --
        // there was simply nobody to notify at all.
        crew.Approve(admin);
        vendor.Approve(admin);
        manager.Approve(admin);
        db.Users.AddRange(crew, vendor, manager);

        var ev = new Event("Diwali Gala", null, "Grand Hyatt", null,
            new DateTime(2026, 11, 8, 18, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 8, 23, 0, 0, DateTimeKind.Utc), admin);
        db.Events.Add(ev);

        var assignment = new EventAssignment(ev.Id, crew.Id, vendor.Id, admin);
        assignment.CrewAccept();   // Invited -> VendorApproved (vendor present)
        db.EventAssignments.Add(assignment);

        db.SaveChanges();
        return new Scene(assignment, crew, vendor, manager);
    }

    private static IOptions<AppUrlOptions> Urls() =>
        Options.Create(new AppUrlOptions { BaseUrl = "https://eventwos.app" });

    [Fact]
    public async Task Vendor_approval_does_not_tell_the_crew_member_they_are_confirmed()
    {
        using var db = NewContext();
        var scene = SeedVendorApprovedAssignment(db);
        var dispatcher = new RecordingDispatcher();

        var result = await new VendorApproveAssignmentHandler(
                db, new SnapshottingUnitOfWork(dispatcher, db), new NoOpPusher(), dispatcher, Urls())
            .Handle(new VendorApproveAssignmentCommand(scene.Assignment.Id, scene.Vendor.Id), default);

        result.IsSuccess.Should().BeTrue();

        // A manager can still reject after this point, so a "confirmed" message here
        // would be a promise the system has not made yet.
        dispatcher.Requests.Should().NotContain(r => r.RecipientUserId == scene.Crew.Id);
        dispatcher.Requests.Should().NotContain(r => r.TemplateCode == "CREW_ASSIGNMENT_APPROVED");
    }

    [Fact]
    public async Task Vendor_approval_puts_a_durable_item_in_the_manager_review_queue()
    {
        using var db = NewContext();
        var scene = SeedVendorApprovedAssignment(db);
        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        await new VendorApproveAssignmentHandler(db, uow, new NoOpPusher(), dispatcher, Urls())
            .Handle(new VendorApproveAssignmentCommand(scene.Assignment.Id, scene.Vendor.Id), default);

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("ASSIGNMENT_PENDING_APPROVAL");
        request.RecipientUserId.Should().Be(scene.Manager.Id);

        // The manager needs to know WHO is waiting and WHERE to act -- a bare
        // "something needs approval" is what the old role-wide push already was.
        request.Data!["CrewName"].Should().Be("Anita Rao");
        request.Data!["VendorName"].Should().Be("Sameer Khan");
        request.Data!["EventName"].Should().Be("Diwali Gala");
        request.Data!["Link"].Should().EndWith("/manager-approvals");

        // Per-recipient key, so re-running the approval cannot nag the same manager.
        request.BusinessEventKey.Should().Be(
            $"assignment:{scene.Assignment.Id}:pending-manager:{scene.Manager.Id}");

        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task Manager_approval_is_what_actually_confirms_the_crew_member()
    {
        using var db = NewContext();
        var scene = SeedVendorApprovedAssignment(db);

        // Advance to the manager's queue the way the app does.
        var forwardDispatcher = new RecordingDispatcher();
        await new VendorApproveAssignmentHandler(
                db, new SnapshottingUnitOfWork(forwardDispatcher, db), new NoOpPusher(), forwardDispatcher, Urls())
            .Handle(new VendorApproveAssignmentCommand(scene.Assignment.Id, scene.Vendor.Id), default);

        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        var result = await new ManagerApproveAssignmentHandler(db, uow, new NoOpPusher(), dispatcher)
            .Handle(new ManagerApproveAssignmentCommand(scene.Assignment.Id, scene.Manager.Id), default);

        result.IsSuccess.Should().BeTrue();

        var confirmation = dispatcher.Requests.Should().ContainSingle(
            r => r.TemplateCode == "CREW_ASSIGNMENT_APPROVED").Subject;
        confirmation.RecipientUserId.Should().Be(scene.Crew.Id);
        confirmation.Data!["EventName"].Should().Be("Diwali Gala");
        confirmation.Data!["EventDate"].Should().Be("08 Nov 2026");

        // The vendor keeps the transient push only: they already approved this person,
        // so an email per crew member per event is noise for them.
        dispatcher.Requests.Should().NotContain(r => r.RecipientUserId == scene.Vendor.Id);

        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task Vendor_rejection_reaches_the_crew_member_with_the_reason()
    {
        using var db = NewContext();
        var scene = SeedVendorApprovedAssignment(db);
        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        var result = await new VendorRejectAssignmentHandler(db, uow, new NoOpPusher(), dispatcher)
            .Handle(new VendorRejectAssignmentCommand(
                scene.Assignment.Id, scene.Vendor.Id, "Shift already filled."), default);

        result.IsSuccess.Should().BeTrue();

        // RejectedByVendor is terminal -- no manager stage follows -- so unlike
        // approval, this one is final and must be sent now.
        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("CREW_ASSIGNMENT_REJECTED");
        request.RecipientUserId.Should().Be(scene.Crew.Id);
        request.Data!["Reason"].Should().Be("Shift already filled.");
        request.Data!["EventName"].Should().Be("Diwali Gala");
        request.BusinessEventKey.Should().Be($"assignment:{scene.Assignment.Id}:rejected-by-vendor");

        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task Manager_rejection_uses_its_own_key_so_it_cannot_be_swallowed_by_the_vendor_stage()
    {
        using var db = NewContext();
        var scene = SeedVendorApprovedAssignment(db);

        var forwardDispatcher = new RecordingDispatcher();
        await new VendorApproveAssignmentHandler(
                db, new SnapshottingUnitOfWork(forwardDispatcher, db), new NoOpPusher(), forwardDispatcher, Urls())
            .Handle(new VendorApproveAssignmentCommand(scene.Assignment.Id, scene.Vendor.Id), default);

        var dispatcher = new RecordingDispatcher();

        var result = await new ManagerRejectAssignmentHandler(
                db, new SnapshottingUnitOfWork(dispatcher, db), new NoOpPusher(), dispatcher)
            .Handle(new ManagerRejectAssignmentCommand(
                scene.Assignment.Id, scene.Manager.Id, "Certification expired."), default);

        result.IsSuccess.Should().BeTrue();

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("CREW_ASSIGNMENT_REJECTED");
        request.RecipientUserId.Should().Be(scene.Crew.Id);

        // Two rejection stages, two keys. Sharing one would let a late vendor-stage
        // row de-duplicate away the manager's reversal -- the exact message that stops
        // somebody travelling to an event they are no longer on.
        request.BusinessEventKey.Should().Be($"assignment:{scene.Assignment.Id}:manager-rejected");
        request.Data!["EventName"].Should().Be("Diwali Gala");
        request.Data!["Reason"].Should().Be("Certification expired.");
    }
}
