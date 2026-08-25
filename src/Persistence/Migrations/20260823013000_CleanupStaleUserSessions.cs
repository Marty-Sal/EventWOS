using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// One-time data cleanup, not a schema change.
    ///
    /// user_sessions.is_active only ever flipped to false on an EXPLICIT logout
    /// or admin revoke. A token refresh bug (fixed alongside this migration --
    /// see RefreshTokenHandler) meant every refresh minted a JWT whose session_id
    /// claim pointed at no row in this table, so the very next request after a
    /// refresh was rejected as "revoked" even though the user never logged out.
    /// The client reacted to that bogus 401 by silently clearing local storage
    /// and forcing a fresh login -- abandoning the row here, still marked
    /// is_active = true, instead of ending it. Repeated roughly every access
    /// token lifetime, this is exactly what produced dozens of "active" sessions
    /// for the same account in production, most of them dead within an hour of
    /// being created.
    ///
    /// This closes out every row that is currently marked active but has no
    /// live backing anymore: no non-revoked, non-expired refresh_tokens row for
    /// the same (user_id, device_id). That is the same "still logged in" test
    /// GetSessionsQuery now applies when listing sessions, so this migration
    /// just makes the stored data agree with what the UI already shows.
    ///
    /// Idempotent: re-running only ever affects rows that still match the
    /// condition, and once terminated a row no longer matches is_active = true,
    /// so running this twice is a no-op the second time.
    /// </summary>
    [Migration("20260823013000_CleanupStaleUserSessions")]
    public partial class CleanupStaleUserSessions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE user_sessions us
                SET is_active = false,
                    terminated_at = now(),
                    termination_reason = 'stale_no_live_token'
                WHERE us.is_active = true
                  AND us.is_deleted = false
                  AND NOT EXISTS (
                      SELECT 1 FROM refresh_tokens rt
                      WHERE rt.user_id = us.user_id
                        AND rt.device_id = us.device_id
                        AND rt.is_revoked = false
                        AND rt.expires_at > now()
                  );
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data cleanup only -- not reversible (we don't record which rows
            // this touched), and there is nothing meaningful to roll back to:
            // reviving sessions with no live token behind them would just
            // recreate the bug this migration fixes.
        }
    }
}
