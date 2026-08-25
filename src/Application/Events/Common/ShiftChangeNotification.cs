using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Events.Common;

/// <summary>
/// Builds the SHIFT_CHANGED notifications for one shift.
///
/// Shift-level news is deliberately NOT a fan-out: a fan-out addresses everyone on
/// the EVENT, and an event with four shifts would tell all four crews that a shift
/// they do not work has moved. The audience here is the people standing on this one
/// shift, which is small enough to resolve inside the request.
///
/// Two kinds of change reach people, and they are different audiences:
///
///   * TIME or SCOPE moved -> everyone on the shift: the crew have to turn up
///     somewhere else or at another hour, and their vendor is accountable for them.
///   * CAPACITY alone -> only vendors whose quota actually moved. A crew member does
///     not care that the shift grew from 2 seats to 3; the vendor who now has a seat
///     to fill (or one taken away) very much does. This is the case that was silent
///     when a shift grew and the vendor's Assign Crew modal still said "full".
///
/// Removal reuses the same template rather than inventing one: the seeded body is
/// "Your shift for {{EventName}} has changed to {{ShiftName}} on {{EventDate}}", and
/// the seeder never rewrites a template row that already exists in production, so a
/// new token here would render as empty text for real users. The shift label carries
/// the word "removed" instead.
/// </summary>
public static class ShiftChangeNotification
{
    /// <summary>Statuses that mean a person is still standing on the shift.</summary>
    private static readonly AssignmentStatus[] LiveStatuses =
    {
        AssignmentStatus.Invited,
        AssignmentStatus.VendorAccepted,
        AssignmentStatus.VendorApproved,
        AssignmentStatus.PendingManagerApproval,
        AssignmentStatus.ManagerApproved,
        AssignmentStatus.Confirmed,
        AssignmentStatus.Attended
    };

    public static bool IsLive(AssignmentStatus status) => Array.IndexOf(LiveStatuses, status) >= 0;

    /// <summary>
    /// Human label for a shift: what it is, when it runs. Goes in {{ShiftName}},
    /// which is the only token in the seeded body that can carry detail, so the
    /// time change has to be legible here or the message says nothing useful.
    /// </summary>
    public static string Label(string scopeName, DateTime startAt, DateTime? endAt, bool removed = false)
    {
        var window = endAt is null
            ? startAt.ToString("dd MMM HH:mm")
            : $"{startAt:dd MMM HH:mm} - {endAt.Value:HH:mm}";

        return removed ? $"{scopeName} - {window} (removed)" : $"{scopeName} - {window}";
    }

    /// <summary>
    /// One request per recipient. <paramref name="crewIds"/> and
    /// <paramref name="vendorIds"/> are the already-resolved live people on the
    /// shift; the caller resolves them because only the caller knows whether this
    /// was a time/scope move (everyone) or a capacity move (affected vendors only).
    /// </summary>
    public static IReadOnlyList<NotificationRequest> Build(
        Event                ev,
        Guid                 shiftId,
        string               shiftLabel,
        string               changeKey,
        IEnumerable<Guid>    crewIds,
        IEnumerable<Guid>    vendorIds,
        Guid?                actorUserId = null)
    {
        var data = new Dictionary<string, string?>
        {
            [NotificationTokens.EventName] = ev.Title,
            [NotificationTokens.ShiftName] = shiftLabel,
            [NotificationTokens.EventDate] = ev.StartAt.ToString("dd MMM yyyy"),
            [NotificationTokens.VenueName] = ev.Venue
        };

        return crewIds
            .Concat(vendorIds)
            .Where(id => id != Guid.Empty)
            .Distinct()
            // The person who made the change does not need telling. Normally an
            // admin who is on neither list, but a vendor editing their own shift
            // would otherwise be notified of their own click.
            .Where(id => actorUserId is null || id != actorUserId.Value)
            .Select(id => new NotificationRequest(
                NotificationTemplateCodes.ShiftChanged,
                RecipientUserId:  id,
                BusinessEventKey: $"shift:{shiftId}:changed:{changeKey}",
                Data:             data,
                EventId:          ev.Id,
                ActorUserId:      actorUserId))
            .ToList();
    }
}
