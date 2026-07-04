namespace EventWOS.Domain.Enums;

/// <summary>
/// Lifecycle states for a QR-verified check-in request.
///
/// Pending    → crew has requested; QR is live; vendor has not yet scanned.
/// Consumed   → vendor scanned successfully; the actual AttendanceRecord
///              has been written. This row is now historical.
/// Expired    → 10-min TTL lapsed without a scan. Row kept for audit; a
///              fresh Pending row must be minted before a new scan works.
/// Cancelled  → superseded by a newer Pending row (auto-cancel on regen)
///              or explicitly voided by an admin.
///
/// We keep Expired distinct from Cancelled so investigations can tell
/// "crew requested but nobody scanned" from "crew regenerated the code
/// mid-window" — very different signals for on-site troubleshooting.
/// </summary>
public enum PendingCheckInStatus
{
    Pending   = 0,
    Consumed  = 1,
    Expired   = 2,
    Cancelled = 3
}
