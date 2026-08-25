using EventOpsOracle.Application.Auth.Commands;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Auth.Handlers;

public sealed class LogoutHandler : IRequestHandler<LogoutCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;

    public LogoutHandler(IAppDbContext db, IUnitOfWork uow, IAuditLogger audit)
    {
        _db = db;
        _uow = uow;
        _audit = audit;
    }

    public async Task<Result> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        // Revoke refresh token
        var tokenHash = ComputeSha256(request.RefreshToken);
        var token = await _db.RefreshTokens
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash && r.UserId == request.UserId, cancellationToken);
        token?.Revoke("logout");

        // Terminate session
        var session = await _db.UserSessions
            .FirstOrDefaultAsync(s => s.SessionId == request.SessionId && s.UserId == request.UserId, cancellationToken);
        session?.Terminate("logout");

        await _uow.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(AuditAction.Logout, nameof(Domain.Entities.User),
            request.UserId.ToString(),
            additionalData: $"SessionId:{request.SessionId}",
            cancellationToken: cancellationToken);

        return Result.Success();
    }

    private static string ComputeSha256(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
