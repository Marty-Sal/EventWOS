using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// A short-lived request to check in that MUST be verified by a vendor scan
/// before it commits to the attendance ledger. Every row is one attempt: a
/// crew member clicks "Check In", we mint a row (Status = Pending), display
/// the Code as a QR to the crew's screen, and wait up to 10 minutes for
/// the assigned vendor to scan it. When the vendor scans successfully we
/// mark the row Consumed and write a normal AttendanceRecord.
///
/// Why a whole entity vs. a column on the assignment:
///   1. Multiple attempts per assignment are normal (crew regenerates the
///      QR when the window lapses) and we want each attempt auditable.
///   2. The row's TTL is a first-class concept — expiry status must be
///      queryable, and a background sweeper can flip stale Pendings without
///      touching the assignment row.
///   3. Fraud investigation later: "who scanned, when, from where" is
///      easier to reason about as its own append-only table.
///
/// The Code is the raw scannable string (no dashes; case-insensitive on
/// verify). We do NOT hash it — unlike an OTP, the code has zero value
/// once the row is Consumed/Expired, and a vendor must be authenticated
/// AND own the vendor slot on the assignment to redeem it, so plaintext
/// lookup is safe and lets us index/lookup in O(1).
/// </summary>
public sealed class PendingCheckIn : BaseEntity
{
    private PendingCheckIn() { }

    public PendingCheckIn(
        Guid    assignmentId,
        Guid    crewId,
        Guid    eventId,
        Guid?   shiftId,
        string  code,
        string  crewLocation,
        int     ttlMinutes = 10)
    {
        // CrewLocation is REQUIRED by product policy — the whole point of
        // this table is to capture WHERE THE CREW WAS the moment they hit
        // Check In on their own device, so that the eventual
        // AttendanceRecord's coords tell an auditor the crew's position
        // (not the vendor's scanning phone, which may be at a different
        // gate or booth). The command handler validates non-empty before
        // constructing; we assert here so nothing else can bypass it.
        if (string.IsNullOrWhiteSpace(crewLocation))
            throw new ArgumentException(
                "Crew location is required to mint a check-in.", nameof(crewLocation));

        AssignmentId = assignmentId;
        CrewId       = crewId;
        EventId      = eventId;
        ShiftId      = shiftId;
        Code         = code;
        CrewLocation = crewLocation;
        ExpiresAt    = DateTime.UtcNow.AddMinutes(ttlMinutes);
        Status       = PendingCheckInStatus.Pending;
    }

    /// <summary>The assignment being checked into.</summary>
    public Guid  AssignmentId { get; private set; }

    /// <summary>Copy of CrewId for query convenience — avoids a join to filter
    /// "my pending check-ins" on the client.</summary>
    public Guid  CrewId       { get; private set; }

    /// <summary>Copy of EventId for the same reason.</summary>
    public Guid  EventId      { get; private set; }

    /// <summary>ShiftId (nullable — legacy single-shift assignments).</summary>
    public Guid? ShiftId      { get; private set; }

    /// <summary>The scannable code. Opaque, ~12 base32 chars. Uniqueness is
    /// enforced by a partial unique index over (code) WHERE status = 0,
    /// so an old expired code can be reused if we ever wanted — but the
    /// generator picks fresh values each time, so collisions are astronomically
    /// unlikely.</summary>
    public string Code { get; private set; } = default!;

    /// <summary>Raw geolocation of the crew's device at the moment they tapped
    /// Check In — stored as "lat,lng" with 6 decimal places (roughly 10 cm
    /// precision, way more than we need but matches what
    /// checkin.js emits). Server-side reverse-geocoding happens later in
    /// VerifyCheckInHandler (Nominatim call, cached). Required at construction
    /// — see the ctor guard.</summary>
    public string CrewLocation { get; private set; } = default!;

    public DateTime ExpiresAt { get; private set; }
    public PendingCheckInStatus Status { get; private set; }

    /// <summary>The vendor user who successfully scanned this code. Null until
    /// Status flips to Consumed.</summary>
    public Guid?    ConsumedByVendorId { get; private set; }
    public DateTime? ConsumedAt        { get; private set; }

    /// <summary>Convenience — mirrors OtpRequest.IsExpired.</summary>
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    public bool IsValid   => Status == PendingCheckInStatus.Pending && !IsExpired;

    /// <summary>Called when a vendor scans successfully. Idempotent guard —
    /// once Consumed, further attempts must be rejected by the handler.</summary>
    public void MarkConsumed(Guid vendorUserId)
    {
        if (Status != PendingCheckInStatus.Pending)
            throw new InvalidOperationException(
                $"Cannot consume a PendingCheckIn in status {Status}.");
        Status              = PendingCheckInStatus.Consumed;
        ConsumedByVendorId  = vendorUserId;
        ConsumedAt          = DateTime.UtcNow;
    }

    /// <summary>Set by a background sweeper (or a lazy check on read) when
    /// ExpiresAt has passed and no scan happened.</summary>
    public void MarkExpired()
    {
        if (Status == PendingCheckInStatus.Pending)
            Status = PendingCheckInStatus.Expired;
    }

    /// <summary>Called when the crew regenerates a fresh QR — the prior row
    /// becomes historically visible but must never be redeemable.</summary>
    public void MarkCancelled()
    {
        if (Status == PendingCheckInStatus.Pending)
            Status = PendingCheckInStatus.Cancelled;
    }
}
