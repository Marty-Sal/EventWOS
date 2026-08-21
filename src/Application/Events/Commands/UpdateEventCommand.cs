using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Rules;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

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
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public UpdateEventHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(UpdateEventCommand req, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.Id, ct);
        if (ev is null) return Result.Failure(new Error("Event.NotFound", "Event not found."));

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

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
