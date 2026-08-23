using EventWOS.Application.Notifications.Commands;
using EventWOS.Application.Notifications.Queries;
using EventWOS.Application.Notifications.Rendering;
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
/// The inbox is the first place a recipient's own data is served back to them, so
/// these tests care most about the boundary: one user must never see or mutate
/// another user's notifications. The ids come from the client, so they cannot be
/// trusted to belong to the caller.
/// </summary>
public class NotificationInboxTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob   = Guid.NewGuid();

    public NotificationInboxTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"inbox-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

        _db.NotificationTemplates.Add(new NotificationTemplate(
            "EVENT_CANCELLED", NotificationChannel.InApp,
            "Important: {{EventName}} on {{EventDate}} has been CANCELLED.",
            "Event cancelled"));

        _db.SaveChanges();
    }

    private Notification Seed(Guid recipient, string data, bool withInApp = true, bool read = false)
    {
        var n = new Notification(
            recipient, "EVENT_CANCELLED", NotificationPriority.High,
            data, $"key-{Guid.NewGuid()}");

        if (withInApp)
            n.AddDelivery(NotificationChannel.InApp, destination: null, provider: "SignalR", templateVersion: 1);
        else
            n.AddDelivery(NotificationChannel.WhatsApp, "+919000000000", "AiSensy", 1);

        if (read) n.MarkReadByRecipient(DateTime.UtcNow);

        _db.Notifications.Add(n);
        _db.SaveChanges();
        return n;
    }

    private GetMyNotificationsHandler Query() =>
        new(_db, new NotificationTemplateRenderer());

    [Fact]
    public async Task The_inbox_renders_the_stored_data_not_a_raw_template()
    {
        Seed(_alice, """{"EventName":"Sunburn Arena","EventDate":"12 Sep 2026"}""");

        var result = await Query().Handle(new GetMyNotificationsQuery(_alice), default);

        var item = result.Value.Items.Should().ContainSingle().Subject;

        // The recipient must read the words, not the placeholders.
        item.Body.Should().Contain("Sunburn Arena").And.Contain("12 Sep 2026");
        item.Body.Should().NotContain("{{");
        item.Title.Should().Be("Event cancelled");
        item.IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task One_user_never_sees_another_users_notifications()
    {
        Seed(_alice, """{"EventName":"Alice event"}""");
        Seed(_bob,   """{"EventName":"Bob event"}""");

        var result = await Query().Handle(new GetMyNotificationsQuery(_alice), default);

        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].Body.Should().Contain("Alice event");
        result.Value.UnreadCount.Should().Be(1);
    }

    [Fact]
    public async Task WhatsApp_only_notifications_stay_out_of_the_app_inbox()
    {
        // Showing one would imply the inbox is a complete record of everything the
        // person was sent, when it is specifically the in-app copy.
        Seed(_alice, """{"EventName":"No in-app copy"}""", withInApp: false);

        var result = await Query().Handle(new GetMyNotificationsQuery(_alice), default);

        result.Value.Items.Should().BeEmpty();
        result.Value.UnreadCount.Should().Be(0);
    }

    [Fact]
    public async Task The_badge_counts_every_unread_not_just_the_current_page()
    {
        for (var i = 0; i < 5; i++)
            Seed(_alice, $$"""{"EventName":"Event {{i}}"}""");

        var result = await Query().Handle(new GetMyNotificationsQuery(_alice, Take: 2), default);

        result.Value.Items.Should().HaveCount(2);

        // A badge reading 2 while 5 wait would train the user to ignore it.
        result.Value.UnreadCount.Should().Be(5);
        result.Value.Total.Should().Be(5);
    }

    [Fact]
    public async Task A_missing_template_still_shows_the_row()
    {
        _db.NotificationTemplates.RemoveRange(_db.NotificationTemplates);
        await _db.SaveChangesAsync();

        Seed(_alice, """{"EventName":"Sunburn Arena"}""");

        var result = await Query().Handle(new GetMyNotificationsQuery(_alice), default);

        // Something happened to this person; silence is the worst possible answer.
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].Body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Corrupt_placeholder_json_does_not_break_the_page()
    {
        Seed(_alice, "{not valid json");

        var result = await Query().Handle(new GetMyNotificationsQuery(_alice), default);

        // Degraded to the template skeleton rather than a 500 for the whole inbox.
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Marking_read_is_scoped_to_the_caller()
    {
        var bobs = Seed(_bob, """{"EventName":"Bob event"}""");

        // Alice submits Bob's id, which she could have guessed or seen in a log.
        var result = await new MarkNotificationsReadHandler(_db, new FakeUnitOfWork(_db))
            .Handle(new MarkNotificationsReadCommand(_alice, new[] { bobs.Id }), default);

        result.Value.Should().Be(0);

        // Bob's notification must still be unread -- otherwise Alice just hid a
        // cancellation from him.
        _db.Notifications.Single(n => n.Id == bobs.Id).ReadAt.Should().BeNull();
    }

    [Fact]
    public async Task Marking_read_is_idempotent_and_keeps_the_first_read_time()
    {
        var n = Seed(_alice, """{"EventName":"Sunburn Arena"}""");

        var handler = new MarkNotificationsReadHandler(_db, new FakeUnitOfWork(_db));

        (await handler.Handle(new MarkNotificationsReadCommand(_alice, new[] { n.Id }), default))
            .Value.Should().Be(1);

        var firstReadAt = _db.Notifications.Single(x => x.Id == n.Id).ReadAt;

        // A double-tap on the bell must not rewrite when it was read.
        (await handler.Handle(new MarkNotificationsReadCommand(_alice, new[] { n.Id }), default))
            .Value.Should().Be(0);

        _db.Notifications.Single(x => x.Id == n.Id).ReadAt.Should().Be(firstReadAt);
    }

    [Fact]
    public async Task Read_all_touches_only_the_callers_unread_rows()
    {
        Seed(_alice, """{"EventName":"A1"}""");
        Seed(_alice, """{"EventName":"A2"}""");
        Seed(_alice, """{"EventName":"A3"}""", read: true);
        var bobs = Seed(_bob, """{"EventName":"B1"}""");

        var result = await new MarkNotificationsReadHandler(_db, new FakeUnitOfWork(_db))
            .Handle(new MarkNotificationsReadCommand(_alice), default);

        result.Value.Should().Be(2);
        _db.Notifications.Single(n => n.Id == bobs.Id).ReadAt.Should().BeNull();
    }

    [Fact]
    public async Task Reading_in_the_app_marks_only_the_in_app_delivery()
    {
        var n = new Notification(
            _alice, "EVENT_CANCELLED", NotificationPriority.High,
            """{"EventName":"Sunburn Arena"}""", $"key-{Guid.NewGuid()}");

        n.AddDelivery(NotificationChannel.InApp, null, "SignalR", 1);
        n.AddDelivery(NotificationChannel.Email, "crew@example.com", "SendGrid", 1);
        _db.Notifications.Add(n);
        await _db.SaveChangesAsync();

        await new MarkNotificationsReadHandler(_db, new FakeUnitOfWork(_db))
            .Handle(new MarkNotificationsReadCommand(_alice, new[] { n.Id }), default);

        var reloaded = _db.Notifications.Include(x => x.Deliveries).Single(x => x.Id == n.Id);

        reloaded.Deliveries.Single(d => d.Channel == NotificationChannel.InApp)
                .Status.Should().Be(NotificationStatus.Read);

        // Opening the app says NOTHING about whether the email was opened. Claiming
        // it would put a fact in the audit trail that nobody observed.
        reloaded.Deliveries.Single(d => d.Channel == NotificationChannel.Email)
                .ReadAt.Should().BeNull();
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        private readonly AppDbContext _db;
        public FakeUnitOfWork(AppDbContext db) => _db = db;
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
        public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

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
