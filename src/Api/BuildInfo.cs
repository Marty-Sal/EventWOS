namespace EventOpsOracle.Api;

/// <summary>
/// Boot-time facts about the container that is actually running, surfaced by
/// GET /version.
///
/// Why this exists: on 2026-08-23 login was down with
/// 42P01 relation "user_sessions" does not exist. The fix was committed and
/// pushed, the Blazor service picked it up (its version.json reported the new
/// commit), but the API service kept serving an older container -- and there was
/// no way to tell that from the outside. Diagnosing it cost a full round of
/// inference from screenshots and code reading. "Which commit is live, and did
/// the boot schema patch actually work?" now has a one-request answer that needs
/// no Railway log access.
///
/// The absence of /version is itself a signal: a 404 means the container
/// predates this file and is therefore NOT running current code.
///
/// Deliberately does NOT expose raw SQL or Postgres error text. Failed section
/// NAMES are enough to know where to look; the full Where=/Detail= stays in the
/// boot log, which is not publicly readable.
/// </summary>
public static class BuildInfo
{
    /// <summary>When this container booted.</summary>
    public static readonly DateTime BootedAtUtc = DateTime.UtcNow;

    /// <summary>
    /// Railway injects RAILWAY_GIT_COMMIT_SHA into every deploy, so this is the
    /// commit the running image was built from -- not what is on the branch.
    /// </summary>
    public static string CommitSha { get; } =
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA"),
            Environment.GetEnvironmentVariable("GIT_COMMIT_SHA"),
            "unknown");

    public static string ShortSha => CommitSha.Length >= 7 ? CommitSha[..7] : CommitSha;

    // --- Set by the startup migrate/patch block in Program.cs ---------------

    /// <summary>True when RUN_MIGRATIONS_ON_STARTUP armed EF migrations for this boot.</summary>
    public static bool MigrationGateArmed { get; set; }
    public static int MigrationsApplied { get; set; }
    public static int MigrationsPending { get; set; }

    /// <summary>not-run | complete | partial | skipped</summary>
    public static string SchemaPatchStatus { get; set; } = "not-run";
    public static int SchemaPatchApplied { get; set; }
    public static int SchemaPatchTotal { get; set; }
    public static IReadOnlyList<string> SchemaPatchFailedSections { get; set; } = Array.Empty<string>();

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c)) return c.Trim();
        return "unknown";
    }
}
