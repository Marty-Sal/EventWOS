using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace EventWOS.Application.UnitTests.Persistence;

/// <summary>
/// Guards against one specific, repeat-offender bug: a foreign key that exists
/// in the database but is not mapped in EF.
///
/// When a child row is inserted in the same SaveChanges as its parent and EF has
/// no knowledge of the relationship, EF is free to insert the child first and
/// Postgres rejects it with 23503. It has happened twice in production --
/// terms_acceptances during registration, then event_announcement_attachments
/// when posting an announcement -- and both times the code looked perfectly
/// correct, because the mapping is invisible at the call site.
///
/// These assertions read the built EF model, so they catch a missing mapping
/// without needing a database at all.
/// </summary>
public class EfRelationshipMappingTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"model-{Guid.NewGuid()}")
            .Options;

        using var db = new AppDbContext(options, new StubMediator(), new StubUser());
        return db.Model;
    }

    private static void AssertMapped<TDependent, TPrincipal>(string foreignKeyProperty)
    {
        var entity = Model().FindEntityType(typeof(TDependent));
        entity.Should().NotBeNull($"{typeof(TDependent).Name} must be part of the model");

        var mapped = entity!.GetForeignKeys().Any(fk =>
            fk.PrincipalEntityType.ClrType == typeof(TPrincipal) &&
            fk.Properties.Any(p => p.Name == foreignKeyProperty));

        mapped.Should().BeTrue(
            $"{typeof(TDependent).Name}.{foreignKeyProperty} -> {typeof(TPrincipal).Name} must be mapped in the " +
            "Persistence layer, or EF may insert the dependent before its principal and Postgres will reject it");
    }

    [Fact]
    public void Announcement_attachments_declare_their_parent()
        => AssertMapped<EventAnnouncementAttachment, EventAnnouncement>(nameof(EventAnnouncementAttachment.AnnouncementId));

    [Fact]
    public void Announcement_attachments_declare_their_file()
        => AssertMapped<EventAnnouncementAttachment, FileDocument>(nameof(EventAnnouncementAttachment.FileDocumentId));

    [Fact]
    public void Announcement_reads_declare_their_parent()
        => AssertMapped<EventAnnouncementRead, EventAnnouncement>(nameof(EventAnnouncementRead.AnnouncementId));

    [Fact]
    public void Announcements_declare_their_event()
        => AssertMapped<EventAnnouncement, Event>(nameof(EventAnnouncement.EventId));

    [Fact]
    public void Terms_acceptances_declare_their_user()
        // The original outage. Kept as a permanent regression guard.
        => AssertMapped<TermsAcceptance, User>(nameof(TermsAcceptance.UserId));

    [Fact]
    public void Notification_deliveries_declare_their_notification()
        => AssertMapped<NotificationDelivery, Notification>(nameof(NotificationDelivery.NotificationId));

    [Fact]
    public void Device_registrations_declare_their_user()
        => AssertMapped<DeviceRegistration, User>(nameof(DeviceRegistration.UserId));

    [Fact]
    public void Every_notification_table_is_in_the_model()
    {
        // Cheap guard that a DbSet was not forgotten, which would make the
        // dispatcher fail at runtime rather than at build time.
        var model = Model();

        model.FindEntityType(typeof(Notification)).Should().NotBeNull();
        model.FindEntityType(typeof(NotificationDelivery)).Should().NotBeNull();
        model.FindEntityType(typeof(NotificationTemplate)).Should().NotBeNull();
        model.FindEntityType(typeof(OutboxMessage)).Should().NotBeNull();
    }

    [Fact]
    public void Outbox_messages_are_not_soft_delete_filtered()
    {
        // A filtered-out outbox row is a message nobody sends and nobody can
        // see. The table carries the audit columns for consistency, but the
        // worker must be able to read every row.
        var entity = Model().FindEntityType(typeof(OutboxMessage));
        entity!.GetQueryFilter().Should().BeNull();
    }

    private sealed class StubMediator : IMediator
    {
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => Task.FromResult<TResponse>(default!);
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => Task.CompletedTask;
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubUser : ICurrentUser
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
