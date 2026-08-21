# Database Migrations — the one thing to never forget

**A new table or column needs changes in TWO places, not one.** Missing the
second one is what caused the `indian_states` incident (2026-08-22): the EF
migration was written and committed, but the table never existed in
production, because migrations don't auto-apply on deploy here.

## Why migrations don't just "run" on deploy

`Program.cs` gates `db.Database.MigrateAsync()` behind an env var:

```
RUN_MIGRATIONS_ON_STARTUP=true   (Railway variable, or Database:RunMigrationsOnStartup)
```

It's **off by default on purpose** — an earlier incident had the DB wiped
while `__EFMigrationsHistory` survived, so `MigrateAsync()` saw every
migration as "already applied" and did nothing, silently booting the app
against an empty schema. Auto-migrate-on-every-boot was too dangerous to
leave on permanently.

So: writing a migration class and pushing it is **not enough**. Nobody flips
`RUN_MIGRATIONS_ON_STARTUP` for routine deploys, so a brand-new migration
just sits pending forever unless you do the second step below.

## The second step: the emergency schema patch

`Program.cs` also runs an **idempotent raw-SQL patch** (`emergencySchemaPatchSql`)
on **every single boot**, regardless of the migration gate. It's a big
`DO $$ ... END $$` block full of `CREATE TABLE IF NOT EXISTS`,
`ALTER TABLE ... ADD COLUMN IF NOT EXISTS`, etc. — safe to run repeatedly,
safe on both a brand-new empty database and a fully-migrated one.

**Any new table (and any new column on an existing table) must get a
matching block added there**, mirroring the migration. Search that file for
`scope_of_work`, `venues`, or `terms_and_conditions` for the pattern to copy.

The seeder (`DatabaseSeeder.SeedAsync`) runs immediately after the patch, so
if a table also needs reference-data rows (like `indian_states`), add the
seeding there too — it already runs on every boot and is non-fatal.

## Checklist for every new table/column

1. Write the EF migration as normal (`Configurations/`, `Migrations/`,
   entity in `Domain/Entities/`) — this is still the schema of record and
   keeps `dotnet ef` / design-time tooling happy.
2. **Add the equivalent `CREATE TABLE IF NOT EXISTS` / `ALTER TABLE ... ADD
   COLUMN IF NOT EXISTS` to `emergencySchemaPatchSql` in `src/Api/Program.cs`.**
   This is the step that actually makes it to production.
3. If the table needs seed rows, add/extend a method in `DatabaseSeeder` and
   call it from `SeedAsync()` — make it idempotent (check for existing rows
   by a natural key before inserting).
4. After deploying, verify directly — don't assume:
   ```
   curl -s https://eventwos-production.up.railway.app/api/v1/<your-new-endpoint>
   ```
   A `42P01: relation "..." does not exist` means step 2 was skipped or
   didn't match what the migration created.

## If you ever DO need to run real migrations

Set `RUN_MIGRATIONS_ON_STARTUP=true` in Railway for exactly one deploy, let
it apply, then unset it again. Don't leave it on.
