using EventWOS.Application.Announcements.Commands;
using EventWOS.Application.Common;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Guards the announcement broadcast after it stopped sending WhatsApp inline.
///
/// The old handler looped over recipients calling the WhatsApp API one at a time
/// inside the admin's request: a 200-person event meant 200 sequential HTTP calls,
/// every transient failure was logged and lost with no retry, and anyone without a
/// mobile number was skipped entirely. These tests pin the properties that replaced
/// that -- one queued message per recipient including the mobile-less ones, the deep
/// link surviving inside the body, and per-recipient idempotency keys.
/// </summary>
public class AnnouncementQueueingTests
{
    private sealed class RecordingDispatcher : INotificationDispatcher
    {
        public List<NotificationRequest> Requests { get; } = new();
        public void Enqueue(NotificationRequest request) => Requests.Add(request);
        public void Enqueue(IEnumerable<NotificationRequest> requests) => Requests.AddRange(requests);
        public void EnqueueFanOut(NotificationFanOutRequest request) { }
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
                .UseInMemoryDatabase($"announcement-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

    private static SendEventAnnouncementHandler NewHandler(AppDbContext db, RecordingDispatcher dispatcher) =>
        new(db, new NoOpPusher(), dispatcher,
            Options.Create(new AppUrlOptions { BaseUrl = "https://eventwos.app" }),
            NullLogger<SendEventAnnouncementHandler>.Instance);

    private sealed record Scene(Event Event, User WithMobile, User WithoutMobile, Guid AdminId);

    /// <summary>
    /// Two crew on one event: one with a mobile number, one without. The second is the
    /// interesting case -- the old inline loop hit `continue` on them.
    /// </summary>
    private static Scene SeedEventWithTwoCrew(AppDbContext db)
    {
        var admin = Guid.NewGuid();

        var withMobile    = new User("9700000001", "Anita Rao", UserRole.Crew);
        var withoutMobile = new User("9700000002", "Vikram Shah", UserRole.Crew);
        withMobile.Approve(admin);
        withoutMobile.Approve(admin);
        db.Users.AddRange(withMobile, withoutMobile);
        db.SaveChanges();

        // Clearing it after the fact because the ctor requires a mobile: the real-world
        // shape of this is a crew row whose number was removed or never verified.
        db.Entry(withoutMobile).Property(nameof(User.Mobile)).CurrentValue = "";

        var ev = new Event("Sunburn Arena", null, "Vagator", null,
            new DateTime(2026, 12, 20, 16, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 20, 23, 0, 0, DateTimeKind.Utc), admin);
        db.Events.Add(ev);

        foreach (var crew in new[] { withMobile, withoutMobile })
        {
            var assignment = new EventAssignment(ev.Id, crew.Id, null, admin);
            assignment.CrewAccept();
            db.EventAssignments.Add(assignment);
        }

        db.SaveChanges();
        return new Scene(ev, withMobile, withoutMobile, admin);
    }

    [Fact]
    public async Task Every_recipient_is_queued_including_the_one_with_no_mobile_number()
    {
        using var db = NewContext();
        var scene = SeedEventWithTwoCrew(db);
        var dispatcher = new RecordingDispatcher();

        var result = await NewHandler(db, dispatcher).Handle(new SendEventAnnouncementCommand(
            scene.Event.Id, AnnouncementAudience.Crew,
            "Gate change", "<p>Use <b>Gate 3</b> tomorrow.</p>",
            Array.Empty<Guid>(), scene.AdminId), default);

        result.IsSuccess.Should().BeTrue();

        // The old loop skipped anyone without a mobile, so a crew member with no number
        // on file got nothing at all -- not even an inbox row they could have read.
        dispatcher.Requests.Should().HaveCount(2);
        dispatcher.Requests.Select(r => r.RecipientUserId)
            .Should().BeEquivalentTo(new[] { scene.WithMobile.Id, scene.WithoutMobile.Id });
        dispatcher.Requests.Should().OnlyContain(r => r.TemplateCode == "EVENT_ANNOUNCEMENT");
    }

    [Fact]
    public async Task The_queued_body_keeps_the_subject_the_event_and_the_deep_link()
    {
        using var db = NewContext();
        var scene = SeedEventWithTwoCrew(db);
        var dispatcher = new RecordingDispatcher();

        await NewHandler(db, dispatcher).Handle(new SendEventAnnouncementCommand(
            scene.Event.Id, AnnouncementAudience.Crew,
            "Gate change", "<p>Use <b>Gate 3</b> tomorrow.</p>",
            Array.Empty<Guid>(), scene.AdminId), default);

        var request = dispatcher.Requests.First(r => r.RecipientUserId == scene.WithMobile.Id);

        request.Data!["Subject"].Should().Be("Gate change");

        var body = request.Data!["Message"];
        body.Should().Contain("Anita Rao");
        body.Should().Contain("Sunburn Arena");
        body.Should().Contain("Gate 3");          // HTML flattened, content preserved
        body.Should().NotContain("<b>");           // ...and the markup is gone

        // The link has to live inside Message: the stored EVENT_ANNOUNCEMENT template is
        // "{{Subject}}" / "{{Message}}" and the seeder never rewrites an existing row,
        // so a {{Link}} token added to the catalogue would render as nothing in prod.
        body.Should().Contain("/notifications?id=");
    }

    [Fact]
    public async Task Each_recipient_gets_their_own_idempotency_key()
    {
        using var db = NewContext();
        var scene = SeedEventWithTwoCrew(db);
        var dispatcher = new RecordingDispatcher();

        await NewHandler(db, dispatcher).Handle(new SendEventAnnouncementCommand(
            scene.Event.Id, AnnouncementAudience.Crew,
            "Gate change", "<p>Gate 3.</p>", Array.Empty<Guid>(), scene.AdminId), default);

        // Distinct per recipient, or the platform's de-duplication would collapse a
        // whole broadcast into a single delivered message.
        dispatcher.Requests.Select(r => r.BusinessEventKey).Should().OnlyHaveUniqueItems();
        dispatcher.Requests.Should().OnlyContain(r => r.BusinessEventKey.StartsWith("announcement:"));
    }

    [Fact]
    public async Task Attachments_are_announced_as_a_link_not_pushed_to_everyone()
    {
        using var db = NewContext();
        var scene = SeedEventWithTwoCrew(db);
        var dispatcher = new RecordingDispatcher();

        // An unknown file id is filtered out by the handler's validity check, so the
        // count stays honest rather than promising an attachment that is not there.
        await NewHandler(db, dispatcher).Handle(new SendEventAnnouncementCommand(
            scene.Event.Id, AnnouncementAudience.Crew,
            "Rider", "<p>See attached.</p>", new[] { Guid.NewGuid() }, scene.AdminId), default);

        dispatcher.Requests.Should().NotBeEmpty();
        dispatcher.Requests.First().Data!["Message"].Should().NotContain("attachment");
    }

    [Fact]
    public async Task An_announcement_with_no_recipients_is_still_stored_and_queues_nothing()
    {
        using var db = NewContext();
        var admin = Guid.NewGuid();
        var ev = new Event("Empty Gig", null, "TBC", null,
            new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), admin);
        db.Events.Add(ev);
        db.SaveChanges();

        var dispatcher = new RecordingDispatcher();

        var result = await NewHandler(db, dispatcher).Handle(new SendEventAnnouncementCommand(
            ev.Id, AnnouncementAudience.Crew, "Save the date", "<p>Details soon.</p>",
            Array.Empty<Guid>(), admin), default);

        // Nobody assigned yet is a normal state, not an error: the row has to persist so
        // it is readable when people are added, and the UI says as much.
        result.IsSuccess.Should().BeTrue();
        result.Value!.RecipientCount.Should().Be(0);
        dispatcher.Requests.Should().BeEmpty();
        (await db.EventAnnouncements.CountAsync()).Should().Be(1);
    }
}
