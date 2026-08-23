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
/// Picking a vendor on a shift while creating the event, or while adding a shift
/// afterwards, has to tell that vendor -- the allocation quota alone is invisible to
/// them, since My Events, their approval queue and the notification pipeline all read
/// EventAssignments.
///
/// The judgement call pinned here is the message count. A vendor picked on four shifts
/// of one event gets four placeholder rows, but VENDOR_EVENT_INVITED names only the
/// event, date and venue, so four messages would be four identical sentences. One per
/// vendor per event is the whole story the template can tell.
/// </summary>
public class EventCreationVendorInviteTests
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
                .UseInMemoryDatabase($"event-creation-invite-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

    private static IOptions<AppUrlOptions> Urls() =>
        Options.Create(new AppUrlOptions { BaseUrl = "https://eventwos.app" });

    private static readonly DateTime Start = new(2026, 12, 20, 16, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End   = new(2026, 12, 20, 23, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_vendor_picked_on_several_shifts_is_told_once_not_once_per_shift()
    {
        using var db = NewContext();
        var admin = Guid.NewGuid();

        var vendor = new User("9900000001", "Sameer Khan", UserRole.Vendor);
        vendor.Approve(admin);
        db.Users.Add(vendor);

        var rigging = new EventWOS.Domain.Entities.ScopeOfWork("Stage Rigging", null, admin);
        var bar     = new EventWOS.Domain.Entities.ScopeOfWork("Bar Service", null, admin);
        db.ScopesOfWork.AddRange(rigging, bar);
        await db.SaveChangesAsync();

        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        var result = await new CreateEventHandler(db, uow, new NoOpPusher(), dispatcher, Urls())
            .Handle(new CreateEventCommand(
                "Sunburn Arena", null, "Vagator Grounds", null, Start, End, 0, admin,
                Shifts: new[]
                {
                    new CreateEventShiftDto(rigging.Id, 5, Start, End, vendor.Id),
                    new CreateEventShiftDto(bar.Id,     4, Start, End, vendor.Id)
                }), default);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        // Two placeholder rows -- the vendor genuinely holds both shifts.
        var placeholders = await db.EventAssignments
            .Where(a => a.CrewId == null && a.VendorId == vendor.Id)
            .ToListAsync();
        placeholders.Should().HaveCount(2);

        // One message, because the template has nothing shift-specific to say.
        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("VENDOR_EVENT_INVITED");
        request.RecipientUserId.Should().Be(vendor.Id);
        request.Data!["RecipientName"].Should().Be("Sameer Khan");
        request.Data!["VenueName"].Should().Be("Vagator Grounds");

        // Keyed on the event and the vendor, which is what collapses the per-shift
        // duplication rather than relying on the caller to remember.
        request.BusinessEventKey.Should().Contain("vendor-invited");
        request.BusinessEventKey.Should().Contain(vendor.Id.ToString());

        // Staged before the save: no message about an event that failed to commit.
        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task Two_vendors_on_one_event_each_get_their_own_invitation()
    {
        using var db = NewContext();
        var admin = Guid.NewGuid();

        var one = new User("9900000011", "Vendor One", UserRole.Vendor);
        var two = new User("9900000012", "Vendor Two", UserRole.Vendor);
        one.Approve(admin); two.Approve(admin);
        db.Users.AddRange(one, two);

        var rigging = new EventWOS.Domain.Entities.ScopeOfWork("Stage Rigging", null, admin);
        var bar     = new EventWOS.Domain.Entities.ScopeOfWork("Bar Service", null, admin);
        db.ScopesOfWork.AddRange(rigging, bar);
        await db.SaveChangesAsync();

        var dispatcher = new RecordingDispatcher();

        var result = await new CreateEventHandler(
                db, new SnapshottingUnitOfWork(dispatcher, db), new NoOpPusher(), dispatcher, Urls())
            .Handle(new CreateEventCommand(
                "Sunburn Arena", null, "Vagator Grounds", null, Start, End, 0, admin,
                Shifts: new[]
                {
                    new CreateEventShiftDto(rigging.Id, 5, Start, End, one.Id),
                    new CreateEventShiftDto(bar.Id,     4, Start, End, two.Id)
                }), default);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        dispatcher.Requests.Should().HaveCount(2);
        dispatcher.Requests.Select(r => r.RecipientUserId).Should().BeEquivalentTo(new[] { one.Id, two.Id });
        dispatcher.Requests.Select(r => r.BusinessEventKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task An_unstaffed_event_notifies_nobody()
    {
        using var db = NewContext();
        var admin = Guid.NewGuid();
        var rigging = new EventWOS.Domain.Entities.ScopeOfWork("Stage Rigging", null, admin);
        db.ScopesOfWork.Add(rigging);
        await db.SaveChangesAsync();

        var dispatcher = new RecordingDispatcher();

        var result = await new CreateEventHandler(
                db, new SnapshottingUnitOfWork(dispatcher, db), new NoOpPusher(), dispatcher, Urls())
            .Handle(new CreateEventCommand(
                "Sunburn Arena", null, "Vagator Grounds", null, Start, End, 0, admin,
                Shifts: new[] { new CreateEventShiftDto(rigging.Id, 5, Start, End, null) }), default);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Adding_a_shift_with_a_vendor_invites_them_with_the_same_key_shape()
    {
        using var db = NewContext();
        var admin = Guid.NewGuid();

        var vendor = new User("9900000021", "Sameer Khan", UserRole.Vendor);
        vendor.Approve(admin);
        db.Users.Add(vendor);

        var rigging = new EventWOS.Domain.Entities.ScopeOfWork("Stage Rigging", null, admin);
        db.ScopesOfWork.Add(rigging);

        var ev = new Event("Sunburn Arena", null, "Vagator Grounds", null, Start, End, admin);
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var dispatcher = new RecordingDispatcher();
        var uow = new SnapshottingUnitOfWork(dispatcher, db);

        var result = await new AddEventShiftHandler(db, uow, new NoOpPusher(), dispatcher, Urls())
            .Handle(new AddEventShiftCommand(ev.Id, rigging.Id, 6, Start, End, admin, vendor.Id), default);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("VENDOR_EVENT_INVITED");
        request.RecipientUserId.Should().Be(vendor.Id);

        // Same (event, vendor) key as the create path, so a vendor already invited to
        // this event is not sent a second word-for-word identical message when another
        // shift is added for them.
        request.BusinessEventKey.Should().Be($"event:{ev.Id}:vendor-invited:{vendor.Id}");

        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }
}
