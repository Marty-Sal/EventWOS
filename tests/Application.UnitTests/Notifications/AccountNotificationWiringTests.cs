using EventWOS.Application.Approval.Commands;
using EventWOS.Application.Approval.Handlers;
using EventWOS.Application.Auth.Interfaces;
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
/// Guards the account-lifecycle notifications: approval and rejection.
///
/// These two matter more than they look. Both handlers already emailed and texted
/// the user, but every one of those side-effects is wrapped in catch/LogWarning --
/// so on a bad provider day an account was approved, or locked out for 24 hours,
/// and NOBODY ever told the person. The platform row is the durable record that
/// survives that, and these tests pin the two properties that make it work: it is
/// staged before the save, and it does not send a second, worse email alongside
/// the rich one the handler already sends.
/// </summary>
public class AccountNotificationWiringTests
{
    private sealed class RecordingDispatcher : INotificationDispatcher
    {
        public List<NotificationRequest> Requests { get; } = new();
        public void Enqueue(NotificationRequest request) => Requests.Add(request);
        public void Enqueue(IEnumerable<NotificationRequest> requests) => Requests.AddRange(requests);
        public void EnqueueFanOut(NotificationFanOutRequest request) { }
    }

    /// <summary>Snapshots what was staged at the moment the save ran.</summary>
    private sealed class SnapshottingUnitOfWork : IUnitOfWork
    {
        private readonly RecordingDispatcher _dispatcher;
        public SnapshottingUnitOfWork(RecordingDispatcher d) => _dispatcher = d;
        public int RequestsStagedAtSaveTime { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            RequestsStagedAtSaveTime = _dispatcher.Requests.Count;
            return Task.FromResult(1);
        }

        public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>
    /// Every provider here fails, on purpose. That is the scenario the platform row
    /// exists for, and the handlers must still succeed.
    /// </summary>
    private sealed class FailingEmail : IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string subject, string htmlBody, string? plainTextBody = null, CancellationToken ct = default)
            => throw new InvalidOperationException("SendGrid down");
        public Task<bool> SendApprovalEmailAsync(string toEmail, string fullName, string role, string? referralCode, string loginUrl, CancellationToken ct = default)
            => throw new InvalidOperationException("SendGrid down");
        public Task<bool> SendRejectionEmailAsync(string toEmail, string fullName, string reason, DateTime canRetryAt, CancellationToken ct = default)
            => throw new InvalidOperationException("SendGrid down");
        public Task<bool> SendPasswordResetOtpEmailAsync(string toEmail, string fullName, string otp, CancellationToken ct = default)
            => throw new InvalidOperationException("SendGrid down");
        public Task<bool> SendAccountInviteEmailAsync(string toEmail, string fullName, string role, string invitedByName, string setupLink, CancellationToken ct = default)
            => throw new InvalidOperationException("SendGrid down");
        public Task<bool> SendProfileCompletedEmailAsync(string toEmail, string inviterName, string fullName, string role, CancellationToken ct = default)
            => throw new InvalidOperationException("SendGrid down");
    }

    private sealed class FailingSms : ISmsProvider
    {
        public Task<bool> SendAsync(string mobile, string message, CancellationToken ct = default)
            => throw new InvalidOperationException("SMS gateway down");
    }

    private sealed class FailingPusher : INotificationPusher
    {
        public Task PushToUserAsync(Guid userId, string eventName, object payload, CancellationToken ct = default)
            => throw new InvalidOperationException("SignalR down");
        public Task PushToRoleAsync(string role, string eventName, object payload, CancellationToken ct = default)
            => throw new InvalidOperationException("SignalR down");
        public Task PushToAllAsync(string eventName, object payload, CancellationToken ct = default)
            => throw new InvalidOperationException("SignalR down");
    }

    private sealed class StubOtp : IOtpService
    {
        public (string Plaintext, string Hash) GenerateOtp() => ("000000", "hash");
        public bool VerifyOtp(string plaintext, string storedHash) => true;
        public Task<bool> SendOtpAsync(string mobile, string otp, CancellationToken ct = default) => Task.FromResult(true);
        public bool IsDevelopmentMode => true;
    }

    private sealed class NoOpAudit : IAuditLogger
    {
        public Task LogAsync(AuditAction action, string entityType, string? entityId = null,
            object? oldValues = null, object? newValues = null, string? additionalData = null,
            Guid? actorUserId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AdminUser : ICurrentUser
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string? Mobile => "9000000000";
        public UserRole? Role => UserRole.Admin;
        public IReadOnlyList<string> Permissions => Array.Empty<string>();
        public Guid? SessionId => null;
        public string? DeviceId => null;
        public string? IpAddress => null;
        public bool IsAuthenticated => true;
        public bool IsInRole(UserRole role) => role == UserRole.Admin;
        public bool HasPermission(string permission) => true;
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

    private sealed class AnonymousDbUser : ICurrentUser
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
                .UseInMemoryDatabase($"account-wiring-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousDbUser());

    private static User SeedPendingVendor(AppDbContext db)
    {
        var user = new User("9876500001", "Sameer Khan", UserRole.Vendor);
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static ApproveUserHandler NewApproveHandler(
        AppDbContext db, IUnitOfWork uow, RecordingDispatcher dispatcher, ICurrentUser me) =>
        new(db, uow, new NoOpAudit(), new FailingEmail(), new StubOtp(), new FailingSms(),
            new FailingPusher(), dispatcher, me,
            Options.Create(new AppUrlOptions { BaseUrl = "https://eventwos.app" }),
            NullLogger<ApproveUserHandler>.Instance);

    [Fact]
    public async Task Approving_an_account_stages_the_inbox_record_before_the_save()
    {
        using var db = NewContext();
        var user  = SeedPendingVendor(db);
        var admin = new AdminUser();

        var dispatcher = new RecordingDispatcher();
        var uow        = new SnapshottingUnitOfWork(dispatcher);

        var result = await NewApproveHandler(db, uow, dispatcher, admin)
            .Handle(new ApproveUserCommand(user.Id, admin.UserId!.Value), default);

        // Email, SMS and SignalR all threw. The approval must still stand -- those
        // are best-effort couriers, not the decision.
        result.IsSuccess.Should().BeTrue();

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("ACCOUNT_APPROVED");
        request.RecipientUserId.Should().Be(user.Id);
        request.Data!["RecipientName"].Should().Be("Sameer Khan");
        request.BusinessEventKey.Should().Be($"user:{user.Id}:approved");

        // Staged before the save, so approval and record commit together.
        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task The_approval_notification_is_in_app_only_so_it_cannot_duplicate_the_welcome_email()
    {
        using var db = NewContext();
        var user  = SeedPendingVendor(db);
        var admin = new AdminUser();

        var dispatcher = new RecordingDispatcher();

        await NewApproveHandler(db, new SnapshottingUnitOfWork(dispatcher), dispatcher, admin)
            .Handle(new ApproveUserCommand(user.Id, admin.UserId!.Value), default);

        // SendApprovalEmailAsync sends a branded onboarding email carrying the
        // vendor's referral code and a crew-recruiting link. The platform's
        // ACCOUNT_APPROVED email is a one-line courtesy note. Both going out means
        // the good email arrives next to a worse copy of itself.
        dispatcher.Requests.Single().Channels
            .Should().BeEquivalentTo(new[] { NotificationChannel.InApp });
    }

    [Fact]
    public async Task Rejecting_an_account_tells_the_applicant_why_and_when_they_can_retry()
    {
        using var db = NewContext();
        var user  = SeedPendingVendor(db);
        var admin = new AdminUser();

        var dispatcher = new RecordingDispatcher();
        var uow        = new SnapshottingUnitOfWork(dispatcher);

        var result = await new RejectUserHandler(
                db, uow, new NoOpAudit(), new FailingEmail(), new FailingSms(),
                new FailingPusher(), dispatcher, admin,
                NullLogger<RejectUserHandler>.Instance)
            .Handle(new RejectUserCommand(user.Id, admin.UserId!.Value, "ID proof was unreadable."), default);

        result.IsSuccess.Should().BeTrue();

        var request = dispatcher.Requests.Should().ContainSingle().Subject;
        request.TemplateCode.Should().Be("ACCOUNT_REJECTED");
        request.RecipientUserId.Should().Be(user.Id);

        // The reason AND the retry time, together: a rejection the applicant never
        // received is indistinguishable from being ignored, and they re-apply with
        // the same unreadable ID.
        request.Data!["Reason"].Should().StartWith("ID proof was unreadable.");
        request.Data!["Reason"].Should().Contain("re-apply after");

        uow.RequestsStagedAtSaveTime.Should().Be(1);
    }

    [Fact]
    public async Task A_rejection_with_no_stated_reason_still_reads_as_a_sentence()
    {
        using var db = NewContext();
        var user  = SeedPendingVendor(db);
        var admin = new AdminUser();

        var dispatcher = new RecordingDispatcher();

        var result = await new RejectUserHandler(
                db, new SnapshottingUnitOfWork(dispatcher), new NoOpAudit(),
                new FailingEmail(), new FailingSms(), new FailingPusher(), dispatcher, admin,
                NullLogger<RejectUserHandler>.Instance)
            .Handle(new RejectUserCommand(user.Id, admin.UserId!.Value, "   "), default);

        if (result.IsSuccess)
        {
            // An empty token would reach the recipient as a dangling "Reason:" line,
            // and WhatsApp rejects empty template parameters outright.
            dispatcher.Requests.Single().Data!["Reason"].Should().NotBeNullOrWhiteSpace();
            dispatcher.Requests.Single().Data!["Reason"].Should().Contain("re-apply after");
        }
        else
        {
            // Equally acceptable: the domain refuses a blank reason outright. Either
            // way the recipient never sees an empty explanation.
            dispatcher.Requests.Should().BeEmpty();
        }
    }
}
