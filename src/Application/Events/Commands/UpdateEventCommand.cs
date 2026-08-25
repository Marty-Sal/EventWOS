using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Domain.Rules;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Events.Commands;

public sealed record UpdateEventCommand(
    Guid     Id,
    string   Title,
    string?  Description,
    string   Venue,
    string?  Address,
    DateTime StartAt,
    DateTime EndAt,
    int      MaxCrew,
    // Optional: pick a catalog Venue instead of typing Venue/Address by hand.
    // When set, the handler overwrites Venue/Address with the venue's
    // name/address so the two always agree with what was actually picked.
    Guid?    VenueId = null
) : IRequest<Result>;

public sealed class UpdateEventHandler : IRequestHandler<UpdateEventCommand, Result>
{
    private readonly IAppDbContext          _db;
    private readonly IUnitOfWork            _uow;
    private readonly INotificationDispatcher _notifications;

    public UpdateEventHandler(IAppDbContext db, IUnitOfWork uow, INotificationDispatcher notifications)
    {
        _db            = db;
        _uow           = uow;
        _notifications = notifications;
    }

    public async Task<Result> Handle(UpdateEventCommand req, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.Id, ct);
        if (ev is null) return Result.Failure(new Error("Event.NotFound", "Event not found."));

        // Snapshot the details staff actually travel on, BEFORE the entity mutates.
        // Description and MaxCrew are deliberately not here: nobody needs a message
        // because an admin fixed a typo in the blurb or changed a seat count they
        // never see.
        var wasTitle   = ev.Title;
        var wasVenue   = ev.Venue;
        var wasAddress = ev.Address;
        var wasStartAt = ev.StartAt;
        var wasEndAt   = ev.EndAt;

        // Resolve venue -> display copy (see CreateEventHandler for the
        // same pattern and rationale).
        var venueName = req.Venue;
        var venueAddr = req.Address;
        if (req.VenueId is not null)
        {
            var venue = await _db.Venues.FirstOrDefaultAsync(v => v.Id == req.VenueId, ct);
            if (venue is null)
                return Result.Failure(new Error("Event.InvalidVenue", "Selected venue not found or archived."));
            venueName = venue.Name;
            venueAddr = $"{venue.AddressLine1}, {venue.City}".Trim(' ', ',');
        }

        // Count seat-occupiers BEFORE calling Update so the entity can enforce
        // its MaxCrew floor (you cannot shrink the cap below already-approved
        // staff — see Event.Update for the full rationale).
        //
        // Uses the canonical AssignmentCapacityRules.OccupiesSeat predicate so
        // this counts EXACTLY the same set as AssignCrewCommand / VendorAssignCrewCommand
        // / GetEventByIdQuery.AssignedCrew. One source of truth.
        var currentSeats = await _db.EventAssignments
            .Where(a => a.EventId == req.Id)
            .Where(AssignmentCapacityRules.OccupiesSeat)
            .CountAsync(ct);

        try
        {
            ev.Update(req.Title, req.Description, venueName, venueAddr,
                      req.StartAt, req.EndAt, req.MaxCrew, currentSeats, req.VenueId);
        }
        catch (InvalidOperationException ex)
        {
            // Distinguish the two failure modes so the API can pick the right
            // status code + the UI can render the right copy.
            var code = ex.Message.Contains("Completed", StringComparison.OrdinalIgnoreCase)
                       || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)
                ? "Event.NotEditable"
                : "Event.MaxCrewBelowApproved";
            return Result.Failure(new Error(code, ex.Message));
        }

        // EVENT_UPDATED had a template, a policy entry and a deep link, and nothing
        // in the system ever raised it -- a venue or time change reached nobody, so
        // crew travelled to yesterday's address. This is that trigger.
        var changes = new List<string>();
        if (ev.Title   != wasTitle)                        changes.Add("title");
        if (ev.Venue   != wasVenue)                        changes.Add("venue");
        if ((ev.Address ?? "") != (wasAddress ?? ""))       changes.Add("address");
        if (ev.StartAt != wasStartAt)                      changes.Add("start");
        if (ev.EndAt   != wasEndAt)                        changes.Add("end");

        if (changes.Count > 0)
        {
            // Fan-out, not a loop: a 500-crew event must not resolve 500 recipients
            // inside an admin's save. Enqueued BEFORE SaveChanges so the event row
            // and its outbox row commit together -- a rollback tells nobody about a
            // change that did not happen.
            _notifications.EnqueueFanOut(new NotificationFanOutRequest(
                NotificationTemplateCodes.EventUpdated,
                NotificationAudience.EventCrewAndVendors,
                EventId: ev.Id,
                // Keyed on WHAT it now says, not on when it was said: pressing Save
                // twice on the same form is one piece of news and collapses, while a
                // genuinely different edit is a different key and gets through. The
                // minute stamp is the escape hatch for an edit that reverts to an
                // earlier state -- without it, A -> B -> A -> B would silently drop
                // the second move to B for having "already been sent".
                BusinessEventKey: $"event:{ev.Id}:updated:" +
                                  $"{DescribeState(ev)}:{DateTime.UtcNow:yyyyMMddHHmm}",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.EventName] = ev.Title,
                    // The seeded body is "New details: {{EventDate}} at {{VenueName}}",
                    // so the whole new window has to live in EventDate or a time-only
                    // change would produce a message that reads identically to before.
                    [NotificationTokens.EventDate] = $"{ev.StartAt:dd MMM yyyy, HH:mm} - {ev.EndAt:HH:mm}",
                    [NotificationTokens.EventTime] = ev.StartAt.ToString("HH:mm"),
                    [NotificationTokens.VenueName] = string.IsNullOrWhiteSpace(ev.Address)
                        ? ev.Venue
                        : $"{ev.Venue}, {ev.Address}"
                }));
        }

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Short stable fingerprint of the details staff travel on. Same details ->
    /// same key -> one message however many times Save is pressed.
    /// </summary>
    private static string DescribeState(Domain.Entities.Event ev)
    {
        var material = $"{ev.Title}|{ev.Venue}|{ev.Address}|{ev.StartAt:O}|{ev.EndAt:O}";
        var bytes    = System.Security.Cryptography.SHA256.HashData(
                           System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(bytes)[..10].ToLowerInvariant();
    }
}
