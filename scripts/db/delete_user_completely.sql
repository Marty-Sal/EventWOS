-- ============================================================================
-- delete_user_completely.sql
--
-- Permanently and completely removes ONE user and every row that belongs to
-- them, across every table in the schema. Safe by construction:
--   1. Prints a full impact report (row counts per table) FIRST.
--   2. Aborts the WHOLE transaction (nothing is deleted) if the user has any
--      "attribution" footprint on someone else's live data that cannot be
--      auto-resolved (see "BLOCKING ATTRIBUTION CHECK" below) — deleting
--      those would either destroy another user's real records or violate a
--      NOT NULL constraint. That situation needs a human decision, not a
--      script.
--   3. Otherwise deletes everything in dependency-safe order inside a single
--      transaction and COMMITs.
--
-- USAGE:
--   psql "$DATABASE_URL" -v mobile="1234567890" -f delete_user_completely.sql
--
--   (plain digits, no extra quoting needed — the SET line below handles it)
--
-- WHAT GETS AUTO-HANDLED BY THE DATABASE (no script code needed):
--   refresh_tokens, user_sessions, user_role_permissions, manager_permissions
--     -> ON DELETE CASCADE, removed automatically when the users row is deleted.
--   users.manager_id / users.vendor_id on OTHER users who report to / belong
--   to this user
--     -> ON DELETE SET NULL, automatically cleared. NOTE: this silently
--        disconnects any Crew from their Vendor, or any Manager-managed user
--        from their Manager. The impact report below tells you if that would
--        happen so you're not surprised.
--
-- WHAT THIS SCRIPT DELETES MANUALLY (the user's OWN data trail — these tables
-- have RESTRICT/NO ACTION foreign keys to users, so Postgres would otherwise
-- reject the delete outright):
--   attendance_records (crew_id), event_assignments (crew_id or vendor_id),
--   crew_payments (crew_id, vendor_id, or via a deleted assignment),
--   crew_group_members (crew_id), crew_groups (vendor_id),
--   payroll_batches (vendor_id), vendor_crew_mappings (vendor_id or crew_id),
--   vendor_shift_allocations (vendor_id), otp_requests (by mobile).
--
-- WHAT THIS SCRIPT NULLS OUT (nullable "who did this" audit attribution —
-- the row survives, it just loses the actor reference):
--   audit_logs.performed_by_user_id
--
-- WHAT THIS SCRIPT REFUSES TO TOUCH AUTOMATICALLY (NOT NULL attribution
-- columns — the referenced row is someone ELSE's real record):
--   events.created_by_user_id, event_assignments.assigned_by_user_id (as the
--   actor on an assignment that ISN'T this user's own),
--   vendor_crew_mappings.approved_by_manager_id (as the approving manager on
--   a mapping that ISN'T this user's own).
--   If any of these exist, the script aborts with a clear error naming the
--   table/count so a human can decide (e.g. reassign to another admin, or
--   confirm this is intentional and extend the script).
-- ============================================================================

\set ON_ERROR_STOP on
\pset pager off

BEGIN;

SET myvars.mobile = :'mobile';

DO $$
DECLARE
    v_mobile      text := current_setting('myvars.mobile');
    v_user_id     uuid;
    v_role        int;
    v_status      int;
    v_count       int;
    v_blocked     boolean := false;
    v_block_msg   text := '';
BEGIN
    SELECT id, role, status INTO v_user_id, v_role, v_status
    FROM users WHERE mobile = v_mobile;

    IF v_user_id IS NULL THEN
        RAISE EXCEPTION 'No user found with mobile = %', v_mobile;
    END IF;

    RAISE NOTICE '=== Target user: id=% mobile=% role=% status=% ===', v_user_id, v_mobile, v_role, v_status;

    -- ── Impact report ───────────────────────────────────────────────────────
    SELECT count(*) INTO v_count FROM attendance_records WHERE crew_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'attendance_records (crew_id): % row(s) -> will DELETE', v_count; END IF;

    SELECT count(*) INTO v_count FROM event_assignments WHERE crew_id = v_user_id OR vendor_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'event_assignments (crew_id/vendor_id): % row(s) -> will DELETE', v_count; END IF;

    SELECT count(*) INTO v_count FROM crew_payments WHERE crew_id = v_user_id OR vendor_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'crew_payments (crew_id/vendor_id): % row(s) -> will DELETE', v_count; END IF;

    SELECT count(*) INTO v_count FROM crew_group_members WHERE crew_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'crew_group_members (crew_id): % row(s) -> will DELETE', v_count; END IF;

    SELECT count(*) INTO v_count FROM crew_groups WHERE vendor_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'crew_groups (vendor_id): % row(s) -> will DELETE (cascades to any remaining crew_group_members)', v_count; END IF;

    SELECT count(*) INTO v_count FROM payroll_batches WHERE vendor_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'payroll_batches (vendor_id): % row(s) -> will DELETE', v_count; END IF;

    SELECT count(*) INTO v_count FROM vendor_crew_mappings WHERE vendor_id = v_user_id OR crew_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'vendor_crew_mappings (vendor_id/crew_id): % row(s) -> will DELETE', v_count; END IF;

    SELECT count(*) INTO v_count FROM vendor_shift_allocations WHERE vendor_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'vendor_shift_allocations (vendor_id): % row(s) -> will DELETE', v_count; END IF;

    SELECT count(*) INTO v_count FROM otp_requests WHERE mobile = v_mobile;
    IF v_count > 0 THEN RAISE NOTICE 'otp_requests (mobile): % row(s) -> will DELETE', v_count; END IF;

    SELECT count(*) INTO v_count FROM refresh_tokens WHERE user_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'refresh_tokens: % row(s) -> auto CASCADE', v_count; END IF;

    SELECT count(*) INTO v_count FROM user_sessions WHERE user_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'user_sessions: % row(s) -> auto CASCADE', v_count; END IF;

    SELECT count(*) INTO v_count FROM user_role_permissions WHERE user_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'user_role_permissions: % row(s) -> auto CASCADE', v_count; END IF;

    SELECT count(*) INTO v_count FROM manager_permissions WHERE manager_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'manager_permissions: % row(s) -> auto CASCADE', v_count; END IF;

    SELECT count(*) INTO v_count FROM users WHERE manager_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'users.manager_id on % other user(s) -> auto SET NULL (they lose their manager link)', v_count; END IF;

    SELECT count(*) INTO v_count FROM users WHERE vendor_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'users.vendor_id on % other user(s) -> auto SET NULL (they lose their vendor link)', v_count; END IF;

    SELECT count(*) INTO v_count FROM audit_logs WHERE performed_by_user_id = v_user_id;
    IF v_count > 0 THEN RAISE NOTICE 'audit_logs.performed_by_user_id: % row(s) -> will NULL (rows kept, actor cleared)', v_count; END IF;

    -- ── Blocking attribution check (NOT NULL — cannot auto-resolve) ────────
    SELECT count(*) INTO v_count FROM events WHERE created_by_user_id = v_user_id;
    IF v_count > 0 THEN
        v_blocked := true;
        v_block_msg := v_block_msg || format('events.created_by_user_id: %s row(s). ', v_count);
    END IF;

    SELECT count(*) INTO v_count FROM event_assignments
        WHERE assigned_by_user_id = v_user_id AND crew_id <> v_user_id AND vendor_id <> v_user_id;
    IF v_count > 0 THEN
        v_blocked := true;
        v_block_msg := v_block_msg || format('event_assignments.assigned_by_user_id (on someone else''s assignment): %s row(s). ', v_count);
    END IF;

    SELECT count(*) INTO v_count FROM vendor_crew_mappings
        WHERE approved_by_manager_id = v_user_id AND vendor_id <> v_user_id AND crew_id <> v_user_id;
    IF v_count > 0 THEN
        v_blocked := true;
        v_block_msg := v_block_msg || format('vendor_crew_mappings.approved_by_manager_id (on someone else''s mapping): %s row(s). ', v_count);
    END IF;

    IF v_blocked THEN
        RAISE EXCEPTION 'ABORTED — this user has NOT NULL attribution on other users'' real records: %s. '
            'These need a human decision (e.g. reassign to another admin) before this user can be deleted. Nothing was changed.', v_block_msg;
    END IF;

    -- ── Delete the user's own data trail (dependency-safe order) ───────────
    DELETE FROM crew_payments      WHERE crew_id = v_user_id OR vendor_id = v_user_id;
    DELETE FROM attendance_records WHERE crew_id = v_user_id;
    DELETE FROM event_assignments  WHERE crew_id = v_user_id OR vendor_id = v_user_id;
    DELETE FROM vendor_shift_allocations WHERE vendor_id = v_user_id;
    DELETE FROM vendor_crew_mappings     WHERE vendor_id = v_user_id OR crew_id = v_user_id;
    DELETE FROM crew_group_members WHERE crew_id = v_user_id;
    DELETE FROM crew_groups        WHERE vendor_id = v_user_id;
    DELETE FROM payroll_batches    WHERE vendor_id = v_user_id;
    DELETE FROM otp_requests       WHERE mobile = v_mobile;

    -- Preserve audit history rows, just drop the dangling actor reference.
    UPDATE audit_logs SET performed_by_user_id = NULL WHERE performed_by_user_id = v_user_id;

    -- refresh_tokens / user_sessions / user_role_permissions / manager_permissions
    -- all CASCADE, and users.manager_id / users.vendor_id on other rows SET NULL,
    -- automatically as part of this final delete.
    DELETE FROM users WHERE id = v_user_id;

    RAISE NOTICE '=== User % (mobile %) deleted completely. ===', v_user_id, v_mobile;
END $$;

COMMIT;
