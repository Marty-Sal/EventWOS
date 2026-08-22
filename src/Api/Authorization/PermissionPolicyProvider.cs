using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace EventWOS.Api.Authorization;

/// <summary>
/// Manufactures <c>perm:&lt;permission&gt;</c> authorization policies on demand.
///
/// WHY THIS EXISTS
/// ---------------
/// <see cref="PermissionAttribute"/> turns <c>[Permission("files:upload")]</c>
/// into a policy *name* (<c>perm:files:upload</c>) - but a name is not a
/// registration. Previously every one of those policies also had to be
/// hand-added to a long <c>AddAuthorization</c> list in Program.cs, and if you
/// forgot, the endpoint failed neither at build time nor at boot: it threw
/// <c>InvalidOperationException: The AuthorizationPolicy named 'perm:x' was not
/// found</c> on the first real request, in production.
///
/// That footgun fired at least three separate times in this codebase
/// (scope_of_work, then venues/terms, then the entire files module - which broke
/// every upload and download endpoint, i.e. profile photos and ID proofs).
/// Program.cs carried apologetic comments about it instead of a fix.
///
/// Now the policy is derived from the attribute itself, so declaring
/// <c>[Permission("anything:here")]</c> is sufficient and the two can never
/// drift apart again. Authorization semantics are unchanged - the manufactured
/// policy is exactly what the hand-written registrations built: a single
/// <see cref="PermissionRequirement"/> carrying the permission string, evaluated
/// by <see cref="PermissionHandler"/> (Admin bypasses; everyone else needs a
/// matching "permission" JWT claim).
///
/// Non-<c>perm:</c> policy names fall through to the default provider, so
/// anything registered the normal way keeps working.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private const string Prefix = "perm:";

    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        => _fallback = new DefaultAuthorizationPolicyProvider(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        // An explicitly registered policy always wins, so any custom policy
        // added the normal way stays authoritative.
        var existing = await _fallback.GetPolicyAsync(policyName);
        if (existing is not null) return existing;

        if (!policyName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var permission = policyName[Prefix.Length..];
        if (string.IsNullOrWhiteSpace(permission)) return null;

        return new AuthorizationPolicyBuilder()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
    }
}
