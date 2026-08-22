namespace EventWOS.Domain.Rules;

/// <summary>
/// Centralises what "active session" means for the Sessions page.
///
/// The UserSession.IsActive flag alone is not a liveness signal -- it only
/// flips on an explicit logout or an admin revoke. Someone who simply closes
/// the tab, lets the laptop sleep, or clears local storage never triggers
/// either, so the row would advertise itself as active until its refresh token
/// finally aged out 30 days later.
///
/// Every signed-in client pings /api/v1/sessions/ping every 30 seconds, and
/// that ping stamps LastActivityAt. So LastActivityAt is a real heartbeat, and
/// liveness reduces to "has this session been heard from recently".
/// </summary>
public static class SessionActivityRules
{
    /// <summary>
    /// How long a session may go without a heartbeat before it stops counting
    /// as active. Twenty missed 30-second beats -- generous enough to ride out
    /// a network blip or a mobile browser throttling background timers, tight
    /// enough that a closed browser drops off the list in minutes rather than
    /// weeks. A session that comes back (tab refocused, laptop woken) starts
    /// heartbeating again and reappears on its own.
    /// </summary>
    public static readonly TimeSpan HeartbeatGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Skip the heartbeat write when the row was already stamped this recently.
    /// Several open tabs on one device share a session and would otherwise each
    /// drive an identical UPDATE every 30 seconds.
    /// </summary>
    public static readonly TimeSpan HeartbeatWriteFloor = TimeSpan.FromSeconds(15);
}
