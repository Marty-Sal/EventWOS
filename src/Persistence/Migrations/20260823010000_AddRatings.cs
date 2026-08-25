using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Adds the ratings table: vendor and crew performance, per event, on two axes.
    ///
    /// See Rating.cs for why this replaces the previous mechanisms. In short, both
    /// were lossy: users.rating was overwritten per vendor (so a second event
    /// destroyed the first), and users.crew_rating folded stars into a running mean
    /// that discarded the individual scores, making corrections impossible. Those
    /// columns survive as caches recomputed from this table by full aggregation.
    ///
    /// Existing EventAssignment.VendorRating stars are backfilled rather than
    /// discarded -- they are real feedback, and dropping them would reset every
    /// crew member's reputation on deploy. Imported rows are flagged
    /// is_legacy_single_score because the old column never split performance from
    /// cooperation, so both axes carry the same imported number.
    ///
    /// >>> Also mirrored in emergencySchemaPatchSql in Program.cs <<<
    /// Migrations do not auto-apply on deploy (RUN_MIGRATIONS_ON_STARTUP gate),
    /// so a migration alone would never reach production. See
    /// docs/DatabaseMigrations.md.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260823010000_AddRatings")]
    public partial class AddRatings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
    -- Sample size behind users.rating. An average without its count invites
    -- trusting one glowing review as much as twenty.
    ALTER TABLE users ADD COLUMN IF NOT EXISTS rating_count INT NOT NULL DEFAULT 0;

    -- ═══ ratings ═════════════════════════════════════════════════════════
    -- Single source of truth for vendor + crew reputation, scored on two axes
    -- (performance, cooperation) and scoped to ONE event.
    --
    -- Replaces two lossy mechanisms that could not survive a correction:
    --   * users.rating was OVERWRITTEN per vendor, so rating a vendor for their
    --     second event destroyed the first. No history, no count, no average.
    --   * users.crew_rating folded each star into a running mean, discarding the
    --     individual scores -- so nothing could be revised or recomputed. That is
    --     the same incremental-cache pattern behind the max_crew drift bug fixed
    --     earlier in this file.
    -- Those two columns remain, but purely as CACHES recomputed from this table
    -- by full aggregation at the bottom of this block.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'ratings') THEN
        CREATE TABLE ratings (
            id                      UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            event_id                UUID NOT NULL,
            subject_user_id         UUID NOT NULL,
            -- 1 = Vendor, 2 = Crew. Stored, not inferred from users.role: a role
            -- can change, and a promotion must not re-file old ratings.
            subject_type            INT  NOT NULL,
            rater_user_id           UUID NOT NULL,
            performance             INT  NOT NULL,
            cooperation             INT  NOT NULL,
            comment                 VARCHAR(1000),
            -- Provenance for crew ratings; never part of uniqueness.
            assignment_id           UUID,
            rated_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
            revised_at              TIMESTAMPTZ,
            -- Marks rows imported from the old single-star vendor_rating, where
            -- both axes hold the same number because the split never existed.
            is_legacy_single_score  BOOLEAN NOT NULL DEFAULT false,
            created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by              UUID,
            updated_at              TIMESTAMPTZ,
            updated_by              UUID,
            is_deleted              BOOLEAN NOT NULL DEFAULT false,
            deleted_at              TIMESTAMPTZ,
            deleted_by              UUID,
            CONSTRAINT ck_ratings_performance CHECK (performance BETWEEN 1 AND 5),
            CONSTRAINT ck_ratings_cooperation CHECK (cooperation BETWEEN 1 AND 5),
            CONSTRAINT ck_ratings_subject_type CHECK (subject_type IN (1, 2)),
            -- Self-rating would quietly inflate a real average, so the database
            -- refuses it rather than trusting every caller to remember.
            CONSTRAINT ck_ratings_no_self_rating CHECK (subject_user_id <> rater_user_id)
        );
    END IF;

    CREATE INDEX IF NOT EXISTS ix_ratings_event_id      ON ratings (event_id);
    CREATE INDEX IF NOT EXISTS ix_ratings_rater_user_id ON ratings (rater_user_id);
    -- Covers the only hot read: ""average for this person in this capacity"",
    -- which every dashboard and user list performs.
    CREATE INDEX IF NOT EXISTS ix_ratings_subject_user_id_subject_type
        ON ratings (subject_user_id, subject_type);

    -- ONE live rating per person per event. Partial on is_deleted so withdrawing
    -- a rating frees the slot instead of blocking it forever. This index is what
    -- makes ""re-rating is a revision"" true under concurrency -- two simultaneous
    -- checkouts would otherwise both pass a ""already rated?"" read and each insert.
    CREATE UNIQUE INDEX IF NOT EXISTS ux_ratings_event_subject_live
        ON ratings (event_id, subject_user_id, subject_type) WHERE is_deleted = false;

    -- ═══ ratings backfill from event_assignments.vendor_rating ════════════
    -- Those stars are real feedback vendors already gave; dropping them would
    -- reset every crew member's reputation to zero on deploy. Imported flagged
    -- as legacy because the old column never separated the two axes.
    --
    -- DISTINCT ON collapses a crew member rated on several shifts of the SAME
    -- event down to their most recent star, because an event must count once --
    -- the per-shift model was letting a three-shift crew member outvote a
    -- one-shift colleague three to one.
    --
    -- ON CONFLICT makes re-runs harmless, so this is safe on every boot.
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'event_assignments' AND column_name = 'vendor_rating') THEN
        INSERT INTO ratings (
            event_id, subject_user_id, subject_type, rater_user_id,
            performance, cooperation, assignment_id, rated_at, is_legacy_single_score)
        SELECT DISTINCT ON (ea.event_id, ea.crew_id)
            ea.event_id,
            ea.crew_id,
            2,                                  -- RatingSubjectType.Crew
            ea.vendor_id,
            GREATEST(1, LEAST(5, ROUND(ea.vendor_rating)::INT)),
            GREATEST(1, LEAST(5, ROUND(ea.vendor_rating)::INT)),
            ea.id,
            COALESCE(ea.rated_at, ea.updated_at, ea.created_at, now()),
            true
        FROM event_assignments ea
        WHERE ea.vendor_rating IS NOT NULL
          AND ea.crew_id       IS NOT NULL
          AND ea.vendor_id     IS NOT NULL
          AND ea.crew_id      <> ea.vendor_id
          AND COALESCE(ea.is_deleted, false) = false
        ORDER BY ea.event_id, ea.crew_id,
                 COALESCE(ea.rated_at, ea.updated_at, ea.created_at) DESC
        ON CONFLICT (event_id, subject_user_id, subject_type) WHERE is_deleted = false DO NOTHING;
    END IF;

    -- ═══ Reputation cache recompute ══════════════════════════════════════
    -- users.crew_rating / crew_rating_count / rating are DERIVED. Recomputed
    -- here by full aggregation rather than nudged, so they are correct by
    -- construction and self-heal on the next boot if anything ever drifts.
    -- The IS DISTINCT FROM guards make this a no-op once settled.
    UPDATE users u
       SET crew_rating       = agg.avg_score,
           crew_rating_count = agg.cnt
      FROM (SELECT subject_user_id,
                   ROUND(AVG((performance + cooperation) / 2.0), 2) AS avg_score,
                   COUNT(*)                                         AS cnt
              FROM ratings
             WHERE subject_type = 2 AND is_deleted = false
          GROUP BY subject_user_id) agg
     WHERE u.id = agg.subject_user_id
       AND (u.crew_rating       IS DISTINCT FROM agg.avg_score
         OR u.crew_rating_count IS DISTINCT FROM agg.cnt);

    UPDATE users u
       SET rating       = agg.avg_score,
           rating_count = agg.cnt
      FROM (SELECT subject_user_id,
                   ROUND(AVG((performance + cooperation) / 2.0), 2) AS avg_score,
                   COUNT(*)                                         AS cnt
              FROM ratings
             WHERE subject_type = 1 AND is_deleted = false
          GROUP BY subject_user_id) agg
     WHERE u.id = agg.subject_user_id
       AND (u.rating       IS DISTINCT FROM agg.avg_score
         OR u.rating_count IS DISTINCT FROM agg.cnt);
                END $$;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The cached averages on users are left as they are: recomputing them
            // to NULL would destroy reputation that this table was the only record
            // of. A re-Up repopulates them from the backfill anyway.
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ratings;");
        }
    }
}
