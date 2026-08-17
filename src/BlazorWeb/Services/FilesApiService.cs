using System.Net.Http.Json;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// DocumentType values mirror EventWOS.Domain.Enums.DocumentType on the API —
/// kept as plain ints here so the Blazor client doesn't need a Domain reference.
/// </summary>
public static class DocumentTypes
{
    public const int CrewProfilePhoto = 1;
    public const int CrewIdentificationProof = 2;
    public const int VendorDocument = 3;
    public const int EventDocument = 4;
}

public sealed record FileDocumentDto(
    Guid Id, Guid OwnerId, Guid? EntityId, int DocumentType,
    string OriginalFileName, string ContentType, long FileSizeBytes,
    bool HasThumbnail, DateTime UploadedAt);

public interface IFilesApiService
{
    /// <summary>Uploads a file for the current user (or, with files:manage, for another owner via ownerId).</summary>
    Task<(bool Ok, FileDocumentDto? File, string? Error)> UploadAsync(
        byte[] content, string fileName, string contentType, int documentType,
        Guid? ownerId = null, Guid? entityId = null, CancellationToken ct = default);

    /// <summary>Downloads a file's bytes — caller must be the owner or hold the right admin permission (enforced server-side).</summary>
    Task<(bool Ok, byte[]? Content, string? ContentType, string? Error)> DownloadAsync(Guid fileId, CancellationToken ct = default);

    Task<(bool Ok, string? Error)> DeleteAsync(Guid fileId, CancellationToken ct = default);
}

public sealed class FilesApiService : IFilesApiService
{
    private readonly HttpClient _http;
    public FilesApiService(HttpClient http) => _http = http;

    public async Task<(bool Ok, FileDocumentDto? File, string? Error)> UploadAsync(
        byte[] content, string fileName, string contentType, int documentType,
        Guid? ownerId = null, Guid? entityId = null, CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(content);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(fileContent, "File", fileName);
            form.Add(new StringContent(documentType.ToString()), "DocumentType");
            if (ownerId.HasValue) form.Add(new StringContent(ownerId.Value.ToString()), "OwnerId");
            if (entityId.HasValue) form.Add(new StringContent(entityId.Value.ToString()), "EntityId");

            var resp = await _http.PostAsync("api/v1/files/upload", form, ct);
            var result = await resp.Content.ReadFromJsonAsync<ApiResult<FileDocumentDto>>(cancellationToken: ct);

            if (resp.IsSuccessStatusCode && result?.Data is not null)
                return (true, result.Data, null);

            return (false, null, result?.Errors?.FirstOrDefault() ?? result?.Message ?? "Upload failed.");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public async Task<(bool Ok, byte[]? Content, string? ContentType, string? Error)> DownloadAsync(Guid fileId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"api/v1/files/{fileId}/download", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "You don't have permission to view this file."
                    : "File could not be downloaded.";
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

    public async Task<(bool Ok, string? Error)> DeleteAsync(Guid fileId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"api/v1/files/{fileId}", ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var result = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(cancellationToken: ct);
            return (false, result?.Errors?.FirstOrDefault() ?? "Delete failed.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
