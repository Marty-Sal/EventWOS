using System.Net.Http.Json;

namespace EventWOS.BlazorWeb.Services;

/// <summary>One notification in the caller's inbox. Mirrors MyNotificationDto.</summary>
public sealed record InboxNotificationDto(
    Guid     Id,
    string   Code,
    string?  Title,
    string   Body,
    string   Priority,
    Guid?    EventId,
    bool     IsRead,
    DateTime CreatedAt)
{
    public bool IsUrgent =>
        string.Equals(Priority, "High", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Priority, "Critical", StringComparison.OrdinalIgnoreCase);

    /// <summary>Relative age, because "3h ago" is read faster than a timestamp.</summary>
    public string Age
    {
        get
        {
            var span = DateTime.UtcNow - CreatedAt;

            if (span.TotalMinutes <  1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours   < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays    <  7) return $"{(int)span.TotalDays}d ago";

            return CreatedAt.ToLocalTime().ToString("dd MMM");
        }
    }
}

public sealed record InboxPageDto(
    List<InboxNotificationDto> Items,
    int UnreadCount,
    int Total);

public interface INotificationApiService
{
    Task<InboxPageDto> MineAsync(bool unreadOnly = false, int skip = 0, int take = 30, CancellationToken ct = default);
    Task<int>  UnreadCountAsync(CancellationToken ct = default);
    Task<bool> MarkReadAsync(Guid id, CancellationToken ct = default);
    Task<int>  MarkAllReadAsync(CancellationToken ct = default);
}

/// <summary>
/// Client for the notification inbox.
///
/// Every method swallows transport failures and returns an empty/neutral result.
/// The bell renders inside MainLayout on EVERY page, so an API hiccup must not
/// throw there: an unhandled exception in a layout blanks the whole app, which is
/// a catastrophic response to not knowing a badge count.
/// </summary>
public sealed class NotificationApiService : INotificationApiService
{
    private readonly HttpClient _http;
    public NotificationApiService(HttpClient http) => _http = http;

    private static readonly InboxPageDto Empty = new(new List<InboxNotificationDto>(), 0, 0);

    public async Task<InboxPageDto> MineAsync(
        bool unreadOnly = false, int skip = 0, int take = 30, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<ApiResult<InboxPageDto>>(
                $"api/v1/notifications/mine?unreadOnly={unreadOnly}&skip={skip}&take={take}", ct);

            return result?.Data ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public async Task<int> UnreadCountAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<ApiResult<int>>(
                "api/v1/notifications/unread-count", ct);

            return result?.Data ?? 0;
        }
        catch
        {
            // Zero, not a cached value: a stale badge is a lie the user acts on.
            return 0;
        }
    }

    public async Task<bool> MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync($"api/v1/notifications/{id}/read", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync("api/v1/notifications/read-all", null, ct);
            if (!resp.IsSuccessStatusCode) return 0;

            var result = await resp.Content.ReadFromJsonAsync<ApiResult<int>>(cancellationToken: ct);
            return result?.Data ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
