using EventWOS.Application.Common;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace EventWOS.Infrastructure.Storage;

/// <summary>
/// Re-encodes uploaded images down to a sane max dimension + JPEG quality
/// and generates a square thumbnail, so we never store an unnecessarily
/// large original (per spec: "avoid storing unnecessarily large images").
/// </summary>
public sealed class ImageSharpProcessor : IImageProcessor
{
    private const int MaxDimension = 1600;   // optimized "full-size" cap
    private const int ThumbnailSize = 256;   // square thumbnail
    private const int JpegQuality = 82;
    private readonly ILogger<ImageSharpProcessor> _logger;

    public ImageSharpProcessor(ILogger<ImageSharpProcessor> logger) => _logger = logger;

    public async Task<ProcessedImage> OptimizeAsync(Stream input, CancellationToken ct = default)
    {
        input.Position = 0;
        using var image = await Image.LoadAsync(input, ct);

        // Optimized full-size copy — downscale only if larger than the cap; never upscale.
        using var optimizedImage = image.Clone(ctx =>
        {
            if (image.Width > MaxDimension || image.Height > MaxDimension)
                ctx.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(MaxDimension, MaxDimension) });
        });
        var optimizedStream = new MemoryStream();
        await optimizedImage.SaveAsync(optimizedStream, new JpegEncoder { Quality = JpegQuality }, ct);
        optimizedStream.Position = 0;

        // Square thumbnail — center-cropped so it looks right in avatar circles/grids.
        using var thumbImage = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Crop,
            Size = new Size(ThumbnailSize, ThumbnailSize)
        }));
        var thumbStream = new MemoryStream();
        await thumbImage.SaveAsync(thumbStream, new JpegEncoder { Quality = JpegQuality }, ct);
        thumbStream.Position = 0;

        _logger.LogInformation("Optimized image {OrigW}x{OrigH} -> {NewW}x{NewH} + {Thumb}x{Thumb} thumbnail",
            image.Width, image.Height, optimizedImage.Width, optimizedImage.Height, ThumbnailSize, ThumbnailSize);

        return new ProcessedImage(optimizedStream, "image/jpeg", thumbStream, "image/jpeg");
    }
}
