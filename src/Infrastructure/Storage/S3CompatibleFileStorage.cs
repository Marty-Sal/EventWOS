using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using EventWOS.Application.Common;
using EventWOS.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EventWOS.Infrastructure.Storage;

/// <summary>
/// Object-storage IFileStorage for any S3-API-compatible backend — AWS S3,
/// Cloudflare R2, or MinIO — chosen by which config keys are set:
///   - AWS S3: Storage:S3:Region (+ AccessKey/SecretKey or an attached IAM role)
///   - Cloudflare R2 / MinIO: Storage:S3:ServiceUrl (custom endpoint) + credentials
/// Business/handler code never sees any of this — it only calls IFileStorage.
/// </summary>
public sealed class S3CompatibleFileStorage : IFileStorage
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly ILogger<S3CompatibleFileStorage> _logger;

    public StorageProvider ActiveProvider => StorageProvider.S3Compatible;

    public S3CompatibleFileStorage(IConfiguration config, ILogger<S3CompatibleFileStorage> logger)
    {
        _logger = logger;
        _bucket = config["Storage:S3:BucketName"]
            ?? throw new InvalidOperationException("Storage:S3:BucketName is required when Storage:Provider=S3.");

        var accessKey  = config["Storage:S3:AccessKey"];
        var secretKey  = config["Storage:S3:SecretKey"];
        var serviceUrl = config["Storage:S3:ServiceUrl"];   // set for R2 / MinIO; omit for real AWS S3
        var region     = config["Storage:S3:Region"] ?? "us-east-1";

        var s3Config = new AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region) };
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            // R2 / MinIO: force path-style addressing and point at the custom endpoint.
            s3Config.ServiceURL   = serviceUrl;
            s3Config.ForcePathStyle = true;
        }

        _client = !string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey)
            ? new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), s3Config)
            : new AmazonS3Client(s3Config); // falls back to the default credential chain (IAM role, env vars, etc.)

        _logger.LogInformation("S3CompatibleFileStorage active — bucket={Bucket} endpoint={Endpoint}", _bucket, serviceUrl ?? "aws-default");
    }

    public async Task<string> UploadAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default)
    {
        content.Position = 0;
        var req = new PutObjectRequest
        {
            BucketName  = _bucket,
            Key         = storageKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
            // Objects are private by default (no ACL set) — access only via our API + pre-signed URLs.
        };
        await _client.PutObjectAsync(req, ct);
        return storageKey;
    }

    public async Task<Stream> DownloadAsync(string storageKey, CancellationToken ct = default)
    {
        try
        {
            var resp = await _client.GetObjectAsync(_bucket, storageKey, ct);
            return resp.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Storage object not found: {storageKey}", ex);
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        await _client.DeleteObjectAsync(_bucket, storageKey, ct);
    }

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, storageKey, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task<string?> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct = default)
    {
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key        = storageKey,
            Expires    = DateTime.UtcNow.Add(expiry),
            Verb       = HttpVerb.GET
        });
        return Task.FromResult<string?>(url);
    }
}
