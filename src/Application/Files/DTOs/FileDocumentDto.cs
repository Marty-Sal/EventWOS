using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Files.DTOs;

/// <summary>
/// Client-facing shape for a FileDocument. Deliberately omits StorageKey —
/// the storage layout is an internal implementation detail and must never
/// reach the browser (per "do not store/expose permanent public URLs").
/// Clients fetch bytes via GET /files/{Id}/download, not via any stored URL.
/// </summary>
public sealed record FileDocumentDto(
    Guid Id,
    Guid OwnerId,
    Guid? EntityId,
    DocumentType DocumentType,
    string OriginalFileName,
    string ContentType,
    long FileSizeBytes,
    bool HasThumbnail,
    DateTime UploadedAt);
