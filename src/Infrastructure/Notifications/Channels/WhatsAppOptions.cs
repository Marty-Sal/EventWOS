namespace EventWOS.Infrastructure.Notifications.Channels;

/// <summary>
/// WhatsApp configuration, bound from the "WhatsApp" section. Which provider is
/// live is a config decision, not a code one, so deliverability between AiSensy
/// and Meta can be compared on the same build.
/// </summary>
public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>"AiSensy", "Meta", or "None" to switch the channel off entirely.</summary>
    public string Provider { get; set; } = "None";

    /// <summary>Prefixed onto bare local numbers. India by default.</summary>
    public string DefaultCountryCode { get; set; } = "91";

    public AiSensyOptions AiSensy { get; set; } = new();
    public MetaWhatsAppOptions Meta { get; set; } = new();

    public bool IsAiSensy => Provider.Equals("AiSensy", StringComparison.OrdinalIgnoreCase);
    public bool IsMeta    => Provider.Equals("Meta", StringComparison.OrdinalIgnoreCase);
}

public sealed class AiSensyOptions
{
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://backend.aisensy.com";

    /// <summary>
    /// Campaign used when a template has no ProviderTemplateId of its own.
    /// Optional: without it, such templates are skipped rather than sent through
    /// a campaign that was approved for different wording.
    /// </summary>
    public string? DefaultCampaign { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class MetaWhatsAppOptions
{
    public string? AccessToken { get; set; }
    public string? PhoneNumberId { get; set; }
    public string GraphVersion { get; set; } = "v19.0";

    /// <summary>Language pack of the approved templates, e.g. "en" or "en_US".</summary>
    public string TemplateLanguage { get; set; } = "en";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(PhoneNumberId);
}
