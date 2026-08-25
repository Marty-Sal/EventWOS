using EventOpsOracle.Application.Events.DTOs;
using EventOpsOracle.Application.Common;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Application.Events.Shifts;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Application.Events.Commands;

/// <summary>
/// Create an event.
///
/// Phase B contract: callers should now pass a non-empty <see cref="Shifts"/>
/// list — each entry describes one staffing slot (Box Office, Gates, etc.).
/// During the rollout the field is OPTIONAL: if the caller passes a non-empty
/// list, those shifts get created and MaxCrew is computed from their sum.
/// If they pass an empty/null list (legacy callers, tests), we fall back to
/// the old behaviour: persist with the supplied <see cref="MaxCrew"/> and
/// auto-create a single "General" shift using the seeded General scope row,
/// so the resulting event still satisfies the Phase B invariant ("every
/// event has at least one shift").
///
/// This dual-path is a deliberate, temporary scaffold. The day every caller
/// is updated, we collapse to "Shifts is required, MaxCrew goes away".
/// </summary>
public sealed record CreateEventShiftDto(
    Guid     ScopeOfWorkId,
    int      CrewCount,
    DateTime StartAt,
    DateTime? EndAt,
    // Optional: assign a vendor to this shift at creation time instead of
    // doing it as a separate step afterwards (Vendor Quotas). When set, the
    // ENTIRE shift capacity (CrewCount) is granted to this one vendor via a
    // VendorShiftAllocation created alongside the shift in the same
    // transaction. Leave null to skip — the shift is created unassigned and
    // vendors can still be allocated later (one or split across several).
    Guid?    VendorId = null
);

public sealed record CreateEventCommand(
    string   Title,
    string?  Description,
    string   Venue,
    string?  Address,
    DateTime StartAt,
    DateTime EndAt,
    int      MaxCrew,
    Guid     CreatedByUserId,
    IReadOnlyList<CreateEventShiftDto>? Shifts = null,
    // Optional: pick a catalog Venue (Settings -> Venue) instead of typing
    // Venue/Address by hand. When set, the handler overwrites Venue/Address
    // with the venue's name/address so the two always agree with what was
    // actually picked -- the venue catalog is the source of truth for the
    // full address details (incl. lat/lng); the event just carries the
    // display copy plus this VenueId back-reference.
    Guid?    VenueId = null,

    // ── Attendance geofence configuration ───────────────────────────────────
    // Set when the admin ticks "Location / GPS" under Attendance Verification.
    // The radius belongs to the EVENT (not the Venue) so two events at the
    // same venue can enforce different boundaries. The handler validates it
    // against the venue's coordinates and clamps it via Event.EnableGeoFence —
    // a client-supplied radius is never taken on trust beyond that.
    bool     GeoFenceEnabled      = false,
    int?     GeoFenceRadiusMeters = null
) : IRequest<Result<EventDto>>;

public sealed class CreateEventHandler : IRequestHandler<CreateEventCommand, Result<EventDto>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    /// <summary>
    /// Applied when the admin enables the fence but sends no radius. 150 m is a
    /// deliberate middle ground: comfortably outside GPS jitter, tight enough
    /// that "at the venue" still means it.
    /// </summary>
    private const int DefaultGeoFenceRadiusMeters = 150;

    private readonly INotificationDispatcher _notifications;
    private readonly AppUrlOptions _appUrls;

    public CreateEventHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications, IOptions<AppUrlOptions> appUrls)
    {
        _db = db; _uow = uow; _push = push;
        _notifications = notifications; _appUrls = appUrls.Value;
    }

    public async Task<Result<EventDto>> Handle(CreateEventCommand req, CancellationToken ct)
    {
        // ── Resolve shifts payload ──────────────────────────────────────────
        // Two code paths converge on the same end state: an Event with >= 1
        // EventShift attached. The Phase-B-aware path validates the supplied
        // shifts; the legacy path synthesises a single "General" shift.
        // Resolve venue -> display copy. A selected venue's Name/Address wins
        // over whatever free text was passed, so the event's location text
        // always matches the catalog entry it was picked from.
        var venueName = req.Venue;
        var venueAddr = req.Address;

        // Whether the picked venue actually has coordinates. Captured here
        // while the venue is already loaded so the geofence step below doesn't
        // need a second round trip.
        var venueHasCoordinates = false;

        if (req.VenueId is not null)
        {
            var venue = await _db.Venues.FirstOrDefaultAsync(v => v.Id == req.VenueId, ct);
            if (venue is null)
                return Result.Failure<EventDto>(new Error("Event.InvalidVenue", "Selected venue not found or archived."));
            venueName = venue.Name;
            venueAddr = $"{venue.AddressLine1}, {venue.City}".Trim(' ', ',');
            venueHasCoordinates = venue.Latitude is not null && venue.Longitude is not null;
        }

        // Geofencing requires a catalog venue — there is nothing to measure
        // from otherwise. Event creation deliberately cannot mint a venue
        // implicitly: venues are centrally managed master data, and a
        // half-specified ad-hoc venue would produce an unenforceable fence.
        if (req.GeoFenceEnabled && req.VenueId is null)
            return Result.Failure<EventDto>(new Error(
                "Event.GeoFenceNeedsVenue",
                "Select a saved venue before enabling location verification — the geofence is measured from the venue's coordinates."));

        var providedShifts = req.Shifts ?? Array.Empty<CreateEventShiftDto>();

        // Effective MaxCrew = sum of shift crew counts when shifts are
        // provided, otherwise the legacy value passed in MaxCrew. This is
        // what we persist into events.max_crew so legacy queries still see
        // the right number while we migrate them off the column.
        int effectiveMaxCrew = providedShifts.Count > 0
            ? providedShifts.Sum(s => s.CrewCount)
            : Math.Max(req.MaxCrew, 1);

        var ev = new Event(
            req.Title, req.Description, venueName, venueAddr,
            req.StartAt, req.EndAt, req.CreatedByUserId,
            maxCrew: effectiveMaxCrew, venueId: req.VenueId);

        // Arm the attendance geofence. The domain enforces the venue-has-
        // coordinates invariant and the 20 m..5 km clamp, so an out-of-range
        // radius from any client surfaces as a clean validation failure rather
        // than a silently mis-sized fence.
        if (req.GeoFenceEnabled)
        {
            try
            {
                ev.EnableGeoFence(
                    req.GeoFenceRadiusMeters ?? DefaultGeoFenceRadiusMeters,
                    venueHasCoordinates);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
            {
                return Result.Failure<EventDto>(new Error("Event.GeoFenceInvalid", ex.Message));
            }
        }

        _db.Events.Add(ev);

        // Vendors that got a shift handed to them at create time. Collected
        // during the shift loop and pushed AFTER SaveChangesAsync, so we
        // never notify anyone about an event that failed to persist.
        var vendorInvites = new List<(Guid VendorId, Guid AssignmentId)>();

        // ── Build the shift rows ────────────────────────────────────────────
        if (providedShifts.Count > 0)
        {
            // Validate scope-of-work IDs in one round trip rather than N.
            // Archived rows are excluded by the global query filter, so a
            // shift referencing an archived scope will return NotFound here.
            var scopeIds = providedShifts.Select(s => s.ScopeOfWorkId).Distinct().ToList();
            var valid    = await _db.ScopesOfWork
                .Where(s => scopeIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct);

            var missing = scopeIds.Except(valid).ToList();
            if (missing.Count > 0)
                return Result.Failure<EventDto>(new Error(
                    "Event.InvalidScope",
                    $"Scope-of-work not found or archived: {string.Join(", ", missing)}."));

            // Pre-validate every requested vendor in one round trip (rather
            // than N queries inside the loop) — same shape as the checks in
            // CreateVendorAllocationHandler, just batched since several
            // shifts can name a vendor in the same create-event request.
            var vendorIds = providedShifts
                .Where(s => s.VendorId.HasValue)
                .Select(s => s.VendorId!.Value)
                .Distinct()
                .ToList();
            Dictionary<Guid, Domain.Entities.User> vendorsById = new();
            if (vendorIds.Count > 0)
            {
                vendorsById = await _db.Users
                    .Where(u => vendorIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, ct);

                var missingVendors = vendorIds.Except(vendorsById.Keys).ToList();
                if (missingVendors.Count > 0)
                    return Result.Failure<EventDto>(new Error(
                        "Event.InvalidShiftVendor",
                        $"Vendor not found: {string.Join(", ", missingVendors)}."));

                var nonVendors = vendorsById.Values.Where(u => u.Role != UserRole.Vendor).ToList();
                if (nonVendors.Count > 0)
                    return Result.Failure<EventDto>(new Error(
                        "Event.InvalidShiftVendor",
                        $"User is not a Vendor: {string.Join(", ", nonVendors.Select(u => u.FullName ?? u.Id.ToString()))}."));
            }

            foreach (var sh in providedShifts)
            {
                // Phase D step 2: per-shift bounds-check against the event
                // window. UI defaults shift.StartAt to event.StartAt, but
                // we re-check here to catch malicious or stale clients.
                var boundsCheck = ShiftTimeBounds.Validate(ev, sh.StartAt, sh.EndAt);
                if (boundsCheck.IsFailure)
                    return Result.Failure<EventDto>(boundsCheck.Error);

                EventShift shift;
                try
                {
                    shift = new EventShift(
                        ev.Id, sh.ScopeOfWorkId, sh.CrewCount,
                        sh.StartAt, sh.EndAt, req.CreatedByUserId);
                    _db.EventShifts.Add(shift);
                }
                catch (ArgumentException ex)
                {
                    return Result.Failure<EventDto>(new Error("Event.InvalidShift", ex.Message));
                }

                // Vendor assigned at shift-creation time: grant them the
                // WHOLE shift (Quota == CrewCount) via the same
                // VendorShiftAllocation model the post-creation "Vendor
                // Quotas" flow uses. shift.Id is already populated (client-
                // generated GUID — see BaseEntity), so this can happen
                // before SaveChangesAsync without a round trip.
                if (sh.VendorId is { } vendorId)
                {
                    try
                    {
                        var allocation = new VendorShiftAllocation(
                            shift.Id, vendorId, sh.CrewCount, req.CreatedByUserId);
                        _db.VendorShiftAllocations.Add(allocation);
                    }
                    catch (ArgumentException ex)
                    {
                        return Result.Failure<EventDto>(new Error("Event.InvalidShiftVendorAllocation", ex.Message));
                    }

                    // The quota alone is invisible to the vendor: My Events,
                    // their approval queue and the push pipeline all read
                    // EventAssignments, never VendorShiftAllocations. So the
                    // allocation on its own left the vendor with seats they
                    // never knew about. Mirror what AssignCrewHandler does in
                    // vendor-only mode and drop the placeholder invite anchor
                    // (CrewId == null, Status == Invited) on the same shift, so
                    // "Assign vendor" at create time really does assign them.
                    //
                    // One placeholder per (shift, vendor) — same shape the
                    // admin would have produced by clicking "Assign to Event"
                    // on each shift afterwards. The vendor accepts it, then
                    // staffs crew up to their quota.
                    var placeholder = new EventAssignment(
                        ev.Id, shift.Id, crewId: null, vendorId: vendorId, req.CreatedByUserId);
                    _db.EventAssignments.Add(placeholder);
                    vendorInvites.Add((vendorId, placeholder.Id));
                }
            }
        }
        else
        {
            // Legacy path — synthesise one "General" shift mirroring the
            // event's own (start_at, end_at, max_crew). Same shape as the
            // backfill SQL so old and new code converge on identical data.
            var general = await _db.ScopesOfWork
                .Where(s => s.Name.ToLower() == "general")
                .Select(s => s.Id)
                .FirstOrDefaultAsync(ct);

            if (general == Guid.Empty)
            {
                // Seeder hasn't run yet (extremely fresh DB). Surface a
                // clean error rather than a NULL FK explosion.
                return Result.Failure<EventDto>(new Error(
                    "Event.MissingDefaultScope",
                    "Cannot create event without shifts on a fresh DB — " +
                    "the default 'General' scope-of-work row hasn't been seeded yet."));
            }

            _db.EventShifts.Add(new EventShift(
                ev.Id, general, effectiveMaxCrew,
                req.StartAt, req.EndAt, req.CreatedByUserId));
        }

        // Durable invitation for every vendor picked on a shift, staged before the save
        // so the event, the placeholder rows and the messages commit as one.
        //
        // Deliberately ONE message per vendor, not one per placeholder. A vendor picked
        // on four shifts of the same event gets four placeholder rows, but the
        // VENDOR_EVENT_INVITED template says only "you have been invited to {{EventName}}
        // on {{EventDate}} at {{VenueName}}" -- it carries nothing shift-specific, so
        // four copies would be four word-for-word repeats. Their My Events page and the
        // quota panel show the per-shift detail.
        if (vendorInvites.Count > 0)
        {
            var vendorIds = vendorInvites.Select(v => v.VendorId).Distinct().ToList();
            var vendorNames = await _db.Users
                .Where(u => vendorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName })
                .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

            var link = _appUrls.BaseUrl.TrimEnd('/') + "/vendor-assignments";

            _notifications.Enqueue(vendorIds.Select(vendorId => new NotificationRequest(
                NotificationTemplateCodes.VendorEventInvited,
                RecipientUserId: vendorId,
                // Keyed on (event, vendor) rather than on the placeholder row, which is
                // what collapses the multi-shift case above. Idempotency is permanent
                // here, so a vendor is told about THIS event exactly once -- and if the
                // invitation is later revoked and re-issued, ReinviteVendor uses a
                // timestamped key of its own, so recovery still reaches them.
                BusinessEventKey: $"event:{ev.Id}:vendor-invited:{vendorId}",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = vendorNames.TryGetValue(vendorId, out var n) ? n : "there",
                    [NotificationTokens.EventName]     = ev.Title,
                    [NotificationTokens.EventDate]     = ev.StartAt.ToString("dd MMM yyyy"),
                    [NotificationTokens.EventTime]     = ev.StartAt.ToString("HH:mm"),
                    [NotificationTokens.VenueName]     = ev.Venue,
                    [NotificationTokens.Link]          = link
                },
                ActorUserId: req.CreatedByUserId)));
        }

        await _uow.SaveChangesAsync(ct);

        // Same notification AssignCrewHandler sends in vendor-only mode, so a
        // vendor staffed straight from the create-event form hears about it
        // through the identical channel as one added afterwards.
        foreach (var (vendorId, assignmentId) in vendorInvites)
        {
            await _push.PushToUserAsync(vendorId, "VendorEventAssigned", new
            {
                assignmentId = assignmentId,
                eventTitle   = ev.Title,
                eventStart   = ev.StartAt
            }, ct);
        }

        var creator = await _db.Users.FindAsync(new object[] { req.CreatedByUserId }, ct);
        return Result.Success(MapToDto(ev, 0, creator?.FullName ?? "Unknown", 0));
    }

    // Phase D step 21: optional confirmedCrew param so callers that don't
    // care (Create / Update — fresh event has 0 confirmed) can stay
    // unchanged, while GetEventByIdQuery can pass the real number.
    internal static EventDto MapToDto(
        Domain.Entities.Event ev, int assignedCrew, string creatorName, int confirmedCrew = 0) => new(
        ev.Id, ev.Title, ev.Description, ev.Venue, ev.Address,
        ev.StartAt, ev.EndAt, ev.Status.ToString(), ev.MaxCrew,
        assignedCrew, ev.CreatedByUserId, creatorName, ev.CreatedAt, confirmedCrew, ev.VenueId);
}
