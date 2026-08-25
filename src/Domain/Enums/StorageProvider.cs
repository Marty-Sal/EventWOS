namespace EventOpsOracle.Domain.Enums;

/// <summary>
/// Which physical backend holds a FileDocument's bytes. Recorded per-row
/// (not just globally) so a provider migration (e.g. Local → S3) can be
/// rolled out gradually without breaking reads of files uploaded under the
/// old provider.
/// </summary>
public enum StorageProvider
{
    Local = 1,
    S3Compatible = 2,   // AWS S3, Cloudflare R2, MinIO — all speak the S3 API
    AzureBlob = 3
}
