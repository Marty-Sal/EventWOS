using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Auth.Commands;

/// <summary>Revokes session and refresh token on logout.</summary>
public sealed record LogoutCommand(
    Guid UserId,
    Guid SessionId,
    string RefreshToken
) : IRequest<Result>;
