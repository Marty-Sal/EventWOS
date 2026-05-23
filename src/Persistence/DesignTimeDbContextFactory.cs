using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using MediatR;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Enums;

namespace EventWOS.Persistence;

/// <summary>
/// Used ONLY by EF Core tools (dotnet ef migrations add / update-database).
/// Never instantiated at runtime.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Walk up from src/Persistence to find appsettings in src/Api
        var basePath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "Api");

        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection not found. " +
                "Make sure src/Api/appsettings.Development.json has a valid connection string.");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(connectionString, o =>
            o.MigrationsAssembly("EventWOS.Persistence"));

        return new AppDbContext(
            optionsBuilder.Options,
            new NoOpMediator(),
            new NoOpCurrentUser());
    }
}

// ── Design-time no-op stubs ──────────────────────────────────────────────────

internal sealed class NoOpMediator : IMediator
{
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Design-time stub — not called at runtime.");

    public IAsyncEnumerable<object?> CreateStream(
        object request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Design-time stub — not called at runtime.");

    public Task Publish(object notification, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task Publish<TNotification>(TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
        => Task.CompletedTask;

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        => Task.FromResult<TResponse>(default!);

    public Task Send<TRequest>(TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => Task.CompletedTask;

    public Task<object?> Send(object request,
        CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(null);
}

internal sealed class NoOpCurrentUser : ICurrentUser
{
    public Guid? UserId    => null;
    public string? Mobile  => null;
    public UserRole? Role  => null;
    public IReadOnlyList<string> Permissions => Array.Empty<string>();
    public Guid? SessionId => null;
    public string? DeviceId  => null;
    public string? IpAddress => null;
    public bool IsAuthenticated => false;
    public bool IsInRole(UserRole role) => false;
    public bool HasPermission(string permission) => false;
}
