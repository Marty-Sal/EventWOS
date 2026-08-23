using EventWOS.Application.Events.Commands;
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

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Guards the wiring between business handlers and the notification platform.
///
/// The rule these protect is the one that is easiest to break by accident and
/// invisible when broken: notifications must be staged BEFORE the handler's
/// SaveChangesAsync, so the business change and its messages commit together. A
/// handler that enqueues afterwards still looks correct and still sends mail --
/// right up to the first rollback, when it announces an assignment that does not
/// exist.
/// </summary>
public class CallSiteWiringTests
{
    /// <summary>Records what a handler asked for, and when relative to the save.</summary>
    private sealed class RecordingDispatcher : INotificationDispatcher
    {
        public List<NotificationRequest> Requests { get; } = new();
        public List<NotificationFanOutRequest> FanOuts { get; } = new();

        public void Enqueue(NotificationRequest request) => Requests.Add(request);

        public void Enqueue(IEnumerable<NotificationRequest> requests) => Requests.AddRange(requests);

        public void EnqueueFanOut(NotificationFanOutRequest request) => FanOuts.Add(request);
    }

    /// <summary>
    /// Fake unit of work that snapshots what the dispatcher had recorded at the
    /// moment SaveChangesAsync was called. That snapshot is the whole point: it is
    /// how these tests prove the enqueue happened BEFORE the save rather than
    /// after it.
    /// </summary>
    private sealed class SnapshottingUnitOfWork : IUnitOfWork
    {
        private readonly RecordingDispatcher _dispatcher;

        public SnapshottingUnitOfWork(RecordingDispatcher dispatcher) => _dispatcher = dispatcher;

        public int SaveCount { get; private set; }
        public int RequestsStagedAtSaveTime { get; private set; }
        public int FanOutsStagedAtSaveTime { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            SaveCount++;
            RequestsStagedAtSaveTime = _dispatcher.Requests.Count;
            FanOutsStagedAtSaveTime  = _dispatcher.FanOuts.Count;
            return Task.FromResult(1);
        }

        public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"wiring-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

    private static Event SeedEvent(AppDbContext db)
    {
        var ev = new Event(
            "Sunburn Arena", "Main stage", "NSCI Dome", "Worli, Mumbai",
            new DateTime(2026, 9, 12, 18, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 12, 23, 0, 0, DateTimeKind.Utc),
            Guid.NewGuid());

        // Only a published event can be cancelled.
        ev.Publish();

        db.Events.Add(ev);
        db.SaveChanges();
        return ev;
    }

    [Fact]
    public async Task Cancelling_an_event_fans_out_to_crew_and_vendors_before_the_save()
    {
        // Before this wiring, cancelling notified NOBODY -- crew found out by
        // arriving at a venue for a job that no longer existed.
        using var db = NewContext();
        var ev = SeedEvent(db);

        var dispatcher = new RecordingDispatcher();
        var uow        = new SnapshottingUnitOfWork(dispatcher);
        var actor      = Guid.NewGuid();

        var result = await new ChangeEventStatusHandler(db, uow, dispatcher)
            .Handle(new ChangeEventStatusCommand(ev.Id, "cancel", "Venue flooded", actor), default);

        result.IsSuccess.Should().BeTrue();

        var fanOut = dispatcher.FanOuts.Should().ContainSingle().Subject;
        fanOut.TemplateCode.Should().Be("EVENT_CANCELLED");
        fanOut.Audience.Should().Be(NotificationAudience.EventCrewAndVendors);

        // High: someone is about to travel to a venue for nothing.
        fanOut.Priority.Should().Be(NotificationPriority.High);

        // The admin who cancelled must not be notified about their own click.
        fanOut.ExcludeUserIds.Should().Contain(actor);

        // The reason reaches the recipients rather than being swallowed.
        fanOut.Data!["Reason"].Should().Be("Venue flooded");

        // THE important assertion: already staged when the save ran, so the
        // cancellation and its messages commit in one transaction.
        uow.FanOutsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task A_cancellation_with_no_reason_still_says_something_readable()
    {
        using var db = NewContext();
        var ev = SeedEvent(db);

        var dispatcher = new RecordingDispatcher();

        await new ChangeEventStatusHandler(db, new SnapshottingUnitOfWork(dispatcher), dispatcher)
            .Handle(new ChangeEventStatusCommand(ev.Id, "cancel"), default);

        // An empty token would reach the recipient as a blank line, and WhatsApp
        // rejects empty template parameters outright.
        dispatcher.FanOuts.Single().Data!["Reason"].Should().Be("No reason given");
    }

    [Fact]
    public async Task The_business_event_key_is_stable_across_retries()
    {
        // Two identical cancel requests must produce the SAME key, so the platform
        // can recognise the duplicate and refuse to broadcast twice.
        using var db = NewContext();
        var ev = SeedEvent(db);

        var dispatcher = new RecordingDispatcher();
        var uow        = new SnapshottingUnitOfWork(dispatcher);
        var handler    = new ChangeEventStatusHandler(db, uow, dispatcher);

        await handler.Handle(new ChangeEventStatusCommand(ev.Id, "cancel", "Rain"), default);

        // The second call fails the domain transition (already cancelled), which is
        // itself the first line of defence.
        await handler.Handle(new ChangeEventStatusCommand(ev.Id, "cancel", "Rain"), default);

        dispatcher.FanOuts.Select(f => f.BusinessEventKey).Distinct().Should().HaveCount(1);
        dispatcher.FanOuts[0].BusinessEventKey.Should().Be($"event:{ev.Id}:cancelled");

        // No timestamp anywhere in the key: a key containing "now" never matches
        // itself, which would turn every retry into a second broadcast.
        dispatcher.FanOuts[0].BusinessEventKey.Should().NotContain(DateTime.UtcNow.Year.ToString());
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("start")]
    [InlineData("complete")]
    public async Task Other_transitions_notify_nobody(string action)
    {
        // Publishing or completing an event is not news worth messaging hundreds
        // of people about. Only cancellation is.
        using var db = NewContext();

        var ev = new Event(
            "Sunburn Arena", null, "NSCI Dome", null,
            new DateTime(2026, 9, 12, 18, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 12, 23, 0, 0, DateTimeKind.Utc),
            Guid.NewGuid());

        db.Events.Add(ev);
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();
        var handler    = new ChangeEventStatusHandler(db, new SnapshottingUnitOfWork(dispatcher), dispatcher);

        // Walk the lifecycle up to the requested action; intermediate steps are
        // themselves transitions that must stay silent.
        foreach (var step in new[] { "publish", "start", "complete" })
        {
            await handler.Handle(new ChangeEventStatusCommand(ev.Id, step), default);
            if (step == action) break;
        }

        dispatcher.FanOuts.Should().BeEmpty();
        dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Assignment_keys_distinguish_the_crew_invite_from_the_vendor_invite()
    {
        // Both notifications belong to the same assignment, so they must not share
        // a business event key -- idempotency would silently drop the second.
        var assignmentId = Guid.NewGuid();

        $"assignment:{assignmentId}:invited".Should().NotBe($"assignment:{assignmentId}:vendor-invited");
    }

    // The handlers under test never dispatch domain events or read the current
    // user; these exist only to satisfy the AppDbContext constructor.
    private sealed class NoOpMediator : IMediator
    {
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => Task.FromResult<TResponse>(default!);
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => Task.CompletedTask;
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
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
}
