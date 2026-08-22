using System.Net.Http.Json;

namespace EventWOS.BlazorWeb.Services;

/// <summary>Audience values mirror EventWOS.Domain.Enums.AnnouncementAudience on the API.</summary>
public static class AnnouncementAudiences
{
    public const int Vendors = 1;
    public const int Crew    = 2;
    public const int Both    = 3;

    /// <summary>Incoming JSON serializes the enum by NAME (JsonStringEnumConverter), so match on names.</summary>
    public static string Label(string audience) => audience switch
    {
        "Vendors" => "Vendors only",
        "Crew"    => "Crew only",
        "Both"    => "Vendors & Crew",
        _         => audience
    };
}

public sealed record AnnouncementAttachmentDto(
    Guid FileId, string FileName, string ContentType, long FileSizeBytes, bool IsViewableInline);

public sealed record EventAnnouncementDto(
    Guid Id, Guid EventId, string EventTitle, DateTime EventStartAt,
    string Audience, string Subject, string BodyHtml,
    string SentByName, DateTime SentAt,
    int RecipientCount, int WhatsAppSentCount, bool IsRead,
    List<AnnouncementAttachmentDto> Attachments);

public sealed record SendAnnouncementResultDto(
    Guid AnnouncementId, int RecipientCount, int WhatsAppSentCount, int AttachmentCount);

public interface IAnnouncementApiService
{
    Task<(bool Ok, SendAnnouncementResultDto? Result, string? Error)> SendAsync(
        Guid eventId, int audience, string subject, string bodyHtml,
        IReadOnlyList<Guid> attachmentFileIds, CancellationToken ct = default);

    Task<List<EventAnnouncementDto>> ForEventAsync(Guid eventId, CancellationToken ct = default);
    Task<List<EventAnnouncementDto>> MineAsync(int take = 50, CancellationToken ct = default);
    Task<bool> MarkReadAsync(Guid announcementId, CancellationToken ct = default);

    /// <summary>Fetches an attachment's bytes so the UI can open it in a new tab (see window.openFileInNewTab).</summary>
    Task<(bool Ok, byte[]? Content, string? ContentType, string? Error)> DownloadAttachmentAsync(
        Guid announcementId, Guid fileId, CancellationToken ct = default);
}

public sealed class AnnouncementApiService : IAnnouncementApiService
{
    private readonly HttpClient _http;
    public AnnouncementApiService(HttpClient http) => _http = http;

    public async Task<(bool Ok, SendAnnouncementResultDto? Result, string? Error)> SendAsync(
        Guid eventId, int audience, string subject, string bodyHtml,
        IReadOnlyList<Guid> attachmentFileIds, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                audience,
                subject,
                bodyHtml,
                attachmentFileIds = attachmentFileIds.ToList()
            };
            var resp = await _http.PostAsJsonAsync($"api/v1/events/{eventId}/announcements", payload, ct);
            var result = await resp.Content.ReadFromJsonAsync<ApiResult<SendAnnouncementResultDto>>(cancellationToken: ct);

            if (resp.IsSuccessStatusCode && result?.Data is not null)
                return (true, result.Data, null);

            return (false, null, result?.Errors?.FirstOrDefault() ?? result?.Message ?? "Could not send the notification.");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public async Task<List<EventAnnouncementDto>> ForEventAsync(Guid eventId, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<ApiResult<List<EventAnnouncementDto>>>(
                $"api/v1/events/{eventId}/announcements", ct);
            return result?.Data ?? new List<EventAnnouncementDto>();
        }
        catch
        {
            return new List<EventAnnouncementDto>();
        }
    }

    public async Task<List<EventAnnouncementDto>> MineAsync(int take = 50, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<ApiResult<List<EventAnnouncementDto>>>(
                $"api/v1/announcements/mine?take={take}", ct);
            return result?.Data ?? new List<EventAnnouncementDto>();
        }
        catch
        {
            return new List<EventAnnouncementDto>();
        }
    }

    public async Task<bool> MarkReadAsync(Guid announcementId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync($"api/v1/announcements/{announcementId}/read", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Ok, byte[]? Content, string? ContentType, string? Error)> DownloadAttachmentAsync(
        Guid announcementId, Guid fileId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"api/v1/announcements/{announcementId}/attachments/{fileId}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "You don't have access to this attachment."
                    : "The attachment could not be opened.";
                return (false, null, null, err);
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return (true, bytes, resp.Content.Headers.ContentType?.MediaType, null);
        }
        catch (Exception ex)
        {
            return (false, null, null, ex.Message);
        }
    }
}
