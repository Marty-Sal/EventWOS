using System.Net.Http.Headers;
using System.Net.Http.Json;
using EventOpsOracle.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Infrastructure.Notifications;

/// <summary>
/// Dev-mode WhatsApp "sender" — logs the message instead of dispatching.
/// Active whenever WHATSAPP_ACCESS_TOKEN/WHATSAPP_PHONE_NUMBER_ID are
/// missing from configuration, so the app boots fine without credentials
/// (identical pattern to StubSmsProvider/StubEmailService).
/// </summary>
public sealed class StubWhatsAppProvider : IWhatsAppProvider
{
    private readonly ILogger<StubWhatsAppProvider> _logger;
    public StubWhatsAppProvider(ILogger<StubWhatsAppProvider> logger) => _logger = logger;

    public Task<bool> SendAsync(string mobile, string message, CancellationToken ct = default)
    {
        _logger.LogInformation("💬 [STUB WHATSAPP] To: {Mobile} | Message: {Message}", mobile, message);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Meta WhatsApp Cloud API sender. Activated automatically when
/// WHATSAPP_ACCESS_TOKEN + WHATSAPP_PHONE_NUMBER_ID are present in
/// configuration; otherwise StubWhatsAppProvider is registered instead.
///
/// NOTE — Meta's 24-hour session rule: a plain free-form text message
/// (what this sends) only delivers if the recipient has messaged your
/// WhatsApp Business number within the last 24h, OR you're inside a
/// sandbox/test setup. For true business-initiated first contact in
/// production, Meta requires a pre-approved message *template* instead of
/// free text — swap the payload below for a "template" type once you've
/// created and approved one in Meta Business Manager.
/// </summary>
public sealed class WhatsAppCloudApiProvider : IWhatsAppProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<WhatsAppCloudApiProvider> _logger;
    private readonly string _phoneNumberId;
    private readonly string _defaultCountryCode;

    public WhatsAppCloudApiProvider(IConfiguration cfg, HttpClient http, ILogger<WhatsAppCloudApiProvider> logger)
    {
        _http = http;
        _logger = logger;
        var accessToken = cfg["WhatsApp:AccessToken"] ?? cfg["WHATSAPP_ACCESS_TOKEN"]
            ?? throw new InvalidOperationException("WhatsApp:AccessToken missing.");
        _phoneNumberId = cfg["WhatsApp:PhoneNumberId"] ?? cfg["WHATSAPP_PHONE_NUMBER_ID"]
            ?? throw new InvalidOperationException("WhatsApp:PhoneNumberId missing.");
        _defaultCountryCode = cfg["WhatsApp:DefaultCountryCode"] ?? cfg["WHATSAPP_DEFAULT_COUNTRY_CODE"] ?? "91";

        _http.BaseAddress = new Uri("https://graph.facebook.com/v19.0/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public async Task<bool> SendAsync(string mobile, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mobile))
        {
            _logger.LogWarning("WhatsApp send skipped — empty recipient.");
            return false;
        }

        var digits = new string(mobile.Where(char.IsDigit).ToArray());
        // App stores plain 10-digit mobiles with no country code — prefix the
        // configured default (India = "91") when it looks like a bare local number.
        var toNumber = digits.Length == 10 ? _defaultCountryCode + digits : digits;

        var payload = new
        {
            messaging_product = "whatsapp",
            to = toNumber,
            type = "text",
            text = new { body = message }
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync($"{_phoneNumberId}/messages", payload, ct);
            if (resp.IsSuccessStatusCode) return true;

            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("WhatsApp Cloud API send failed: {Status} {Body}", resp.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp Cloud API request threw an exception.");
            return false;
        }
    }
}
