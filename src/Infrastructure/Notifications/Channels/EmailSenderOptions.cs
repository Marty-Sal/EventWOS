namespace EventOpsOracle.Infrastructure.Notifications.Channels;

/// <summary>
/// Email configuration for the notification pipeline. Shares the existing
/// SendGrid credentials rather than introducing a second set, so there is one
/// place to rotate a key.
/// </summary>
public sealed class EmailSenderOptions
{
    public const string SectionName = "SendGrid";

    public string? ApiKey { get; set; }
    public string? FromEmail { get; set; }
    public string FromName { get; set; } = "OpsOracle";

    /// <summary>
    /// SendGrid validates and accepts the request but sends nothing. Useful for
    /// proving the pipeline end to end against real templates and real
    /// recipients without emailing crew during a test.
    /// </summary>
    public bool SandboxMode { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromEmail);
}
