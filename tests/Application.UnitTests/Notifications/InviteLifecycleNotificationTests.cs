using EventWOS.Application.Common;
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
/// The invitation lifecycle: withdrawals, re-invitations, and the vendor's own
/// accept/reject of an event.
///
/// Cancellations are the notifications with real-world consequences -- somebody
/// travelling to a venue for a shift that no longer exists, or a manager holding an
/// event they believe is staffed. They all go out on every channel the recipient
/// allows, and the tests below pin that along with the key strategy, which differs
/// per path for a specific reason each time.
/// </summary>
public class InviteLifecycleNotificationTests
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
                .UseInMemoryDatabase($"invite-lifecycle-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

    private static IOptions<AppUrlOptions> Urls() =>
        Options.Create(new AppUrlOptions { BaseUrl = "https://eventwos.app" });

    private sealed record Scene(Event Event, User Vendor, User Crew, Guid ManagerId);

    private static Scene SeedScene(AppDbContext db)
    {
        var manager = Guid.NewGuid();
        var vendor = new User("9500000001", "Sameer Khan", UserRole.Vendor);
        var crew   = new User("9500000002", "Anita Rao", UserRole.Crew);
        vendor.Approve(manager);
        crew.Approve(manager);
        db.Users.AddRange(vendor, crew);

        var ev = new Event("Sunburn Arena", null, "Vagator Grounds", null,
            new DateTime(2026, 12, 20, 16, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 20, 23, 0, 0, DateTimeKind.Utc), manager);
        db.Events.Add(ev);
        db.SaveChanges();
        return new Scene(ev, vendor, crew, manager);
    }

    [Fact]
    public async Task Withdrawing_a_crew_invite_tells_the_crew_member_on_every_channel()
    {
        using var db = NewContext();
        var scene = SeedScene(db);
        var assignment = new EventAssignment(scene.Event.Id, scene.Crew.Id, scene.Vendor.Id, scene.ManagerId);
        db.EventAssignments.Add(assignment);
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        var result = await new VendorRevokeCrewInviteHandler(db, uow, new NoOpPusher(), dispatcher, Urls())
            .Handle(new VendorRevokeCrewInviteCommand(scene.Event.Id, scene.Crew.Id, scene.Vendor.Id), default);

        result.IsSuccess.Should().BeTrue();

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("CREW_INVITE_REVOKED");
        request.RecipientUserId.Should().Be(scene.Crew.Id);

        // The whole point of this one. If a withdrawal only ever exists as a toast the
        // crew member did not see, they turn up to a shift that is not there.
        request.Channels.Should().BeNull();
        request.Data!["RecipientName"].Should().Be("Anita Rao");
        request.Data!["EventName"].Should().Be("Sunburn Arena");

        // Staged before the save, so the withdrawal and the message commit together.
        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task Withdrawing_a_vendor_invite_tells_the_vendor()
    {
        using var db = NewContext();
        var scene = SeedScene(db);
        // Placeholder row: CrewId null is what makes it the vendor's invitation.
        var placeholder = new EventAssignment(scene.Event.Id, null, scene.Vendor.Id, scene.ManagerId);
        db.EventAssignments.Add(placeholder);
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();

        var result = await new RevokeVendorInviteHandler(
                db, new SnapshottingUnitOfWork(dispatcher, db), new NoOpPusher(), dispatcher, Urls())
            .Handle(new RevokeVendorInviteCommand(placeholder.Id, scene.ManagerId), default);

        result.IsSuccess.Should().BeTrue();
        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("VENDOR_INVITE_REVOKED");
        request.RecipientUserId.Should().Be(scene.Vendor.Id);
        request.Channels.Should().BeNull();
        request.Data!["RecipientName"].Should().Be("Sameer Khan");
    }

    [Fact]
    public async Task A_vendor_turning_an_event_down_reaches_the_manager_with_reason_and_link()
    {
        using var db = NewContext();
        var scene = SeedScene(db);
        var placeholder = new EventAssignment(scene.Event.Id, null, scene.Vendor.Id, scene.ManagerId);
        db.EventAssignments.Add(placeholder);
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();

        await new VendorRespondToInviteHandler(
                db, new SnapshottingUnitOfWork(dispatcher, db), new NoOpPusher(), dispatcher, Urls())
            .Handle(new VendorRespondToInviteCommand(placeholder.Id, scene.Vendor.Id, "reject", "No staff free."), default);

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("VENDOR_REJECTED_EVENT");

        // Goes to whoever invited them, not to a role: the manager holding this event.
        request.RecipientUserId.Should().Be(scene.ManagerId);
        request.Channels.Should().BeNull();
        request.Data!["VendorName"].Should().Be("Sameer Khan");
        request.Data!["Reason"].Should().Be("No staff free.");
        request.Data!["Link"].Should().EndWith("/approvals/events");
    }

    [Fact]
    public async Task A_vendor_accepting_an_event_is_in_app_only()
    {
        using var db = NewContext();
        var scene = SeedScene(db);
        var placeholder = new EventAssignment(scene.Event.Id, null, scene.Vendor.Id, scene.ManagerId);
        db.EventAssignments.Add(placeholder);
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();

        await new VendorRespondToInviteHandler(
                db, new SnapshottingUnitOfWork(dispatcher, db), new NoOpPusher(), dispatcher, Urls())
            .Handle(new VendorRespondToInviteCommand(placeholder.Id, scene.Vendor.Id, "accept"), default);

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("VENDOR_ACCEPTED_EVENT");

        // A manager running a dozen vendors should not be emailed twelve times to be
        // told the plan is working.
        request.Channels.Should().BeEquivalentTo(new[] { NotificationChannel.InApp });
    }

    [Fact]
    public async Task Re_inviting_the_same_vendor_twice_produces_two_deliverable_invitations()
    {
        using var db = NewContext();
        var scene = SeedScene(db);
        var placeholder = new EventAssignment(scene.Event.Id, null, scene.Vendor.Id, scene.ManagerId);
        db.EventAssignments.Add(placeholder);
        db.SaveChanges();

        var keys = new List<string>();

        for (var round = 0; round < 2; round++)
        {
            placeholder.VendorRejectInvite("Busy.");
            await db.SaveChangesAsync();

            var dispatcher = new RecordingDispatcher();
            var result = await new ReinviteVendorHandler(
                    db, new SnapshottingUnitOfWork(dispatcher, db), new NoOpPusher(), dispatcher, Urls())
                .Handle(new ReinviteVendorCommand(placeholder.Id, scene.ManagerId), default);

            result.IsSuccess.Should().BeTrue();
            var request = dispatcher.Requests.Should().ContainSingle().Subject;
            request.TemplateCode.Should().Be("VENDOR_EVENT_INVITED");
            request.Data!["VenueName"].Should().Be("Vagator Grounds");
            keys.Add(request.BusinessEventKey);
        }

        // ManagerReinviteVendor resurrects the SAME row, so both invitations share an
        // assignment id. Without the timestamp in the key, the platform would treat the
        // second invitation as a duplicate and the vendor would never be told they are
        // wanted again.
        keys.Should().OnlyHaveUniqueItems();
        keys.Should().OnlyContain(k => k.Contains(":vendor-reinvited:"));
    }
}
