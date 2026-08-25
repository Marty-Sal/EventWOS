using EventOpsOracle.Application.Common;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Vendors.DTOs;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Application.Crew.Commands;

public sealed record CreateCrewCommand(
    string Mobile, string FullName, string? Email, string? ReferralCode, Guid RequestingUserId
) : IRequest<Result<CrewDto>>;

/// <summary>
/// Admin/Manager/Vendor directly adds a Crew member. Skips self-registration
/// and the approval queue entirely — the account is Active immediately (an
/// authorized party already vouched for it). Instead we force the
/// first-login password-setup flow (same one grandfathered users go
/// through) and notify the new crew member by email + WhatsApp with a link
/// to set their password and fill in their profile. See
/// User.MarkAsDirectlyAdded / UpdateProfileHandler for the "notify the
/// inviter once the profile is filled in" side of this loop.
/// </summary>
public sealed class CreateCrewHandler : IRequestHandler<CreateCrewCommand, Result<CrewDto>>
{
    private readonly IAppDbContext _db;
    private readonly IEmailService _email;
    private readonly IWhatsAppProvider _whatsApp;
    private readonly AppUrlOptions _appUrls;
    private readonly ILogger<CreateCrewHandler> _logger;

    public CreateCrewHandler(
        IAppDbContext db, IEmailService email, IWhatsAppProvider whatsApp,
        IOptions<AppUrlOptions> appUrls, ILogger<CreateCrewHandler> logger)
    {
        _db = db; _email = email; _whatsApp = whatsApp; _appUrls = appUrls.Value; _logger = logger;
    }

    public async Task<Result<CrewDto>> Handle(CreateCrewCommand req, CancellationToken ct)
    {
        var mobile = req.Mobile.Trim();
        var email  = req.Email?.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Mobile == mobile, ct))
            return Result.Failure<CrewDto>(new Error("Crew.DuplicateMobile", "An account already exists with this mobile number."));
        if (!string.IsNullOrEmpty(email) && await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Result.Failure<CrewDto>(new Error("Crew.DuplicateEmail", "An account already exists with this email."));

        // A vendor is mandatory for every crew member created through this
        // endpoint (Vendor self-service always sends their own code; the
        // Admin/Manager "Add Crew" flow must select one from a dropdown of
        // active vendors — there is no such thing as an unassigned crew
        // member created this way). Crew self-registration is a separate
        // command (RegisterCrewCommand) and is unaffected by this rule.
        if (string.IsNullOrWhiteSpace(req.ReferralCode))
            return Result.Failure<CrewDto>(new Error("Crew.VendorRequired", "A vendor must be selected for this crew member."));

        var vendor = await _db.Users.FirstOrDefaultAsync(
            u => u.ReferralCode == req.ReferralCode && u.Role == UserRole.Vendor && !u.IsDeleted, ct);
        if (vendor is null)
            return Result.Failure<CrewDto>(new Error("Crew.InvalidReferral", "Invalid referral code."));

        var requester = await _db.Users.AsNoTracking()
            .Where(u => u.Id == req.RequestingUserId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(ct);

        var crew = new User(mobile, req.FullName, UserRole.Crew);
        crew.Activate();
        crew.MarkAsDirectlyAdded(req.RequestingUserId);
        if (email is not null) crew.Email = email;
        crew.JoinVendor(vendor.Id);

        _db.Users.Add(crew);
        await _db.SaveChangesAsync(ct);

        await SendInviteAsync(crew, requester ?? vendor.FullName, ct);

        return Result.Success(MapToDto(crew, vendor.FullName));
    }

    private async Task SendInviteAsync(User crew, string invitedByName, CancellationToken ct)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(_appUrls.BaseUrl)
            ? _appUrls.BaseUrl
            : (Environment.GetEnvironmentVariable("APP_BASE_URL") ?? "https://eventwos.app");
        var setupLink = $"{baseUrl.TrimEnd('/')}/setup-password?mobile={Uri.EscapeDataString(crew.Mobile)}";

        if (!string.IsNullOrEmpty(crew.Email))
        {
            try { await _email.SendAccountInviteEmailAsync(crew.Email, crew.FullName, "Crew", invitedByName, setupLink, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Crew invite email failed for {UserId}.", crew.Id); }
        }

        try
        {
            var msg = $"Hi {crew.FullName}, {invitedByName} added you to OpsOracle as Crew. Set up your password: {setupLink}";
            await _whatsApp.SendAsync(crew.Mobile, msg, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Crew invite WhatsApp failed for {UserId}.", crew.Id); }
    }

    internal static CrewDto MapToDto(User c, string? vendorName) => new(
        c.Id, c.Mobile, c.FullName, c.Email, c.AvatarUrl,
        c.Status.ToString(), c.VendorId, vendorName,
        c.DisciplineScore, c.EventsAttended, c.CreatedAt,
        c.CrewRating, c.CrewRatingCount);
}
