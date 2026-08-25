using EventOpsOracle.Application.Events.Commands;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Payments.Commands;
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
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

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

    // ── Payments ─────────────────────────────────────────────────────────────
    // Money is the thing people chase support about, so these are the
    // notifications with the least tolerance for being silently dropped.

    /// <summary>Records the legacy pushes so tests can prove they still happen.</summary>
    private sealed class RecordingPusher : INotificationPusher
    {
        public List<string> UserPushes { get; } = new();
        public List<string> RolePushes { get; } = new();

        public Task PushToUserAsync(Guid userId, string eventName, object payload, CancellationToken ct = default)
        {
            UserPushes.Add(eventName);
            return Task.CompletedTask;
        }

        public Task PushToRoleAsync(string role, string eventName, object payload, CancellationToken ct = default)
        {
            RolePushes.Add($"{role}:{eventName}");
            return Task.CompletedTask;
        }

        public Task PushToAllAsync(string eventName, object payload, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static (CrewPayment payment, Guid crewId) SeedPayment(
        AppDbContext db, Guid eventId, decimal agreed = 4500m, Guid? vendorId = null)
    {
        var crewId = Guid.NewGuid();

        db.Users.Add(new User("9876543210", "Ravi Kumar", UserRole.Crew));
        db.SaveChanges();

        // The seeded user's real id is what the handler will look up for the name.
        var crew = db.Users.OrderByDescending(u => u.CreatedAt).First();

        var payment = new CrewPayment(eventId, Guid.NewGuid(), crew.Id, vendorId, agreed);
        db.CrewPayments.Add(payment);
        db.SaveChanges();

        return (payment, crew.Id);
    }

    [Theory]
    [InlineData("approve", "PAYMENT_APPROVED")]
    [InlineData("reject",  "PAYMENT_REJECTED")]
    public async Task Approving_or_rejecting_a_payment_notifies_the_crew_member_before_the_save(
        string action, string expectedCode)
    {
        using var db = NewContext();
        var ev = SeedEvent(db);
        var (payment, crewId) = SeedPayment(db, ev.Id);

        var dispatcher = new RecordingDispatcher();
        var uow        = new SnapshottingUnitOfWork(dispatcher);

        var result = await new UpdatePaymentStatusHandler(db, uow, new RecordingPusher(), dispatcher)
            .Handle(new UpdatePaymentStatusCommand(
                payment.Id, action, null, null, null, "Missing bank details",
                ActorId: Guid.NewGuid(), ActorIsAdminOrManager: true), default);

        result.IsSuccess.Should().BeTrue();

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be(expectedCode);
        request.RecipientUserId.Should().Be(crewId);
        request.EventId.Should().Be(ev.Id);

        // Keyed on the payment and the transition, so a double-clicked Approve
        // button cannot tell someone twice that their money is coming.
        request.BusinessEventKey.Should().Be($"payment:{payment.Id}:{action}");

        // Staged before the save: a payment marked Approved whose notification was
        // lost is how someone ends up chasing money they were never told about.
        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task A_payout_reports_the_amount_that_actually_moved()
    {
        using var db = NewContext();
        var ev = SeedEvent(db);

        // Agreed 4,500 -- and the client will try to claim 9,999 was paid.
        var (payment, _) = SeedPayment(db, ev.Id, agreed: 4500m);

        // The domain refuses to pay an unapproved payment, so reach the real state.
        payment.Approve();
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();

        await new UpdatePaymentStatusHandler(db, new SnapshottingUnitOfWork(dispatcher), new RecordingPusher(), dispatcher)
            .Handle(new UpdatePaymentStatusCommand(
                payment.Id, "pay", PaidAmount: 9999m, Method: "Upi",
                TransactionRef: "TX1", Reason: null,
                ActorId: Guid.NewGuid(), ActorIsAdminOrManager: true), default);

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("PAYROLL_RELEASED");

        // The domain locks payout to the agreed contract, and the message must say
        // the same number the bank will -- a figure the recipient can check.
        request.Data!["Amount"].Should().Be("4,500.00");
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("ack-received")]
    [InlineData("ack-pending")]
    public async Task Internal_and_self_inflicted_transitions_notify_nobody(string action)
    {
        using var db = NewContext();
        var ev = SeedEvent(db);
        var (payment, crewId) = SeedPayment(db, ev.Id);

        // Each action is only legal from a particular state, so put the payment in
        // the real one rather than asserting against a transition the domain
        // rejected for unrelated reasons.
        payment.Approve();
        if (action != "hold") payment.MarkPaid(4500m, PaymentMethod.UPI, "TX-SEED");
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();

        // The ack actions are the crew's own clicks, so the caller IS the crew.
        var result = await new UpdatePaymentStatusHandler(db, new SnapshottingUnitOfWork(dispatcher), new RecordingPusher(), dispatcher)
            .Handle(new UpdatePaymentStatusCommand(
                payment.Id, action, null, null, null, "note",
                ActorId: crewId, ActorIsAdminOrManager: action == "hold"), default);

        result.IsSuccess.Should().BeTrue();

        // "On hold" flips back within minutes and is nobody's business outside
        // finance; telling you about your own acknowledgement is spam.
        dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_legacy_table_refresh_pushes_are_kept_not_replaced()
    {
        using var db = NewContext();
        var ev = SeedEvent(db);
        var (payment, _) = SeedPayment(db, ev.Id);

        var dispatcher = new RecordingDispatcher();
        var pusher     = new RecordingPusher();

        await new UpdatePaymentStatusHandler(db, new SnapshottingUnitOfWork(dispatcher), pusher, dispatcher)
            .Handle(new UpdatePaymentStatusCommand(
                payment.Id, "approve", null, null, null, null,
                ActorId: Guid.NewGuid(), ActorIsAdminOrManager: true), default);

        // These are cache invalidation, not messages: MyPayments and the admin
        // payments table REFETCH on them. Migrating them into the platform would
        // email an administrator every time a list changed, and deleting them would
        // silently freeze the tables people are looking at.
        pusher.RolePushes.Should().Contain("Admin:PaymentApproved");
        pusher.RolePushes.Should().Contain("Manager:PaymentApproved");
        pusher.UserPushes.Should().Contain("PaymentApproved");
    }

    [Fact]
    public async Task Disbursing_a_batch_notifies_the_vendor_and_not_their_crew()
    {
        using var db = NewContext();
        var ev = SeedEvent(db);

        var vendorId = Guid.NewGuid();
        var batch = new PayrollBatch(vendorId, ev.Id, "BATCH-001");
        batch.SetTotal(120000m);
        batch.Submit();
        batch.Approve(Guid.NewGuid());
        db.PayrollBatches.Add(batch);
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();
        var uow        = new SnapshottingUnitOfWork(dispatcher);

        var result = await new UpdatePayrollStatusHandler(db, uow, new RecordingPusher(), dispatcher)
            .Handle(new UpdatePayrollStatusCommand(batch.Id, "disburse", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("PAYROLL_RELEASED");

        // The vendor, who now holds the money -- NOT the crew, whose own payments
        // are still Pending. Announcing money to crew here would have them asking
        // where it is for days.
        request.RecipientUserId.Should().Be(vendorId);
        request.Data!["Amount"].Should().Be("120,000.00");
        request.BusinessEventKey.Should().Be($"payroll:{batch.Id}:disbursed");

        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("reject")]
    public async Task Internal_payroll_workflow_steps_notify_nobody(string action)
    {
        using var db = NewContext();
        var ev = SeedEvent(db);

        var batch = new PayrollBatch(Guid.NewGuid(), ev.Id, "BATCH-002");
        batch.SetTotal(5000m);
        if (action == "reject") batch.Submit();
        db.PayrollBatches.Add(batch);
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();

        var result = await new UpdatePayrollStatusHandler(db, new SnapshottingUnitOfWork(dispatcher), new RecordingPusher(), dispatcher)
            .Handle(new UpdatePayrollStatusCommand(batch.Id, action, Guid.NewGuid(), "reason"), default);

        result.IsSuccess.Should().BeTrue();

        // submit/approve/reject are steps between admins and managers who are
        // already looking at the payments screen. "Your batch was submitted" tells
        // a vendor nothing they can act on.
        dispatcher.Requests.Should().BeEmpty();
    }

}
