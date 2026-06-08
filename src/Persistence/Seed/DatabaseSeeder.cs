using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Persistence.Seed;

/// <summary>
/// Idempotent database seeder. Safe to run on every startup.
/// Seeds: Roles, Permissions, RolePermissions, default Admin user.
/// Each step is fully independent — loads from DB before acting.
/// </summary>
public sealed class DatabaseSeeder
{
    private readonly AppDbContext _db;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(AppDbContext db, ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedRolesAsync(ct);
        await SeedPermissionsAsync(ct);
        await SeedRolePermissionsAsync(ct);
        await SeedAdminUserAsync(ct);
        await SeedTestUsersAsync(ct);
        await SeedDefaultScopeOfWorkAsync(ct);
        _logger.LogInformation("Database seeding complete.");
    }

    // ─── Default Scope of Work (Phase B) ─────────────────────────────────────
    // Backfills a single ""General"" row used as the FK target for synthetic
    // shifts created from legacy events. Idempotent — only inserts if no
    // active row with that name exists. The migration / Program.cs schema
    // patch already does this in SQL; this is here for the fresh-DB path
    // where the migration runs before any users exist (so the SQL skipped
    // the seed). Once a user lands, the next boot's seeder fills it in.
    private async Task SeedDefaultScopeOfWorkAsync(CancellationToken ct)
    {
        var exists = await _db.ScopesOfWork
            .IgnoreQueryFilters()
            .AnyAsync(s => s.Name.ToLower() == "general" && !s.IsDeleted, ct);
        if (exists) return;

        var creator = await _db.Users.OrderBy(u => u.CreatedAt).FirstOrDefaultAsync(ct);
        if (creator is null)
        {
            // No users at all — extremely fresh DB. Seeder runs again after
            // SeedAdminUserAsync on the next pass; this call site is after
            // that step, so this branch is mostly impossible. Belt-and-braces.
            _logger.LogWarning("No users present; deferring General scope-of-work seed.");
            return;
        }

        _db.ScopesOfWork.Add(new EventWOS.Domain.Entities.ScopeOfWork(
            "General",
            "Default scope of work backfilled from pre-shift events. " +
            "Edit the shift to assign a more specific category.",
            creator.Id));
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded \"General\" scope-of-work row.");
    }

    // ─── Roles ──────────────────────────────────────────────────────────────
    private async Task SeedRolesAsync(CancellationToken ct)
    {
        var existing = await _db.Roles.Select(r => r.RoleType).ToListAsync(ct);

        var toAdd = new[]
        {
            (UserRole.Admin,   "Admin",   "Full unrestricted system access"),
            (UserRole.Manager, "Manager", "Dynamic permission-based access"),
            (UserRole.Vendor,  "Vendor",  "Manage own crew and invitations"),
            (UserRole.Crew,    "Crew",    "Self profile access only"),
        }
        .Where(r => !existing.Contains(r.Item1))
        .Select(r => new Role(r.Item2, r.Item3, r.Item1))
        .ToList();

        if (toAdd.Count == 0) return;

        _db.Roles.AddRange(toAdd);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} roles", toAdd.Count);
    }

    // ─── Permissions ────────────────────────────────────────────────────────
    private static readonly (string Name, string Resource, string Action, string Desc)[] PermissionDefs =
    {
        ("users:read",        "users",       "read",    "View user profiles"),
        ("users:write",       "users",       "write",   "Create and update users"),
        ("users:delete",      "users",       "delete",  "Deactivate users"),
        ("users:status",      "users",       "status",  "Change user status"),
        ("roles:read",        "roles",       "read",    "View roles"),
        ("roles:write",       "roles",       "write",   "Assign roles"),
        ("permissions:read",  "permissions", "read",    "View permissions"),
        ("permissions:write", "permissions", "write",   "Grant/revoke permissions"),
        ("sessions:read",     "sessions",    "read",    "View sessions"),
        ("sessions:revoke",   "sessions",    "revoke",  "Revoke sessions"),
        ("vendors:read",      "vendors",     "read",    "View vendors"),
        ("vendors:write",     "vendors",     "write",   "Manage vendors"),
        ("crew:read",         "crew",        "read",    "View crew"),
        ("crew:write",        "crew",        "write",   "Manage crew"),
        ("crew:invite",       "crew",        "invite",  "Invite crew members"),
        ("crew:approve",      "crew",        "approve", "Approve crew assignments"),
        ("attendance:read",   "attendance",  "read",    "View attendance"),
        ("attendance:write",  "attendance",  "write",   "Manage attendance"),
        ("payments:read",     "payments",    "read",    "View payments"),
        ("payments:write",    "payments",    "write",   "Process payments"),
        ("payments:self",     "payments",    "self",    "View own payment records (Crew)"),
        ("payments:disburse", "payments",    "disburse","Disburse payment to crew (Vendor)"),
        ("payments:acknowledge","payments",  "acknowledge","Acknowledge receipt of payment (Crew)"),
        ("reports:read",      "reports",     "read",    "View reports"),
        ("audit:read",        "audit",       "read",    "View audit logs"),
        // Phase 2 — Events
        ("events:read",       "events",      "read",    "View events"),
        ("events:write",      "events",      "write",   "Create and manage events"),
        ("profile:read",      "profile",     "read",    "View own profile"),
        ("profile:write",     "profile",     "write",   "Update own profile"),

        // Phase A — Scope of Work catalog (admin-managed)
        ("scope_of_work:read",  "scope_of_work", "read",  "View scope-of-work catalog"),
        ("scope_of_work:write", "scope_of_work", "write", "Manage scope-of-work catalog"),

        // Phase C — Vendor shift allocations (per-vendor quota on a shift)
        ("vendor_allocations:read",  "vendor_allocations", "read",
            "View vendor allocations on event shifts"),
        ("vendor_allocations:write", "vendor_allocations", "write",
            "Create, update, or archive vendor allocations on shifts"),
    };

    private async Task SeedPermissionsAsync(CancellationToken ct)
    {
        var existing = await _db.Permissions.Select(p => p.Name).ToListAsync(ct);

        var toAdd = PermissionDefs
            .Where(p => !existing.Contains(p.Name))
            .Select(p => new Permission(p.Name, p.Resource, p.Action, p.Desc))
            .ToList();

        if (toAdd.Count == 0) return;

        _db.Permissions.AddRange(toAdd);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} permissions", toAdd.Count);
    }

    // ─── Role ↔ Permission mappings ─────────────────────────────────────────
    private async Task SeedRolePermissionsAsync(CancellationToken ct)
    {
        // ── One-time data fix: repair RolePermissions accidentally inserted with IsGranted=false ──
        // Prior to commit 6e8b2f1, TryAdd() passed isSysOverride into the isGranted constructor
        // parameter — leaving non-admin role grants stored as IsGranted=false. Those rows are then
        // filtered out by PermissionService and never appear in the JWT. Flip them back to true.
        var broken = await _db.RolePermissions
            .Where(rp => !rp.IsGranted)
            .ToListAsync(ct);
        if (broken.Count > 0)
        {
            foreach (var rp in broken)
            {
                // RolePermission has no public setter for IsGranted — use EF property API
                _db.Entry(rp).Property("IsGranted").CurrentValue = true;
            }
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning("Repaired {Count} RolePermissions that were stored as IsGranted=false", broken.Count);
        }

        // ADDITIVE upsert — never skips so new permissions are always backfilled.
        var roles = await _db.Roles.ToListAsync(ct);
        var perms = await _db.Permissions.ToListAsync(ct);

        if (roles.Count == 0 || perms.Count == 0)
        {
            _logger.LogWarning("Skipping role-permission seed — roles or permissions not yet available.");
            return;
        }

        // Load existing mappings as a fast-lookup set (roleId, permId)
        var existing = (await _db.RolePermissions.ToListAsync(ct))
                           .Select(rp => (rp.RoleId, rp.PermissionId))
                           .ToHashSet();

        Role?       GetRole(UserRole r) => roles.FirstOrDefault(x => x.RoleType == r);
        Permission? GetPerm(string n)   => perms.FirstOrDefault(x => x.Name == n);

        var toAdd = new List<RolePermission>();

        // Seeded role-permissions are ALWAYS granted (isGranted = true).
        // The IsGranted column exists to support deny-overrides at the user level, NOT role-level seeds.
        // The previous version mistakenly passed `isSysOverride` into the `isGranted` constructor
        // parameter — which caused all non-admin role-permissions to be inserted with IsGranted=false,
        // and they were then filtered out at JWT issuance time (rp.IsGranted filter in PermissionService).
        void TryAdd(Guid roleId, Guid permId, bool _unusedIsSysOverride = false)
        {
            if (!existing.Contains((roleId, permId)))
                toAdd.Add(new RolePermission(roleId, permId, isGranted: true));
        }

        // Admin gets every permission
        var adminRole = GetRole(UserRole.Admin);
        if (adminRole is not null)
            foreach (var p in perms)
                TryAdd(adminRole.Id, p.Id, true);

        // Vendor permissions
        var vendorRole = GetRole(UserRole.Vendor);
        if (vendorRole is not null)
        {
            foreach (var name in new[]
            {
                "crew:read", "crew:write", "crew:invite", "events:read",
                "crew:approve", "attendance:read", "profile:read", "profile:write",
                "payments:read", "payments:disburse",
                // Phase C: vendor can SEE their own allocations on shifts so
                // the portal can display the per-shift quota counter. They
                // cannot grant themselves more — write is Admin/Manager only.
                "vendor_allocations:read"
            })
            {
                var perm = GetPerm(name);
                if (perm is not null) TryAdd(vendorRole.Id, perm.Id);
            }
        }

        // Crew permissions — additive so new permissions (e.g. payments:self) are always backfilled
        var crewRole = GetRole(UserRole.Crew);
        if (crewRole is not null)
        {
            foreach (var name in new[]
            {
                "profile:read", "profile:write", "events:read",
                "attendance:read", "payments:self", "payments:acknowledge"
            })
            {
                var perm = GetPerm(name);
                if (perm is not null) TryAdd(crewRole.Id, perm.Id);
            }
        }

        // Manager — minimal baseline only. The rest is assigned dynamically by Admin
        // through the role-permission UI. Baseline covers "stuff a logged-in user
        // simply needs to use the app at all":
        //   • profile:read / profile:write  — view + edit own profile, hit
        //     /approval-queue (which is gated by profile:read because it's the
        //     lowest-common-denominator perm + a role check in the controller).
        // Everything else — users:read, vendors:read, crew:read, attendance:read,
        // payments:read, sessions:read, audit:read, users:status — stays Admin-assigned
        // so a brand-new Manager has *no power* until someone explicitly grants it.
        var managerRole = GetRole(UserRole.Manager);
        if (managerRole is not null)
        {
            // Managers can read the scope-of-work catalog so they can pick
            // categories when creating events. They cannot edit the catalog
            // (Admin only) — keeps "what work types exist" a centrally-governed
            // taxonomy rather than something every Manager mutates.
            foreach (var name in new[]
            {
                "profile:read", "profile:write", "scope_of_work:read",
                // Phase C: Managers run day-to-day staffing, so they can
                // both view and grant per-vendor quotas on event shifts.
                // (Vendors only get :read; Admins implicitly get both via
                // the "Admin owns everything" loop above.)
                "vendor_allocations:read", "vendor_allocations:write"
            })
            {
                var perm = GetPerm(name);
                if (perm is not null) TryAdd(managerRole.Id, perm.Id);
            }
        }

        if (toAdd.Count > 0)
        {
            _db.RolePermissions.AddRange(toAdd);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Backfilled {Count} missing role-permission mappings", toAdd.Count);
        }
        else
        {
            _logger.LogInformation("Role-permission mappings already up to date.");
        }
    }

    // ─── Default Admin User ──────────────────────────────────────────────────
    private async Task SeedAdminUserAsync(CancellationToken ct)
    {
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Role == UserRole.Admin, ct)) return;

        var admin = new User("+911234567890", "System Administrator", UserRole.Admin);
        admin.Activate();
        _db.Users.Add(admin);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded default admin user (mobile: +911234567890)");
    }

    // ─── Dev/Test Users ──────────────────────────────────────────────────────
    private async Task SeedTestUsersAsync(CancellationToken ct)
    {
        var testMobiles = new[]
        {
            ("+911233456789", "Sameer Khan",    UserRole.Crew),
            ("+911223456789", "Priya Vendors",  UserRole.Vendor),
        };

        foreach (var (mobile, name, role) in testMobiles)
        {
            var exists = await _db.Users.IgnoreQueryFilters()
                .AnyAsync(u => u.Mobile == mobile, ct);
            if (exists) continue;

            var user = new User(mobile, name, role);
            user.Activate();
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Seeded test user {Name} ({Mobile}) as {Role}", name, mobile, role);
        }
    }
}
