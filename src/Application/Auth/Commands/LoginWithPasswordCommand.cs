using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Auth.Commands;

/// <summary>
/// Username-or-email + password login. The Portal parameter is the role
/// the user clicked on (Admin/Manager, Vendor, Crew). We enforce that
/// the user's role matches the portal — a Vendor trying /login/admin
/// gets a "wrong portal" error with a hint to use the right page.
///
/// Returns a special flag (RequiresPasswordSetup) for legacy users
/// who have no PasswordHash yet — the frontend routes them to the
/// OTP-driven password-setup flow instead of attempting login.
/// </summary>
public sealed record LoginWithPasswordCommand(
    string UsernameOrEmail,
    string Password,
    string Portal,                  // "Admin" | "Vendor" | "Crew"
    string? DeviceId,
    string? DeviceName,
    string? IpAddress,
    string? UserAgent
) : IRequest<Result<PasswordLoginResponse>>;

public sealed record PasswordLoginResponse(
    bool             RequiresPasswordSetup,
    string?          Mobile,                  // returned only when setup is required
    AuthResponse?    Auth                     // populated on successful login
);
