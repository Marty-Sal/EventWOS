using EventWOS.Application.Notifications.Contracts;
using EventWOS.Application.Notifications.Services;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Covers the dispatcher's contract, which the whole reliability story rests on:
/// it stages outbox rows on the caller's DbContext and does NOT save. If it ever
/// starts saving on its own, notifications stop being transactional with the
/// business data and the guarantees in the design collapse quietly.
/// </summary>
public class NotificationDispatcherTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly NotificationDispatcher _sut;

    public NotificationDispatcherTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"notif-{Guid.NewGuid()}")
            // The in-memory provider warns about the transaction API the real
            // context uses; irrelevant here, since these tests never save.
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db  = new AppDbContext(options, new NoOpMediator(), new AnonymousUser());
        _sut = new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance);
    }

    private static NotificationRequest Request(Guid? recipient = null, string key = "assignment:1:created")
        => new(
            NotificationTemplateCodes.CrewAssignment,
            recipient ?? Guid.NewGuid(),
            key,
            new Dictionary<string, string?> { ["EventName"] = "Sunburn" },
            EventId: Guid.NewGuid());

    private List<OutboxMessage> Staged =>
        _db.ChangeTracker.Entries<OutboxMessage>().Select(e => e.Entity).ToList();

    [Fact]
    public void Stages_an_outbox_row_without_saving()
    {
        _sut.Enqueue(Request());

        Staged.Should().HaveCount(1);
        // Nothing committed: the caller's SaveChanges is what makes it real.
        _db.OutboxMessages.AsNoTracking().Should().BeEmpty();
    }

    [Fact]
    public void Outbox_row_carries_the_message_type_the_worker_switches_on()
    {
        _sut.Enqueue(Request());

        var row = Staged.Single();
        row.MessageType.Should().Be(OutboxMessageTypes.NotificationRequested);
        row.Status.Should().Be(OutboxStatus.Pending);
        row.PayloadJson.Should().Contain(NotificationTemplateCodes.CrewAssignment);
    }

    [Fact]
    public void Priority_defaults_come_from_policy_and_are_serialised_as_names()
    {
        _sut.Enqueue(new NotificationRequest(
            NotificationTemplateCodes.EventCancelled, Guid.NewGuid(), "event:1:cancelled"));

        // Readable at 2am, and Critical because someone may be travelling to it.
        Staged.Single().PayloadJson.Should().Contain("\"Priority\":\"Critical\"");
    }

    [Fact]
    public void Many_recipients_of_the_same_message_share_one_outbox_row()
    {
        var eventId = Guid.NewGuid();
        var requests = Enumerable.Range(0, 40).Select(i => new NotificationRequest(
            NotificationTemplateCodes.CrewAssignment, Guid.NewGuid(), $"assignment:{i}:created",
            EventId: eventId)).ToList();

        _sut.Enqueue(requests);

        Staged.Should().HaveCount(1, "40 crew on one assignment action is one logical message");
    }

    [Fact]
    public void Large_recipient_lists_are_chunked_rather_than_written_as_one_giant_payload()
    {
        var eventId = Guid.NewGuid();
        var requests = Enumerable.Range(0, 250).Select(i => new NotificationRequest(
            NotificationTemplateCodes.CrewAssignment, Guid.NewGuid(), $"assignment:{i}:created",
            EventId: eventId)).ToList();

        _sut.Enqueue(requests);

        Staged.Should().HaveCount(3, "100 recipients per row keeps a single row readable and requeueable");
    }

    [Fact]
    public void Different_templates_do_not_get_merged_into_one_row()
    {
        var eventId = Guid.NewGuid();
        _sut.Enqueue(new[]
        {
            new NotificationRequest(NotificationTemplateCodes.CrewAssignment, Guid.NewGuid(), "a:1", EventId: eventId),
            new NotificationRequest(NotificationTemplateCodes.ShiftChanged,   Guid.NewGuid(), "b:1", EventId: eventId),
        });

        Staged.Should().HaveCount(2);
    }

    [Fact]
    public void The_same_recipient_and_business_key_twice_in_one_call_is_collapsed()
    {
        var crewId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _sut.Enqueue(new[]
        {
            new NotificationRequest(NotificationTemplateCodes.CrewAssignment, crewId, "assignment:7:created", EventId: eventId),
            new NotificationRequest(NotificationTemplateCodes.CrewAssignment, crewId, "assignment:7:created", EventId: eventId),
        });

        // The unique index would reject the duplicate later; collapsing here
        // avoids doing the work only to throw it away.
        Staged.Single().PayloadJson.Split(crewId.ToString()).Length.Should().Be(2);
    }

    [Fact]
    public void A_missing_business_event_key_is_rejected_loudly()
    {
        // Silently generating a key would make every retry a duplicate message.
        var act = () => _sut.Enqueue(new NotificationRequest(
            NotificationTemplateCodes.CrewAssignment, Guid.NewGuid(), "   "));

        act.Should().Throw<ArgumentException>().WithMessage("*BusinessEventKey*");
    }

    [Fact]
    public void Fan_out_writes_a_single_row_regardless_of_audience_size()
    {
        _sut.EnqueueFanOut(new NotificationFanOutRequest(
            NotificationTemplateCodes.EventCancelled,
            NotificationAudience.EventCrewAndVendors,
            Guid.NewGuid(),
            "event:42:cancelled"));

        var row = Staged.Single();
        row.MessageType.Should().Be(OutboxMessageTypes.NotificationFanOut);
        row.PayloadJson.Should().Contain("EventCrewAndVendors");
    }

    [Fact]
    public void Fan_out_without_a_business_key_is_rejected()
    {
        var act = () => _sut.EnqueueFanOut(new NotificationFanOutRequest(
            NotificationTemplateCodes.EventStarting, NotificationAudience.EventCrew, Guid.NewGuid(), ""));

        act.Should().Throw<ArgumentException>();
    }

    public void Dispose() => _db.Dispose();

    // The dispatcher never dispatches domain events or reads the current user;
    // these exist only to satisfy the AppDbContext constructor.
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
