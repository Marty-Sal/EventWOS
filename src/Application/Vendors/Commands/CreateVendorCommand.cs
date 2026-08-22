using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Vendors.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventWOS.Application.Vendors.Commands;

public sealed record CreateVendorCommand(
    string Mobile, string FullName, string? BusinessName, string? Email, Guid RequestingUserId
) : IRequest<Result<VendorDto>>;

/// <summary>
/// Admin directly adds a Vendor. Skips self-registration and the approval
/// queue entirely — the account is Active immediately (an Admin already
/// vouched for it). Instead we force the first-login password-setup flow
/// (same one grandfathered users go through) and notify the new vendor by
/// email + WhatsApp with a link to set their password and fill in their
/// profile. See User.MarkAsDirectlyAdded / UpdateProfileHandler for the
/// "notify the admin once the profile is filled in" side of this loop.
/// </summary>
public sealed class CreateVendorHandler : IRequestHandler<CreateVendorCommand, Result<VendorDto>>
{
    private readonly IAppDbContext _db;
    private readonly IEmailService _email;
    private readonly IWhatsAppProvider _whatsApp;
    private readonly AppUrlOptions _appUrls;
    private readonly ILogger<CreateVendorHandler> _logger;

    public CreateVendorHandler(
        IAppDbContext db, IEmailService email, IWhatsAppProvider whatsApp,
        IOptions<AppUrlOptions> appUrls, ILogger<CreateVendorHandler> logger)
    {
        _db = db; _email = email; _whatsApp = whatsApp; _appUrls = appUrls.Value; _logger = logger;
    }

    public async Task<Result<VendorDto>> Handle(CreateVendorCommand req, CancellationToken ct)
    {
        var mobile = req.Mobile.Trim();
        var email  = req.Email?.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Mobile == mobile, ct))
            return Result.Failure<VendorDto>(new Error("Vendor.DuplicateMobile", "An account already exists with this mobile number."));
        if (!string.IsNullOrEmpty(email) && await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Result.Failure<VendorDto>(new Error("Vendor.DuplicateEmail", "An account already exists with this email."));

        var requester = await _db.Users.AsNoTracking()
            .Where(u => u.Id == req.RequestingUserId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(ct);

        var vendor = new User(mobile, req.FullName, UserRole.Vendor);
        vendor.Activate();
        vendor.MarkAsDirectlyAdded(req.RequestingUserId);
        if (req.BusinessName is not null) vendor.BusinessName = req.BusinessName;
        if (email is not null)            vendor.Email        = email;

        _db.Users.Add(vendor);
        await _db.SaveChangesAsync(ct);

        await SendInviteAsync(vendor, requester ?? "An admin", ct);

        return Result.Success(MapToDto(vendor, 0));
    }

    private async Task SendInviteAsync(User vendor, string invitedByName, CancellationToken ct)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(_appUrls.BaseUrl)
            ? _appUrls.BaseUrl
            : (Environment.GetEnvironmentVariable("APP_BASE_URL") ?? "https://eventwos.app");
        var setupLink = $"{baseUrl.TrimEnd('/')}/setup-password?mobile={Uri.EscapeDataString(vendor.Mobile)}";

        if (!string.IsNullOrEmpty(vendor.Email))
        {
            try { await _email.SendAccountInviteEmailAsync(vendor.Email, vendor.FullName, "Vendor", invitedByName, setupLink, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Vendor invite email failed for {UserId}.", vendor.Id); }
        }

        try
        {
            var msg = $"Hi {vendor.FullName}, {invitedByName} added you to EventWOS as a Vendor. Set up your password: {setupLink}";
            await _whatsApp.SendAsync(vendor.Mobile, msg, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Vendor invite WhatsApp failed for {UserId}.", vendor.Id); }
    }

    internal static VendorDto MapToDto(
        User v, int crewCount,
        IReadOnlyList<EventWOS.Application.Files.DTOs.FileDocumentDto>? files = null,
        // Computed by the caller (see VendorParticipationLoader). Defaults to 0
        // for a vendor that was just created and cannot have delivered anything.
        int eventsCompleted = 0) => new(
        v.Id, v.Mobile, v.FullName, v.BusinessName, v.Email, v.AvatarUrl,
        v.Status.ToString(), v.ReferralCode, v.Rating, v.RatingCount, eventsCompleted, crewCount, v.CreatedAt,
        v.ContactPersonName, v.GstNumber, v.Address, v.City, v.State, v.Website, v.Bio, v.DateOfBirth,
        files ?? Array.Empty<EventWOS.Application.Files.DTOs.FileDocumentDto>(),
        v.InvitedByUserId.HasValue, v.ProfileCompletedAt.HasValue);
}
