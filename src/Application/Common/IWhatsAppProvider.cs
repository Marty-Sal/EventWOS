namespace EventWOS.Application.Common;

/// <summary>
/// WhatsApp dispatch abstraction — mirrors ISmsProvider. Lives in
/// Application so handlers can depend on it directly. Infrastructure
/// provides the concrete implementation: StubWhatsAppProvider for
/// dev/until credentials are configured, or WhatsAppCloudApiProvider
/// (Meta WhatsApp Cloud API) once WHATSAPP_ACCESS_TOKEN + WHATSAPP_PHONE_NUMBER_ID
/// are set.
/// </summary>
public interface IWhatsAppProvider
{
    Task<bool> SendAsync(string mobile, string message, CancellationToken ct = default);
}
