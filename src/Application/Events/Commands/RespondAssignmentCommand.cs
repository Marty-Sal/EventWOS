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

/// <summary>Crew confirms or declines their assignment invitation.</summary>
public sealed record RespondAssignmentCommand(
    Guid    AssignmentId,
    Guid    CrewId,
    string  Response,    // "confirm" | "decline"
    string? Reason = null
) : IRequest<Result>;

public sealed class RespondAssignmentHandler : IRequestHandler<RespondAssignmentCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    private readonly AppUrlOptions _appUrls;

    public RespondAssignmentHandler(
        IAppDbContext db,
        IUnitOfWork uow,
        INotificationPusher push,
        INotificationDispatcher notifications,
        IOptions<AppUrlOptions> appUrls)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
        _notifications = notifications;
        _appUrls = appUrls.Value;
    }

    public async Task<Result> Handle(RespondAssignmentCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            // Vendor and Event come along for the notification: a vendor needs the
            // event name to know which roster just changed, and their own name reads
            // better than "Hi there" on the one message that costs them a replacement.
            .Include(a => a.Vendor)
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && a.CrewId == req.CrewId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        bool accepted;
        try
        {
            if (req.Response.Equals("confirm", StringComparison.OrdinalIgnoreCase))
            {
                assignment.CrewAccept();
                accepted = true;
            }
            else if (req.Response.Equals("decline", StringComparison.OrdinalIgnoreCase))
            {
                assignment.CrewDecline(req.Reason);
                accepted = false;
            }
            else
                return Result.Failure(new Error("Assignment.InvalidResponse", "Response must be 'confirm' or 'decline'."));
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message));
        }

        // Durable copy for the vendor, staged before the save so the response and the
        // notification commit together.
        //
        // Deliberately asymmetric on channels. A DECLINE is urgent and gets every
        // channel the recipient allows: a slot the vendor had counted as filled is now
        // empty, and if that only ever appeared as a toast they missed, the gap is
        // discovered at the venue. An ACCEPT is InApp only -- it is still an action
        // item (the row waits for the vendor to forward it to the manager) but a vendor
        // staffing fifty crew would otherwise get fifty emails saying things are going
        // to plan.
        if (assignment.VendorId.HasValue)
        {
            var link = _appUrls.BaseUrl.TrimEnd('/') + "/vendor-assignments";

            _notifications.Enqueue(new NotificationRequest(
                accepted
                    ? NotificationTemplateCodes.CrewAcceptedAssignment
                    : NotificationTemplateCodes.CrewDeclinedAssignment,
                RecipientUserId: assignment.VendorId.Value,
                // Response-specific key: a crew member who declines, is re-invited and
                // then accepts must produce two distinct messages, not one swallowed by
                // de-duplication of the other.
                BusinessEventKey: $"assignment:{assignment.Id}:crew-{(accepted ? "accepted" : "declined")}",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = assignment.Vendor?.FullName ?? "there",
                    [NotificationTokens.CrewName]      = assignment.Crew?.FullName ?? "A crew member",
                    [NotificationTokens.EventName]     = assignment.Event?.Title ?? "the event",
                    [NotificationTokens.EventDate]     = assignment.Event?.StartAt.ToString("dd MMM yyyy") ?? "-",
                    // Decline reason is optional in the domain, so say "no reason given"
                    // rather than rendering an empty gap in the sentence.
                    [NotificationTokens.Reason]        = string.IsNullOrWhiteSpace(req.Reason)
                        ? "no reason given"
                        : req.Reason,
                    [NotificationTokens.Link]          = link
                },
                ActorUserId: req.CrewId,
                Channels: accepted
                    ? new[] { NotificationChannel.InApp }
                    : null));
        }

        await _uow.SaveChangesAsync(ct);

        // Notify vendor of crew response (skip for direct-assigned crew — no vendor in the loop)
        if (assignment.VendorId.HasValue)
        {
            var notifEvent = accepted ? "CrewAccepted" : "CrewDeclined";
            await _push.PushToUserAsync(assignment.VendorId.Value, notifEvent, new
            {
                assignmentId = assignment.Id,
                crewName     = assignment.Crew?.FullName ?? "Crew member",
                reason       = req.Reason
            }, ct);
        }

        return Result.Success();
    }
}
