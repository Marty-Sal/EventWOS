using EventOpsOracle.Application.Common;
using EventOpsOracle.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Infrastructure.Storage;

/// <summary>
/// Dev/MVP-only IFileStorage backed by local disk. NOT for production —
/// container filesystems on platforms like Railway are ephemeral, so
/// anything written here is lost on redeploy/restart. Kept intentionally
/// simple: it exists purely so the app runs end-to-end without cloud
/// credentials while developing. Switch Storage:Provider to "S3" or
/// "AzureBlob" in config for production — no code changes required
/// anywhere else.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _rootPath;
    private readonly ILogger<LocalFileStorage> _logger;

    public StorageProvider ActiveProvider => StorageProvider.Local;

    public LocalFileStorage(IConfiguration config, ILogger<LocalFileStorage> logger)
    {
        _logger = logger;
        _rootPath = config["Storage:Local:RootPath"] ?? Path.Combine(AppContext.BaseDirectory, "file-storage");
        Directory.CreateDirectory(_rootPath);
        _logger.LogWarning("LocalFileStorage active — root={Root}. Do NOT use in production; container disks are ephemeral.", _rootPath);
    }

    public async Task<string> UploadAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        content.Position = 0;
        await content.CopyToAsync(fs, ct);
        return storageKey;
    }

    public Task<Stream> DownloadAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(storageKey);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Storage object not found: {storageKey}");
        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(storageKey);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default)
        => Task.FromResult(File.Exists(ResolvePath(storageKey)));

    /// <summary>Local disk has no concept of a pre-signed URL — callers must stream through the API.</summary>
    public Task<string?> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Confines all keys under the configured root — rejects any key that
    /// would traverse outside it (defense in depth; keys are always
    /// Guid-based from FileStorageKeyBuilder, but never trust that blindly).
    /// </summary>
    private string ResolvePath(string storageKey)
    {
        var combined = Path.GetFullPath(Path.Combine(_rootPath, storageKey));
        var rootFull = Path.GetFullPath(_rootPath);
        if (!combined.StartsWith(rootFull, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid storage key path.");
        return combined;
    }
}
