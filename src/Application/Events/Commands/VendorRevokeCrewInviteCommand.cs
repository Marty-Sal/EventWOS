using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Vendor revokes a pending crew invitation they sent. Soft-deletes the
/// assignment row so the crew stops seeing it in their My Assignments page,
/// and frees the slot for someone else. Mirrors the manager's
/// RevokeVendorInviteCommand pattern — only valid while Status == Invited.
/// </summary>
public sealed record VendorRevokeCrewInviteCommand(
    Guid EventId,
    Guid CrewId,
    Guid VendorUserId
) : IRequest<Result>;

public sealed class VendorRevokeCrewInviteHandler
    : IRequestHandler<VendorRevokeCrewInviteCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    private readonly AppUrlOptions _appUrls;

    public VendorRevokeCrewInviteHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications, IOptions<AppUrlOptions> appUrls)
    {
        _db = db; _uow = uow; _push = push;
        _notifications = notifications; _appUrls = appUrls.Value;
    }

    public async Task<Result> Handle(VendorRevokeCrewInviteCommand req, CancellationToken ct)
    {
        // Find the active (non-deleted) invite row for this (event, crew)
        // pair owned by the acting vendor.
        var a = await _db.EventAssignments
            .Include(x => x.Event)
            // Crew loaded for the greeting on the message telling them not to come.
            .Include(x => x.Crew)
            .FirstOrDefaultAsync(x => x.EventId == req.EventId
                                   && x.CrewId  == req.CrewId
                                   && x.VendorId == req.VendorUserId, ct);
        if (a is null)
            return Result.Failure(new Error("Invitation.NotFound", "Invitation not found."));

        if (a.Status != AssignmentStatus.Invited)
            return Result.Failure(new Error("Invitation.AlreadyResponded",
                "The crew has already responded — you can't revoke a non-pending invite."));

        try
        {
            a.VendorRevokeCrewInvite(req.VendorUserId);
        }
        catch (System.InvalidOperationException ex)
        {
            return Result.Failure(new Error("Invitation.NotRevokable", ex.Message));
        }

        // Full channel set, and of everything in this batch this is the message that
        // most has to land: a crew member holding an invitation will otherwise travel
        // to a venue for a shift that no longer exists.
        _notifications.Enqueue(new NotificationRequest(
            NotificationTemplateCodes.CrewInviteRevoked,
            RecipientUserId: req.CrewId,
            // Safe as a per-row key because revoking soft-deletes this row and a later
            // re-invite inserts a FRESH row with a new id -- so a second revoke can
            // never collide with this key and get de-duplicated into silence.
            BusinessEventKey: $"assignment:{a.Id}:crew-invite-revoked",
            Data: new Dictionary<string, string?>
            {
                [NotificationTokens.RecipientName] = a.Crew?.FullName ?? "there",
                [NotificationTokens.EventName]     = a.Event.Title,
                [NotificationTokens.EventDate]     = a.Event.StartAt.ToString("dd MMM yyyy")
            },
            ActorUserId: req.VendorUserId));

        await _uow.SaveChangesAsync(ct);

        await _push.PushToUserAsync(req.CrewId, "CrewInviteRevoked", new
        {
            assignmentId = a.Id,
            eventId      = a.EventId,
            eventTitle   = a.Event.Title
        }, ct);

        return Result.Success();
    }
}
