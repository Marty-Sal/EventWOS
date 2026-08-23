using EventWOS.BlazorWeb.Auth;
using Microsoft.AspNetCore.SignalR.Client;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Manages the SignalR connection lifecycle. Auto-reconnects on drop.
/// Exposes strongly-typed events for all backend push notifications.
/// </summary>
public sealed class NotificationHubService : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly AppAuthStateProvider _auth;
    private readonly IConfiguration       _config;

    // ─── Events ────────────────────────────────────────────────────────────
    public event Action<string>?               SystemNotificationReceived;
    public event Action<NotificationPayload>?  AssignmentInviteReceived;      // → crew
    public event Action<NotificationPayload>?  CrewAcceptedReceived;          // → vendor
    public event Action<NotificationPayload>?  CrewDeclinedReceived;          // → vendor
    public event Action<NotificationPayload>?  VendorApprovedReceived;        // → crew
    public event Action<NotificationPayload>?  VendorRejectedReceived;        // → crew
    public event Action<NotificationPayload>?  PendingManagerApprovalReceived;// → managers
    public event Action<NotificationPayload>?  ManagerApprovedReceived;       // → crew
    public event Action<NotificationPayload>?  ManagerRejectedReceived;       // → crew
    // Fires when a vendor scans a crew's QR and the server has committed
    // the actual AttendanceRecord. Payload = { assignmentId, eventId,
    // eventTitle, checkedInAt }. Consumed by CheckInQrModal to auto-close.
    public event Action<NotificationPayload>?  CheckInVerifiedReceived;       // → crew
    public event Action<NotificationPayload>?  EventAnnouncementReceived;     // → vendors + crew of an event
    // Payments
    public event Action<NotificationPayload>?  PaymentChangedReceived;        // payment created / approved / paid / rejected / hold
    public event Action<NotificationPayload>?  PayrollChangedReceived;        // batch submitted / approved / disbursed / rejected

    // A new vendor or crew self-registration landed in the approval queue.
    // Goes to Admins + Managers, and for crew also to the referring vendor,
    // since the vendor approves their own referred crew first.
    public event Action<NotificationPayload>?  RegistrationSubmittedReceived; // -> admins + managers (+ referring vendor)

    // The admin decision on someone's own registration.
    public event Action<NotificationPayload>?  RegistrationDecisionReceived;  // -> the registrant

    // "Your assignments changed, refetch" -- vendor invited to an event,
    // re-invited, or an invite revoked. These were all pushed by the server
    // with no client subscription at all, so they were silently dropped.
    public event Action<NotificationPayload>?  AssignmentChangedReceived;     // -> vendor / crew

    /// <summary>
    /// The notification platform's own feed. Every notification that has an
    /// active in-app template arrives here, already rendered, whatever business
    /// event produced it -- so new notification types show up without a client
    /// change, which is exactly what the twelve hand-written subscriptions above
    /// could never do.
    ///
    /// Runs ALONGSIDE those legacy pushes on purpose. They do double duty: pages
    /// like MyPayments and VendorPayments use them to refetch their tables, not
    /// just to toast. Removing them before that refresh behaviour has an
    /// equivalent here would leave stale data on screen with nothing visibly
    /// broken to explain it.
    /// </summary>
    public event Action<PlatformNotification>? PlatformNotificationReceived;  // -> the recipient

    public event Action?                       ConnectionStateChanged;

    public HubConnectionState State      => _connection?.State ?? HubConnectionState.Disconnected;
    public bool               IsConnected => State == HubConnectionState.Connected;

    public NotificationHubService(AppAuthStateProvider auth, IConfiguration config)
    {
        _auth   = auth;
        _config = config;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_connection is { State: HubConnectionState.Connected }) return;

        var token  = await _auth.GetAccessTokenAsync();
        var hubUrl = $"{_config["ApiBaseUrl"]}/hubs/notifications?access_token={token}";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect([
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(10)
            ])
            .Build();

        // System broadcast
        _connection.On<object>("SystemNotification",
            msg => SystemNotificationReceived?.Invoke(msg?.ToString() ?? ""));

        // Crew invitation (→ crew member)
        _connection.On<NotificationPayload>("AssignmentInvite",
            payload => AssignmentInviteReceived?.Invoke(payload));

        // Crew response (→ vendor)
        _connection.On<NotificationPayload>("CrewAccepted",
            payload => CrewAcceptedReceived?.Invoke(payload));
        _connection.On<NotificationPayload>("CrewDeclined",
            payload => CrewDeclinedReceived?.Invoke(payload));

        // Vendor review (→ crew)
        _connection.On<NotificationPayload>("VendorApprovedYou",
            payload => VendorApprovedReceived?.Invoke(payload));
        _connection.On<NotificationPayload>("VendorRejectedYou",
            payload => VendorRejectedReceived?.Invoke(payload));

        // Manager approval queue (→ managers/admins)
        _connection.On<NotificationPayload>("PendingManagerApproval",
            payload => PendingManagerApprovalReceived?.Invoke(payload));

        // Manager final decision (→ crew)
        _connection.On<NotificationPayload>("ManagerApprovedYou",
            payload => ManagerApprovedReceived?.Invoke(payload));
        _connection.On<NotificationPayload>("ManagerRejectedYou",
            payload => ManagerRejectedReceived?.Invoke(payload));

        // QR-verified check-in — server pushes this to the crew user's
        // group once VerifyCheckInHandler commits the attendance row.
        _connection.On<NotificationPayload>("CheckInVerified",
            payload => CheckInVerifiedReceived?.Invoke(payload));

        // Event notification broadcast by an Admin/Manager. The payload's
        // shape is wider server-side (subject, preview, attachment count,
        // deep link) but NotificationPayload only picks up EventTitle —
        // enough for the toast; the full message is fetched from the API by
        // whichever panel is listening.
        _connection.On<NotificationPayload>("EventAnnouncement",
            payload => EventAnnouncementReceived?.Invoke(payload));

        // Payments — all payment-lifecycle events fold into a single subscription
        // so consumers just refetch the list. (crew owner + vendor + admins/managers)
        foreach (var name in new[] { "PaymentCreated", "PaymentApproved", "PaymentPaid",
                                     "PaymentRejected", "PaymentOnHold", "PaymentUpdated" })
        {
            _connection.On<NotificationPayload>(name,
                payload => PaymentChangedReceived?.Invoke(payload));
        }
        _connection.On<NotificationPayload>("PayrollUpdated",
            payload => PayrollChangedReceived?.Invoke(payload));

        // --- Registrations -------------------------------------------------
        // New self-registration hitting the approval queue.
        _connection.On<NotificationPayload>("RegistrationSubmitted",
            payload => RegistrationSubmittedReceived?.Invoke(payload));

        // The approve/reject decision. The server has always pushed these two;
        // nothing subscribed, so the registrant never saw them.
        foreach (var name in new[] { "RegistrationApproved", "RegistrationRejected" })
        {
            _connection.On<NotificationPayload>(name,
                payload => RegistrationDecisionReceived?.Invoke(payload));
        }

        // --- Assignment churn ----------------------------------------------
        // Also pushed by the server and also unsubscribed until now. That is why
        // a vendor added to a shift got no live notification: the push was
        // emitted correctly and then dropped on the floor by the client.
        foreach (var name in new[] { "VendorEventAssigned", "VendorReinvited",
                                     "VendorInviteRevoked", "CrewInviteRevoked" })
        {
            _connection.On<NotificationPayload>(name,
                payload => AssignmentChangedReceived?.Invoke(payload));
        }

        // --- Notification platform feed -------------------------------------
        // Emitted by InAppNotificationSender for any notification whose in-app
        // template is active. One subscription covers every current and future
        // notification code.
        _connection.On<PlatformNotification>("NotificationReceived",
            payload => PlatformNotificationReceived?.Invoke(payload));

        _connection.Reconnected += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
        _connection.Closed      += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };

        await _connection.StartAsync(ct);
        ConnectionStateChanged?.Invoke();
    }

    public async Task StopAsync() =>
        await (_connection?.StopAsync() ?? Task.CompletedTask);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

/// <summary>
/// A notification from the platform feed, rendered server-side from its template.
///
/// Field names match the anonymous object InAppNotificationSender pushes, since
/// SignalR matches on name -- renaming one end silently yields nulls rather than
/// an error, so these must be changed in lockstep.
/// </summary>
public sealed record PlatformNotification(
    Guid     Id,
    string?  Code,
    string?  Title,
    string?  Body,
    Guid?    EventId,
    string?  Priority,
    DateTime SentAt)
{
    /// <summary>True for the cases worth interrupting someone over.</summary>
    public bool IsUrgent =>
        string.Equals(Priority, "High", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Priority, "Critical", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Generic payload for assignment-related push notifications.</summary>
public sealed record NotificationPayload(
    Guid?   AssignmentId  = null,
    Guid?   UserId        = null,
    string? PersonName    = null,
    string? Role          = null,
    string? BusinessName  = null,
    string? EventTitle    = null,
    string? VendorName    = null,
    string? CrewName      = null,
    string? Reason        = null,
    DateTime? EventStart  = null
);
