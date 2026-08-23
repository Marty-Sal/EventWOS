using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Application.Payments.Commands;
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
/// Creating a payment is bookkeeping, not news. The row lands as Pending, its batch as
/// Draft, and it can still be rejected -- so the crew member hears nothing until the
/// money actually moves (PAYMENT_APPROVED / PAYROLL_RELEASED / PAYMENT_REJECTED, all
/// sent from the status commands).
///
/// This test exists because the opposite looks like a missing notification to anyone
/// auditing the call sites, and "fixing" it would put a WhatsApp message in front of
/// every crew member on a batch about money nobody has approved yet. If a future change
/// deliberately reverses this, it should have to change this test and say why.
/// </summary>
public class PaymentCreationStaysQuietTests
{
    private sealed class StrictDispatcher : INotificationDispatcher
    {
        public List<NotificationRequest> Requests { get; } = new();
        public void Enqueue(NotificationRequest request) => Requests.Add(request);
        public void Enqueue(IEnumerable<NotificationRequest> requests) => Requests.AddRange(requests);
        public void EnqueueFanOut(NotificationFanOutRequest request) => throw new InvalidOperationException("Unexpected fan-out.");
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

    private sealed class RecordingPusher : INotificationPusher
    {
        public List<string> Events { get; } = new();
        public Task PushToUserAsync(Guid userId, string eventName, object payload, CancellationToken ct = default)
        { Events.Add($"user:{eventName}"); return Task.CompletedTask; }
        public Task PushToRoleAsync(string role, string eventName, object payload, CancellationToken ct = default)
        { Events.Add($"role:{eventName}"); return Task.CompletedTask; }
        public Task PushToAllAsync(string eventName, object payload, CancellationToken ct = default)
        { Events.Add($"all:{eventName}"); return Task.CompletedTask; }
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
                .UseInMemoryDatabase($"payment-quiet-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

    [Fact]
    public async Task Recording_a_payment_tells_the_crew_member_nothing_but_still_refreshes_the_screens()
    {
        using var db = NewContext();
        var admin = Guid.NewGuid();

        var vendor = new User("9600000001", "Sameer Khan", UserRole.Vendor);
        var crew   = new User("9600000002", "Ravi Patel", UserRole.Crew);
        vendor.Approve(admin); crew.Approve(admin);
        crew.JoinVendor(vendor.Id);
        db.Users.AddRange(vendor, crew);

        // Payments are only allowed once the event has wrapped up.
        var ev = new Event("Sunburn Arena", null, "Vagator Grounds", null,
            new DateTime(2026, 8, 1, 16, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 1, 23, 0, 0, DateTimeKind.Utc), admin);
        ev.Publish();
        ev.Start();
        ev.Complete();
        db.Events.Add(ev);

        var assignment = new EventAssignment(ev.Id, crew.Id, vendor.Id, admin);
        db.EventAssignments.Add(assignment);
        await db.SaveChangesAsync();

        var dispatcher = new StrictDispatcher();
        var pusher = new RecordingPusher();

        var result = await new CreateCrewPaymentHandler(db, new PassThroughUnitOfWork(db), pusher)
            .Handle(new CreateCrewPaymentCommand(
                ev.Id, assignment.Id, crew.Id, vendor.Id, 4500m, "Rigging shift"), default);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        var payment = await db.CrewPayments.SingleAsync();
        payment.Status.Should().Be(PaymentStatus.Pending);

        // No durable message: nothing has been approved, and the row can still be
        // rejected. The crew member is told when the money moves, not when it is
        // written down.
        dispatcher.Requests.Should().BeEmpty();

        // The transient pushes stay, because they are cache-invalidation signals that
        // make MyPayments / Payments / VendorPayments refetch -- no toast listens to
        // them, so removing them would silently stale those screens.
        pusher.Events.Should().Contain("user:PaymentCreated");
        pusher.Events.Should().Contain("role:PayrollUpdated");
    }
}
