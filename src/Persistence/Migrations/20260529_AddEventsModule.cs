using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    [Migration("20260529000000_AddEventsModule")]
    public partial class AddEventsModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
-- events table
CREATE TABLE IF NOT EXISTS events (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    title               VARCHAR(200) NOT NULL,
    description         VARCHAR(2000),
    venue               VARCHAR(300) NOT NULL,
    address             VARCHAR(500),
    start_at            TIMESTAMP   NOT NULL,
    end_at              TIMESTAMP   NOT NULL,
    status              INT         NOT NULL DEFAULT 0,
    max_crew            INT         NOT NULL DEFAULT 0,
    created_by_user_id  UUID        NOT NULL REFERENCES users(id),
    notes               VARCHAR(1000),
    created_at          TIMESTAMP   NOT NULL DEFAULT NOW(),
    created_by          UUID,
    updated_at          TIMESTAMP,
    updated_by          UUID,
    is_deleted          BOOLEAN     NOT NULL DEFAULT false,
    deleted_at          TIMESTAMP,
    deleted_by          UUID
);

CREATE INDEX IF NOT EXISTS ix_events_status            ON events(status);
CREATE INDEX IF NOT EXISTS ix_events_start_at          ON events(start_at);
CREATE INDEX IF NOT EXISTS ix_events_created_by_user_id ON events(created_by_user_id);

-- event_assignments table
CREATE TABLE IF NOT EXISTS event_assignments (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    event_id            UUID        NOT NULL REFERENCES events(id) ON DELETE CASCADE,
    crew_id             UUID        NOT NULL REFERENCES users(id),
    vendor_id           UUID        NOT NULL REFERENCES users(id),
    assigned_by_user_id UUID        NOT NULL REFERENCES users(id),
    status              INT         NOT NULL DEFAULT 0,
    notes               VARCHAR(1000),
    confirmed_at        TIMESTAMP,
    declined_at         TIMESTAMP,
    created_at          TIMESTAMP   NOT NULL DEFAULT NOW(),
    created_by          UUID,
    updated_at          TIMESTAMP,
    updated_by          UUID,
    is_deleted          BOOLEAN     NOT NULL DEFAULT false,
    deleted_at          TIMESTAMP,
    deleted_by          UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_event_assignments_event_crew_unique
    ON event_assignments(event_id, crew_id) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_event_assignments_event_id  ON event_assignments(event_id);
CREATE INDEX IF NOT EXISTS ix_event_assignments_crew_id   ON event_assignments(crew_id);
CREATE INDEX IF NOT EXISTS ix_event_assignments_vendor_id ON event_assignments(vendor_id);

-- attendance_records table
CREATE TABLE IF NOT EXISTS attendance_records (
    id                   UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    assignment_id        UUID        NOT NULL REFERENCES event_assignments(id) ON DELETE CASCADE,
    event_id             UUID        NOT NULL REFERENCES events(id),
    crew_id              UUID        NOT NULL REFERENCES users(id),
    action               INT         NOT NULL,
    recorded_at          TIMESTAMP   NOT NULL DEFAULT NOW(),
    location             VARCHAR(500),
    recorded_by_user_id  VARCHAR(100),
    created_at           TIMESTAMP   NOT NULL DEFAULT NOW(),
    created_by           UUID,
    updated_at           TIMESTAMP,
    updated_by           UUID,
    is_deleted           BOOLEAN     NOT NULL DEFAULT false,
    deleted_at           TIMESTAMP,
    deleted_by           UUID
);

CREATE INDEX IF NOT EXISTS ix_attendance_records_assignment_id ON attendance_records(assignment_id);
CREATE INDEX IF NOT EXISTS ix_attendance_records_event_id      ON attendance_records(event_id);
CREATE INDEX IF NOT EXISTS ix_attendance_records_crew_id       ON attendance_records(crew_id);
CREATE INDEX IF NOT EXISTS ix_attendance_records_recorded_at   ON attendance_records(recorded_at);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS attendance_records;
DROP TABLE IF EXISTS event_assignments;
DROP TABLE IF EXISTS events;
");
        }
    }
}
