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
on **every single boot**, regardless of the migration gate. It's a long list of
`CREATE TABLE IF NOT EXISTS`, `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`, etc.
— safe to run repeatedly, safe on both a brand-new empty database and a
fully-migrated one.

### It runs section by section — keep the section headers intact

The patch text is **split on its `-- === name ===` header comments**, and each
section is executed as its own `DO $$ ... END $$` block with its own
try/catch. This matters: Postgres aborts an entire `DO` block on the first
error, so while the patch was ONE block, a single bad statement silently
discarded every statement after it. That is how the whole back half of the
patch stopped reaching production unnoticed — `ALTER TABLE user_sessions` threw
`42P01` (the table only existed in migrations, which the gate skips) and killed
~1000 lines of downstream patching on every boot, while the app booted "fine"
because the patch is deliberately non-fatal.

Consequences for anyone editing the patch:

- **Put new SQL under a `-- === name ===` header.** Anything before the first
  header is never executed.
- **Don't let a statement span two sections** (no `IF` opened in one section
  and closed in the next) — each section must be valid PL/pgSQL on its own.
- **A table your section ALTERs must be CREATEd in the same or an earlier
  section.** Migrations do not run on normal deploys, so never assume a table
  exists just because `InitialCreate` makes it.
- Check the boot log: `Emergency schema patch complete (N/34 sections).` means
  clean. `PARTIALLY applied` lists the failed section names, and each failure
  logs its Postgres `Where=` / `Detail=`.

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
   curl -s https://eventwos-production.up.railway.app/version
   ```
   `sha` is the commit the API container is actually running (Railway's
   `RAILWAY_GIT_COMMIT_SHA`), and `schemaPatch` reports `complete (34/34)` or
   names the sections that failed. **Check this before debugging anything else**
   — if `sha` is not the commit you just pushed, the API service did not
   redeploy and you are looking at old code. A 404 on `/version` means the same
   thing. This exact confusion turned the 2026-08-23 login outage into a much
   longer hunt than it needed to be. Then check your own endpoint:
   ```
   curl -s https://eventwos-production.up.railway.app/api/v1/<your-new-endpoint>
   ```
   A `42P01: relation "..." does not exist` means step 2 was skipped or
   didn't match what the migration created.
5. Sanity-check the patch SQL locally before pushing — it is never validated at
   build time. Both of these are quick:
   ```
   # parse-only, no server needed
   pip install pglast && python3 -c "from pglast import parse_sql; ..."
   # or run it for real against a throwaway database
   apt-get install -y postgresql-15 && initdb ... && psql -f patch.sql
   ```

## If you ever DO need to run real migrations

Set `RUN_MIGRATIONS_ON_STARTUP=true` in Railway for exactly one deploy, let
it apply, then unset it again. Don't leave it on.
