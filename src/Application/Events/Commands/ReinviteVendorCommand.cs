using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Manager re-invites a vendor whose invitation was previously rejected.
/// Flips the placeholder row from RejectedByVendor back to Invited and
/// clears rejection state. Notifies the vendor of the new invitation.
/// </summary>
public sealed record ReinviteVendorCommand(
    Guid AssignmentId,
    Guid ReinvitedByUserId
) : IRequest<Result>;

public sealed class ReinviteVendorHandler
    : IRequestHandler<ReinviteVendorCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    private readonly AppUrlOptions _appUrls;

    public ReinviteVendorHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications, IOptions<AppUrlOptions> appUrls)
    {
        _db = db; _uow = uow; _push = push;
        _notifications = notifications; _appUrls = appUrls.Value;
    }

    public async Task<Result> Handle(ReinviteVendorCommand req, CancellationToken ct)
    {
        var a = await _db.EventAssignments
            .Include(x => x.Event)
            .Include(x => x.Vendor)
            .FirstOrDefaultAsync(x => x.Id == req.AssignmentId, ct);
        if (a is null)
            return Result.Failure(new Error("Invitation.NotFound", "Invitation not found."));

        try { a.ManagerReinviteVendor(); }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Invitation.InvalidTransition", ex.Message));
        }

        var reinvitedAt = DateTime.UtcNow;
        a.UpdatedAt = reinvitedAt;
        a.UpdatedBy = req.ReinvitedByUserId;

        if (a.VendorId.HasValue)
        {
            // A fresh invitation, on every channel: it is an action item with a deadline
            // (the event), and the vendor previously turned this same event down, so
            // there is no reason to assume they are watching the page for it.
            _notifications.Enqueue(new NotificationRequest(
                NotificationTemplateCodes.VendorEventInvited,
                RecipientUserId: a.VendorId.Value,
                // Timestamped, unlike the revoke keys. ManagerReinviteVendor resurrects
                // the SAME row, so a reject/re-invite/reject/re-invite cycle reuses one
                // assignment id -- a static key would let the platform treat the second
                // invitation as a duplicate of the first and never deliver it.
                //
                // Ticks rather than milliseconds: two re-invites inside the same tick can
                // only be a double-submit of one manager action, and collapsing that into
                // a single invitation is the behaviour we want anyway.
                BusinessEventKey: $"assignment:{a.Id}:vendor-reinvited:{reinvitedAt.Ticks}",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = a.Vendor?.FullName ?? "there",
                    [NotificationTokens.EventName]     = a.Event.Title,
                    [NotificationTokens.EventDate]     = a.Event.StartAt.ToString("dd MMM yyyy"),
                    [NotificationTokens.VenueName]     = a.Event.Venue,
                    [NotificationTokens.Link]          = _appUrls.BaseUrl.TrimEnd('/') + "/vendor-assignments"
                },
                ActorUserId: req.ReinvitedByUserId));
        }

        await _uow.SaveChangesAsync(ct);

        if (a.VendorId.HasValue)
        {
            await _push.PushToUserAsync(a.VendorId.Value, "VendorReinvited", new
            {
                assignmentId = a.Id,
                eventId      = a.EventId,
                eventTitle   = a.Event.Title,
                eventStart   = a.Event.StartAt
            }, ct);
        }

        return Result.Success();
    }
}
