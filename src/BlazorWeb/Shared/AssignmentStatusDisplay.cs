namespace EventWOS.BlazorWeb.Shared;

/// <summary>
/// Single source of truth for how an EventAssignment row should be rendered
/// across the manager Events drawer, vendor /vendor-assignments, crew
/// /my-assignments, and anywhere else an assignment appears.
///
/// Rule: on a Completed event, the workflow-state label often lies — e.g.
/// "ManagerApproved" on a Completed event really means "approved but
/// never showed up". This helper returns the *story* label (No Show /
/// Not Finalized / Attended) plus a small grey sub-note that preserves
/// the original workflow state for context. Non-completed events keep
/// their workflow label unchanged.
///
/// Pure function — no DI, no state. Easy to unit-test, easy to reuse.
/// </summary>
public static class AssignmentStatusDisplay
{
    /// <param name="assignmentStatus">Raw AssignmentStatus enum value as string.</param>
    /// <param name="eventStatus">Raw EventStatus enum value as string.</param>
    /// <param name="attendanceNote">Admin override note (if any) — wins over the auto sub-note.</param>
    public static (string Label, string BadgeClass, string? Subnote)
        For(string assignmentStatus, string eventStatus, string? attendanceNote = null)
    {
        var isCompleted = string.Equals(eventStatus, "Completed", StringComparison.OrdinalIgnoreCase);

        // Override note (admin-marked-attended) always surfaces verbatim,
        // regardless of event status — but only when status is Attended.
        if (string.Equals(assignmentStatus, "Attended", StringComparison.OrdinalIgnoreCase))
            return ("Attended", BadgeFor("Attended"), attendanceNote);

        if (!isCompleted)
            return (assignmentStatus, BadgeFor(assignmentStatus), null);

        // Completed-event relabeling
        return assignmentStatus switch
        {
            "Invited"
                => ("No Response", BadgeFor("NoShow"), "Never responded"),

            "VendorApproved" or "PendingManagerApproval"
                => ("Not Finalized", BadgeFor("NotFinalized"), "Pending manager approval"),

            "ManagerApproved"
                => ("No Show", BadgeFor("NoShow"), "Manager approved"),

            "Confirmed"
                => ("No Show", BadgeFor("NoShow"), "Confirmed but no attendance"),

            // Rejection-family rows keep their workflow label — the
            // rejection itself is the real story, not the absence.
            "Declined" or "RejectedByVendor" or "RejectedByManager" or "NoShow"
                => (assignmentStatus, BadgeFor(assignmentStatus), null),

            _ => (assignmentStatus, BadgeFor(assignmentStatus), null)
        };
    }

    /// <summary>True iff Admin can flip this row to Attended right now.</summary>
    public static bool CanAdminMarkAttended(string assignmentStatus, string eventStatus, bool isAdmin)
    {
        if (!isAdmin) return false;
        if (!string.Equals(eventStatus, "Completed", StringComparison.OrdinalIgnoreCase)) return false;

        return assignmentStatus is
            "Invited" or
            "VendorApproved" or
            "PendingManagerApproval" or
            "ManagerApproved" or
            "Confirmed" or
            "NoShow";
    }

    public static string BadgeFor(string status) => status switch
    {
        "Attended"               => "bg-green-50 text-green-700 border border-green-100",
        "Confirmed"              => "bg-blue-50 text-blue-700 border border-blue-100",
        "ManagerApproved"        => "bg-emerald-50 text-emerald-700 border border-emerald-100",
        "PendingManagerApproval" => "bg-amber-50 text-amber-700 border border-amber-100",
        "VendorApproved"         => "bg-indigo-50 text-indigo-700 border border-indigo-100",
        "Invited"                => "bg-yellow-50 text-yellow-700 border border-yellow-100",
        "NoShow"                 => "bg-rose-50 text-rose-700 border border-rose-100",
        "NotFinalized"           => "bg-orange-50 text-orange-700 border border-orange-100",
        "Declined" or "RejectedByVendor" or "RejectedByManager"
                                 => "bg-red-50 text-red-700 border border-red-100",
        _                        => "bg-gray-50 text-gray-700 border border-gray-100"
    };
}
