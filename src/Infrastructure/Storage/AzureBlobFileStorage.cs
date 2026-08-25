using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using EventOpsOracle.Application.Common;
using EventOpsOracle.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Infrastructure.Storage;

/// <summary>
/// Object-storage IFileStorage backed by Azure Blob Storage. Selected via
/// Storage:Provider=AzureBlob. Business/handler code is unaffected by this
/// choice — only Program.cs DI registration changes.
/// </summary>
public sealed class AzureBlobFileStorage : IFileStorage
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<AzureBlobFileStorage> _logger;

    public StorageProvider ActiveProvider => StorageProvider.AzureBlob;

    public AzureBlobFileStorage(IConfiguration config, ILogger<AzureBlobFileStorage> logger)
    {
        _logger = logger;
        var connectionString = config["Storage:AzureBlob:ConnectionString"]
            ?? throw new InvalidOperationException("Storage:AzureBlob:ConnectionString is required when Storage:Provider=AzureBlob.");
        var containerName = config["Storage:AzureBlob:ContainerName"] ?? "eventwos-files";

        var serviceClient = new BlobServiceClient(connectionString);
        _container = serviceClient.GetBlobContainerClient(containerName);
        _container.CreateIfNotExists(PublicAccessType.None); // private by default — no public access

        _logger.LogInformation("AzureBlobFileStorage active — container={Container}", containerName);
    }

    public async Task<string> UploadAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default)
    {
        content.Position = 0;
        var blob = _container.GetBlobClient(storageKey);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Conditions = null // overwrite allowed — matches IFileStorage contract
        }, ct);
        return storageKey;
    }

    public async Task<Stream> DownloadAsync(string storageKey, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(storageKey);
        if (!await blob.ExistsAsync(ct))
            throw new FileNotFoundException($"Storage object not found: {storageKey}");
        var resp = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return resp.Value.Content;
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(storageKey);
        await blob.DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(storageKey);
        return await blob.ExistsAsync(ct);
    }

    public Task<string?> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(storageKey);
        if (!blob.CanGenerateSasUri)
        {
            _logger.LogWarning("AzureBlobFileStorage cannot generate a SAS URI with the current credential type — falling back to API streaming.");
            return Task.FromResult<string?>(null);
        }
        var sasUri = blob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(expiry));
        return Task.FromResult<string?>(sasUri.ToString());
    }
}
