using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Notifications.Queries;

/// <summary>
/// The caller's registered devices, for a settings screen that shows "you will be
/// notified on: this phone, your laptop" and lets one be removed.
///
/// Returns only active rows and never the encryption keys or the endpoint --
/// those are addressing material, and an API that echoed them back would let a
/// stolen token be turned into a way to push to someone's phone from outside.
/// </summary>
public sealed record GetMyPushDevicesQuery(Guid UserId) : IRequest<Result<IReadOnlyList<PushDeviceDto>>>;

/// <param name="IsCurrentDevice">Filled in by the client, which knows its own endpoint. Never inferred here.</param>
public sealed record PushDeviceDto(
    Guid      Id,
    string    Provider,
    string?   Platform,
    string?   DeviceLabel,
    DateTime? LastSeenAt,
    DateTime? LastSuccessAt,
    bool      IsCurrentDevice = false);

public sealed class GetMyPushDevicesHandler
    : IRequestHandler<GetMyPushDevicesQuery, Result<IReadOnlyList<PushDeviceDto>>>
{
    private readonly IAppDbContext _db;

    public GetMyPushDevicesHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<PushDeviceDto>>> Handle(GetMyPushDevicesQuery req, CancellationToken ct)
    {
        var rows = await _db.DeviceRegistrations
            .AsNoTracking()
            .Where(d => d.UserId == req.UserId && d.IsActive)
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => new
            {
                d.Id, d.Provider, d.Platform, d.UserAgent, d.LastSeenAt, d.LastSuccessAt
            })
            .ToListAsync(ct);

        var devices = rows
            .Select(r => new PushDeviceDto(
                r.Id,
                r.Provider.ToString(),
                r.Platform,
                DescribeDevice(r.Platform, r.UserAgent),
                r.LastSeenAt,
                r.LastSuccessAt))
            .ToList();

        return Result.Success<IReadOnlyList<PushDeviceDto>>(devices);
    }

    /// <summary>
    /// A short human label. Deliberately coarse -- enough for someone to
    /// recognise which of their own devices a row is, without turning the
    /// settings page into a fingerprint report.
    /// </summary>
    public static string DescribeDevice(string? platform, string? userAgent)
    {
        var agent = userAgent ?? string.Empty;

        var browser =
            agent.Contains("Edg/",     StringComparison.OrdinalIgnoreCase) ? "Edge"    :
            agent.Contains("OPR/",     StringComparison.OrdinalIgnoreCase) ? "Opera"   :
            agent.Contains("Chrome",   StringComparison.OrdinalIgnoreCase) ? "Chrome"  :
            agent.Contains("Firefox",  StringComparison.OrdinalIgnoreCase) ? "Firefox" :
            agent.Contains("Safari",   StringComparison.OrdinalIgnoreCase) ? "Safari"  :
            null;

        var device =
            agent.Contains("iPhone",  StringComparison.OrdinalIgnoreCase) ? "iPhone"  :
            agent.Contains("iPad",    StringComparison.OrdinalIgnoreCase) ? "iPad"    :
            agent.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android" :
            agent.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows" :
            agent.Contains("Mac OS",  StringComparison.OrdinalIgnoreCase) ? "Mac"     :
            platform;

        return (device, browser) switch
        {
            (null or "", null)     => "Unknown device",
            (null or "", not null) => browser!,
            (not null, null)       => device!,
            _                      => $"{browser} on {device}"
        };
    }
}
