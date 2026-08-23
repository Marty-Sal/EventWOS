using EventWOS.Application.Auth.Commands;
using EventWOS.Application.Auth.Handlers;
using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventWOS.Application.UnitTests.Auth;

/// <summary>
/// The OTP is a bearer credential: VerifyOtpHandler mints an access token from it. So
/// returning the plaintext in an API response does not merely leak a code -- it turns
/// "knows your mobile number" into "is signed in as you", for any account including admin.
///
/// These tests pin the two things that keep that shut:
///   1. the response only carries the OTP when ExposeOtpInApiResponse is explicitly on,
///      and NOT merely because SMS delivery is stubbed (IsDevelopmentMode), which is
///      expected to stay on in a deployed environment until a provider is live;
///   2. the base appsettings.json -- the file that applies in production -- does not
///      enable exposure.
/// </summary>
public class OtpExposureTests
{
    private sealed class ConfigurableOtp : IOtpService
    {
        public const string Code = "424242";
        public bool IsDevelopmentMode { get; init; } = true;
        public bool ExposeOtpInApiResponse { get; init; }
        public (string Plaintext, string Hash) GenerateOtp() => (Code, "bcrypt-hash");
        public bool VerifyOtp(string plaintext, string storedHash) => true;
        public Task<bool> SendOtpAsync(string mobile, string otp, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class SilentEmail : IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string subject, string htmlBody, string? plainTextBody = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendApprovalEmailAsync(string toEmail, string fullName, string role, string? referralCode, string loginUrl, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendRejectionEmailAsync(string toEmail, string fullName, string reason, DateTime canRetryAt, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendPasswordResetOtpEmailAsync(string toEmail, string fullName, string otp, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendAccountInviteEmailAsync(string toEmail, string fullName, string role, string invitedByName, string setupLink, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendProfileCompletedEmailAsync(string toEmail, string inviterName, string fullName, string role, CancellationToken ct = default) => Task.FromResult(true);
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

    private sealed class NoOpAudit : IAuditLogger
    {
        public Task LogAsync(AuditAction action, string entityType, string? entityId = null,
            object? oldValues = null, object? newValues = null, string? additionalData = null,
            Guid? actorUserId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
                .UseInMemoryDatabase($"otp-exposure-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousUser());

    private static async Task<User> SeedActiveAdminAsync(AppDbContext db)
    {
        var admin = new User("9800000001", "Site Admin", UserRole.Admin);
        admin.Approve(admin.Id);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        return admin;
    }

    private static async Task<RequestPasswordResetResponse> RunAsync(AppDbContext db, IOtpService otp, string key)
    {
        var result = await new RequestPasswordResetHandler(
                db, otp, new SilentEmail(), new PassThroughUnitOfWork(db), new NoOpAudit(),
                NullLogger<RequestPasswordResetHandler>.Instance)
            .Handle(new RequestPasswordResetCommand(key, "203.0.113.9"), default);

        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    [Fact]
    public async Task Stubbed_SMS_alone_does_not_hand_the_reset_code_back_to_the_caller()
    {
        using var db = NewContext();
        var admin = await SeedActiveAdminAsync(db);

        // The deployed shape while no SMS provider is live: delivery is stubbed, but
        // exposure is off. Anyone probing forgot-password must learn nothing usable.
        var response = await RunAsync(db, new ConfigurableOtp { IsDevelopmentMode = true, ExposeOtpInApiResponse = false }, admin.Mobile);

        response.DevOtp.Should().BeNull(
            "the OTP mints an access token, so returning it lets anyone who knows a mobile number take the account");
        response.OtpRequestId.Should().NotBeNull("the reset itself must still work for whoever received the code");
        (await db.OtpRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Explicit_exposure_still_works_for_local_development()
    {
        using var db = NewContext();
        var admin = await SeedActiveAdminAsync(db);

        var response = await RunAsync(db, new ConfigurableOtp { ExposeOtpInApiResponse = true }, admin.Mobile);

        response.DevOtp.Should().Be(ConfigurableOtp.Code);
    }

    [Fact]
    public async Task An_unknown_account_never_gets_a_code_or_an_otp_row()
    {
        using var db = NewContext();
        await SeedActiveAdminAsync(db);

        var response = await RunAsync(db, new ConfigurableOtp { ExposeOtpInApiResponse = true }, "9799999999");

        response.OtpRequestId.Should().BeNull();
        response.DevOtp.Should().BeNull("even with exposure on, a non-existent account must not leak that it is absent");
        (await db.OtpRequests.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// appsettings.json is the file that applies in production. This is the guard for the
    /// actual root cause we found: a dangerous default sitting in the base config where
    /// nobody looks, rather than in appsettings.Development.json where it belongs.
    /// </summary>
    [Fact]
    public void Base_appsettings_does_not_expose_the_otp()
    {
        var root = FindRepositoryRoot();
        var baseSettings = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "src", "Api", "appsettings.json")));

        var otp = baseSettings.RootElement.GetProperty("Otp");
        otp.TryGetProperty("ExposeOtpInApiResponse", out var expose).Should().BeTrue(
            "the flag must be present and explicit in the base config, not left to a code default");
        expose.GetBoolean().Should().BeFalse(
            "production reads this file; exposing the OTP here is an account-takeover switch");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EventWOS.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must be able to locate the repository root");
        return dir!.FullName;
    }
}
