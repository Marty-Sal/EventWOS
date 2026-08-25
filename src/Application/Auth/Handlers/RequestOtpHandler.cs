using EventOpsOracle.Application.Auth.Commands;
using EventOpsOracle.Application.Auth.Interfaces;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Application.Auth.Handlers;

/// <summary>
/// Handles OTP request:
/// 1. Validates mobile exists or creates new user record
/// 2. Invalidates existing pending OTPs for this mobile
/// 3. Generates + hashes new OTP
/// 4. Stores OtpRequest record
/// 5. Dispatches SMS
/// </summary>
public sealed class RequestOtpHandler : IRequestHandler<RequestOtpCommand, Result<RequestOtpResponse>>
{
    private readonly IAppDbContext _db;
    private readonly IOtpService _otpService;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<RequestOtpHandler> _logger;

    public RequestOtpHandler(
        IAppDbContext db,
        IOtpService otpService,
        IUnitOfWork uow,
        IAuditLogger audit,
        ILogger<RequestOtpHandler> logger)
    {
        _db = db;
        _otpService = otpService;
        _uow = uow;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Result<RequestOtpResponse>> Handle(
        RequestOtpCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Check if user exists. OTP login is for EXISTING, approved accounts
        // only — it must never be a back door that silently registers whoever
        // types a random mobile number. Real registration always goes through
        // RegisterCrewHandler / RegisterVendorHandler (validation, approval
        // queue, referral checks, etc.). See VerifyOtpHandler for the matching
        // guard on the verify side.
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Mobile == request.Mobile && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            _logger.LogWarning("OTP requested for unregistered mobile: {Mobile}", request.Mobile);
            return Result.Failure<RequestOtpResponse>(Error.Custom(
                "Auth.NotRegistered",
                "No account found for this mobile number. Please register first."));
        }

        // 2. Check account status before sending any SMS — mirrors the gate
        // LoginWithPasswordHandler applies before verifying a password.
        switch (user.Status)
        {
            case UserStatus.Suspended:
            case UserStatus.Deactivated:
                return Result.Failure<RequestOtpResponse>(Error.AccountSuspended);
            case UserStatus.Pending:
                return Result.Failure<RequestOtpResponse>(Error.Custom(
                    "Auth.PendingApproval",
                    "Your account is awaiting approval. You'll receive an email once it's approved."));
            case UserStatus.Rejected:
                return Result.Failure<RequestOtpResponse>(Error.Custom(
                    "Auth.Rejected",
                    "Your registration was not approved."));
        }

        if (user.IsLocked)
        {
            _logger.LogWarning("OTP requested for locked account: {Mobile}", request.Mobile);
            return Result.Failure<RequestOtpResponse>(Error.AccountLocked);
        }

        // 3. Expire previous pending OTPs for this mobile
        var existingOtps = await _db.OtpRequests
            .Where(o => o.Mobile == request.Mobile && o.Status == OtpStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var otp in existingOtps)
            otp.MarkExpired();

        // 4. Generate new OTP
        var (plaintext, hash) = _otpService.GenerateOtp();

        var otpRequest = new OtpRequest(
            request.Mobile,
            hash,
            request.DeviceId,
            request.IpAddress);

        _db.OtpRequests.Add(otpRequest);
        await _uow.SaveChangesAsync(cancellationToken);

        // 5. Send SMS (fire-and-forget in handler, log failures)
        var sent = await _otpService.SendOtpAsync(request.Mobile, plaintext, cancellationToken);
        if (!sent)
            _logger.LogError("SMS dispatch failed for mobile: {Mobile}", request.Mobile);

        await _audit.LogAsync(
            AuditAction.OtpRequested,
            nameof(OtpRequest),
            otpRequest.Id.ToString(),
            additionalData: $"IP:{request.IpAddress}",
            cancellationToken: cancellationToken);

        _logger.LogInformation("OTP requested for {Mobile}, RequestId: {Id}", request.Mobile, otpRequest.Id);

        // In development mode, include the plaintext OTP in the response
        // so the UI can show it without needing a real SMS provider
        // Never gate this on IsDevelopmentMode: that flag only means "SMS is stubbed", and
        // it is expected to stay on in a deployed environment until a provider is live.
        // Handing the OTP back is a separate, far more dangerous decision -- VerifyOtp
        // mints an access token, so exposing it turns knowing a mobile number into a
        // signed in session for that account.
        var devOtp = _otpService.ExposeOtpInApiResponse ? plaintext : null;

        return Result.Success(new RequestOtpResponse(
            otpRequest.Id,
            request.Mobile,
            10,
            "OTP sent successfully.",
            devOtp));
    }
}
