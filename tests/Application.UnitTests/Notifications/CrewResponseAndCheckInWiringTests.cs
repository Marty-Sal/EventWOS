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
/// Covers the crew's own response to an invitation.
///
/// The decision pinned here is the channel asymmetry. A DECLINE goes out on every
/// channel the recipient allows, because a slot the vendor had counted as filled is
/// now empty and the alternative is discovering it at the venue. An ACCEPT is InApp
/// only: still an action item, but a vendor staffing fifty crew should not get fifty
/// emails telling them things are going to plan. Losing that distinction is how a
/// notification channel becomes noise people filter out.
/// </summary>
public class CrewResponseAndCheckInWiringTests
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
                .UseInMemoryDatabase($"crew-response-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

    private sealed record Scene(EventAssignment Assignment, User Crew, User Vendor);

    private static Scene SeedInvitedAssignment(AppDbContext db, bool withVendor = true)
    {
        var admin  = Guid.NewGuid();
        var crew   = new User("9600000001", "Anita Rao", UserRole.Crew);
        var vendor = new User("9600000002", "Sameer Khan", UserRole.Vendor);
        crew.Approve(admin);
        vendor.Approve(admin);
        db.Users.AddRange(crew, vendor);

        var ev = new Event("Sunburn Arena", null, "Vagator", null,
            new DateTime(2026, 12, 20, 16, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 20, 23, 0, 0, DateTimeKind.Utc), admin);
        db.Events.Add(ev);

        var assignment = new EventAssignment(ev.Id, crew.Id, withVendor ? vendor.Id : null, admin);
        db.EventAssignments.Add(assignment);
        db.SaveChanges();
        return new Scene(assignment, crew, vendor);
    }

    private static RespondAssignmentHandler NewHandler(
        AppDbContext db, RecordingDispatcher dispatcher, IUnitOfWork uow) =>
        new(db, uow, new NoOpPusher(), dispatcher,
            Options.Create(new AppUrlOptions { BaseUrl = "https://eventwos.app" }));

    [Fact]
    public async Task A_decline_reaches_the_vendor_on_every_channel_with_the_reason_and_a_link()
    {
        using var db = NewContext();
        var scene = SeedInvitedAssignment(db);
        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        var result = await NewHandler(db, dispatcher, uow).Handle(
            new RespondAssignmentCommand(scene.Assignment.Id, scene.Crew.Id, "decline", "Double booked."), default);

        result.IsSuccess.Should().BeTrue();

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("CREW_DECLINED_ASSIGNMENT");
        request.RecipientUserId.Should().Be(scene.Vendor.Id);

        // null Channels means "whatever the recipient allows" -- the urgent case must not
        // be quietly narrowed to InApp.
        request.Channels.Should().BeNull();

        request.Data!["CrewName"].Should().Be("Anita Rao");
        request.Data!["EventName"].Should().Be("Sunburn Arena");
        request.Data!["Reason"].Should().Be("Double booked.");
        request.Data!["Link"].Should().EndWith("/vendor-assignments");

        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task An_accept_is_in_app_only_so_a_busy_vendor_is_not_emailed_per_crew_member()
    {
        using var db = NewContext();
        var scene = SeedInvitedAssignment(db);
        var dispatcher = new RecordingDispatcher();

        await NewHandler(db, dispatcher, new SnapshottingUnitOfWork(dispatcher, db)).Handle(
            new RespondAssignmentCommand(scene.Assignment.Id, scene.Crew.Id, "confirm"), default);

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("CREW_ACCEPTED_ASSIGNMENT");
        request.Channels.Should().BeEquivalentTo(new[] { NotificationChannel.InApp });
    }

    [Fact]
    public async Task A_decline_with_no_reason_says_so_instead_of_leaving_a_hole_in_the_sentence()
    {
        using var db = NewContext();
        var scene = SeedInvitedAssignment(db);
        var dispatcher = new RecordingDispatcher();

        await NewHandler(db, dispatcher, new SnapshottingUnitOfWork(dispatcher, db)).Handle(
            new RespondAssignmentCommand(scene.Assignment.Id, scene.Crew.Id, "decline"), default);

        // The domain allows a null reason here, and the template renders "Reason: {{Reason}}".
        dispatcher.Requests.Single().Data!["Reason"].Should().Be("no reason given");
    }

    [Fact]
    public async Task Directly_assigned_crew_produce_no_vendor_notification()
    {
        using var db = NewContext();
        var scene = SeedInvitedAssignment(db, withVendor: false);
        var dispatcher = new RecordingDispatcher();

        var result = await NewHandler(db, dispatcher, new SnapshottingUnitOfWork(dispatcher, db)).Handle(
            new RespondAssignmentCommand(scene.Assignment.Id, scene.Crew.Id, "decline", "Ill."), default);

        // No vendor in the loop on a direct assignment, so there is nobody to tell --
        // and inventing a recipient would leak one client's staffing to another vendor.
        result.IsSuccess.Should().BeTrue();
        dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Accept_and_decline_use_different_keys_so_a_re_invite_is_not_swallowed()
    {
        using var db = NewContext();
        var scene = SeedInvitedAssignment(db);

        var declineDispatcher = new RecordingDispatcher();
        await NewHandler(db, declineDispatcher, new SnapshottingUnitOfWork(declineDispatcher, db)).Handle(
            new RespondAssignmentCommand(scene.Assignment.Id, scene.Crew.Id, "decline", "Clash."), default);

        // Re-invite in place (the app resurrects the row rather than inserting a new one),
        // then the crew member changes their mind.
        scene.Assignment.ReInvite(scene.Vendor.Id, Guid.NewGuid());
        await db.SaveChangesAsync();

        var acceptDispatcher = new RecordingDispatcher();
        await NewHandler(db, acceptDispatcher, new SnapshottingUnitOfWork(acceptDispatcher, db)).Handle(
            new RespondAssignmentCommand(scene.Assignment.Id, scene.Crew.Id, "confirm"), default);

        // Same assignment id, two responses. Sharing one key would let the platform
        // de-duplicate the acceptance away and leave the vendor believing the slot is
        // still empty.
        declineDispatcher.Requests.Single().BusinessEventKey
            .Should().NotBe(acceptDispatcher.Requests.Single().BusinessEventKey);
        acceptDispatcher.Requests.Single().BusinessEventKey.Should().EndWith(":crew-accepted");
    }
}
