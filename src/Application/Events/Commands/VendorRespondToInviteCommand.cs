using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Vendor accepts or rejects the Manager's invitation to staff an event.
/// Operates on the placeholder row (CrewId == null, Status == Invited).
/// Does NOT touch the Manager approval queue — this is a vendor↔manager
/// decision about the event itself, not about any specific crew member.
/// </summary>
public sealed record VendorRespondToInviteCommand(
    Guid    AssignmentId,
    Guid    VendorUserId,
    string  Response,    // "accept" | "reject"
    string? Reason = null
) : IRequest<Result>;

public sealed class VendorRespondToInviteHandler
    : IRequestHandler<VendorRespondToInviteCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    private readonly AppUrlOptions _appUrls;

    public VendorRespondToInviteHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications, IOptions<AppUrlOptions> appUrls)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
        _notifications = notifications;
        _appUrls = appUrls.Value;
    }

    public async Task<Result> Handle(VendorRespondToInviteCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Event)
            // Vendor loaded so the manager's message names who responded rather than
            // saying "a vendor" -- a manager staffing several vendors needs to know which.
            .Include(a => a.Vendor)
            .FirstOrDefaultAsync(a =>
                a.Id        == req.AssignmentId &&
                a.VendorId  == req.VendorUserId &&
                a.CrewId    == null, ct);

        if (assignment is null)
            return Result.Failure(new Error("Invitation.NotFound",
                "Invitation not found or not addressed to you."));

        bool accepted;
        try
        {
            if (req.Response.Equals("accept", StringComparison.OrdinalIgnoreCase))
            {
                assignment.VendorAcceptInvite();
                accepted = true;
            }
            else if (req.Response.Equals("reject", StringComparison.OrdinalIgnoreCase))
            {
                assignment.VendorRejectInvite(req.Reason ?? "No reason provided");
                accepted = false;
            }
            else
            {
                return Result.Failure(new Error("Invitation.InvalidResponse",
                    "Response must be 'accept' or 'reject'."));
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Invitation.InvalidTransition", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure(new Error("Invitation.InvalidInput", ex.Message));
        }

        // Durable copy for the manager who invited them, staged before the save.
        //
        // Same asymmetry as the crew responses: a REJECTION is urgent on every channel,
        // because that staffing is now unassigned and somebody has to find another
        // vendor before the event; an ACCEPTANCE is InApp only, since the queue showing
        // it is enough and a manager running a dozen vendors should not be emailed
        // twelve times to be told the plan is working.
        _notifications.Enqueue(new NotificationRequest(
            accepted
                ? NotificationTemplateCodes.VendorAcceptedEvent
                : NotificationTemplateCodes.VendorRejectedEvent,
            RecipientUserId: assignment.AssignedByUserId,
            // Response-specific: a rejected vendor can be re-invited on the same row and
            // may then accept, and those two messages must both survive de-duplication.
            BusinessEventKey: $"assignment:{assignment.Id}:vendor-{(accepted ? "accepted" : "rejected")}-event",
            Data: new Dictionary<string, string?>
            {
                // The manager's own name is not loaded here (AssignedByUserId is a bare
                // id on this row), and one extra query per response is not worth it for a
                // greeting -- the template opens with "there" rather than a wrong name.
                [NotificationTokens.RecipientName] = "there",
                [NotificationTokens.VendorName]    = assignment.Vendor?.FullName ?? "A vendor",
                [NotificationTokens.EventName]     = assignment.Event.Title,
                [NotificationTokens.EventDate]     = assignment.Event.StartAt.ToString("dd MMM yyyy"),
                // VendorRejectInvite already substitutes "No reason provided" into the
                // domain, so mirror that here instead of rendering an empty gap.
                [NotificationTokens.Reason]        = string.IsNullOrWhiteSpace(req.Reason)
                    ? "no reason given"
                    : req.Reason,
                [NotificationTokens.Link]          = _appUrls.BaseUrl.TrimEnd('/') + "/approvals/events"
            },
            ActorUserId: req.VendorUserId,
            Channels: accepted ? new[] { NotificationChannel.InApp } : null));

        await _uow.SaveChangesAsync(ct);

        // Notify the manager who originally assigned the vendor to the event.
        var notif = accepted ? "VendorAcceptedEvent" : "VendorRejectedEvent";
        await _push.PushToUserAsync(assignment.AssignedByUserId, notif, new
        {
            assignmentId = assignment.Id,
            eventId      = assignment.EventId,
            eventTitle   = assignment.Event.Title,
            reason       = req.Reason
        }, ct);

        return Result.Success();
    }
}
