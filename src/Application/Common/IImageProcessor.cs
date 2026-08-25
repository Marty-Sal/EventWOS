namespace EventOpsOracle.Application.Common;

/// <summary>Result of optimizing an uploaded image for storage.</summary>
public sealed record ProcessedImage(
    Stream Optimized, string OptimizedContentType,
    Stream? Thumbnail, string? ThumbnailContentType);

/// <summary>
/// Image optimization abstraction, kept separate from IFileStorage so
/// storage backends stay dumb byte-movers. Used today for CrewProfilePhoto
/// (re-encode + generate a thumbnail); available for any future image
/// document type without touching storage or upload-handler code.
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// Re-encodes the input as an optimized JPEG (capped dimensions + quality)
    /// and produces a small square thumbnail. Non-image content types should
    /// never reach this — callers gate on DocumentType first.
    /// </summary>
    Task<ProcessedImage> OptimizeAsync(Stream input, CancellationToken ct = default);
}
