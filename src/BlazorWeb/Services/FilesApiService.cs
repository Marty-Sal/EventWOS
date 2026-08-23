using System.Net.Http.Json;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// DocumentType values mirror EventWOS.Domain.Enums.DocumentType on the API —
/// kept as plain ints here (for outgoing upload requests only, where
/// ASP.NET Core's [FromForm] enum binder happily accepts a numeric
/// string) so the Blazor client doesn't need a Domain reference.
///
/// NOTE: incoming JSON (FileDocumentDto.DocumentType below) is a STRING,
/// not one of these ints — the API's JsonStringEnumConverter serializes
/// every enum by name (e.g. "VendorProfilePhoto"), so Label() below
/// matches on the enum's name, not these numeric constants.
/// </summary>
public static class DocumentTypes
{
    public const int CrewProfilePhoto = 1;
    public const int CrewIdentificationProof = 2;
    public const int VendorDocument = 3;
    public const int EventDocument = 4;
    public const int VendorProfilePhoto = 5;

    /// <summary>Short human label for the "View details" modal file list.</summary>
    public static string Label(string documentType) => documentType switch
    {
        nameof(CrewProfilePhoto)        => "Profile photo",
        nameof(CrewIdentificationProof) => "ID proof",
        nameof(VendorDocument)          => "Vendor document",
        nameof(EventDocument)           => "Event document",
        nameof(VendorProfilePhoto)      => "Profile photo",
        _ => "Document"
    };
}

public sealed record FileDocumentDto(
    Guid Id, Guid OwnerId, Guid? EntityId, string DocumentType,
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
                // Prefer the API's own message: "The file could not be found in
                // storage." is the one failure a user can actually act on (the
                // document needs re-uploading), and hiding it behind a generic
                // "could not be downloaded" is what made this look like a dead
                // button rather than a missing file.
                string? serverMessage = null;
                try
                {
                    var body = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(cancellationToken: ct);
                    serverMessage = body?.Errors?.FirstOrDefault();
                }
                catch { /* non-JSON body (404 from the pipeline, HTML error page) */ }

                var err = serverMessage
                       ?? resp.StatusCode switch
                          {
                              System.Net.HttpStatusCode.Forbidden    => "You don't have permission to view this file.",
                              System.Net.HttpStatusCode.Unauthorized => "Your session expired — sign in again to view this file.",
                              System.Net.HttpStatusCode.NotFound     => "This file is no longer available.",
                              _                                      => "File could not be downloaded."
                          };
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
