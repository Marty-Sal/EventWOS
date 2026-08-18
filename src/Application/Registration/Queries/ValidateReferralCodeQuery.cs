using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Registration.Queries;

/// <summary>
/// Public, anonymous "is this vendor referral code valid" check — lets the
/// Crew sign-up form validate the code inline (before the user fills out
/// the rest of the form and hits submit) instead of only finding out at
/// final submission. Read-only; leaks nothing beyond validity + the
/// vendor's display name (useful for a "You're joining Acme Events" hint).
/// </summary>
public sealed record ValidateReferralCodeQuery(string? Code) : IRequest<ReferralCodeCheckResult>;

public sealed record ReferralCodeCheckResult(bool IsValid, string? VendorBusinessName);

public sealed class ValidateReferralCodeHandler : IRequestHandler<ValidateReferralCodeQuery, ReferralCodeCheckResult>
{
    private readonly IAppDbContext _db;
    public ValidateReferralCodeHandler(IAppDbContext db) => _db = db;

    public async Task<ReferralCodeCheckResult> Handle(ValidateReferralCodeQuery req, CancellationToken ct)
    {
        var code = req.Code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
            return new ReferralCodeCheckResult(false, null);

        var vendor = await _db.Users
            .Where(u => u.Role == UserRole.Vendor && u.Status == UserStatus.Active && u.ReferralCode == code)
            .Select(u => new { u.BusinessName, u.FullName })
            .FirstOrDefaultAsync(ct);

        return vendor is null
            ? new ReferralCodeCheckResult(false, null)
            : new ReferralCodeCheckResult(true, vendor.BusinessName ?? vendor.FullName);
    }
}
