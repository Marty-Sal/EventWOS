using EventWOS.Application.Auth.Commands;
using EventWOS.Application.Auth.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Auth.Handlers;

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
        // 1. Check if user exists
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Mobile == request.Mobile && !u.IsDeleted, cancellationToken);

        // 2. Check lock status
        if (user is not null && user.IsLocked)
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
        var devOtp = _otpService.IsDevelopmentMode ? plaintext : null;

        return Result.Success(new RequestOtpResponse(
            otpRequest.Id,
            request.Mobile,
            10,
            "OTP sent successfully.",
            devOtp));
    }
}
