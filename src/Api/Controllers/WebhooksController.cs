using System.Text.Json;
using EventOpsOracle.Application.Notifications.Commands;
using EventOpsOracle.Infrastructure.Notifications.Webhooks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Api.Controllers;

/// <summary>
/// Inbound provider callbacks: what actually happened to a message after we
/// handed it over. Without these, every email and WhatsApp delivery sits at
/// Accepted forever and "did the crew get the cancellation?" has no answer.
///
/// Necessarily anonymous -- SendGrid and Meta cannot present a JWT -- so the
/// SIGNATURE is the authentication. An unverified endpoint here would let anyone
/// who learned the URL mark a shift alert as delivered, or bounce it, and the
/// audit trail would faithfully record the lie.
///
/// Every action returns 200 for anything it merely does not recognise. Providers
/// disable endpoints that keep erroring, and losing the whole feed because of one
/// unfamiliar event type would be far worse than ignoring that event.
/// </summary>
[ApiController]
[Route("api/v1/webhooks")]
[AllowAnonymous]
public sealed class WebhooksController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly WebhookOptions _options;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(IMediator mediator, IOptions<WebhookOptions> options, ILogger<WebhooksController> logger)
    {
        _mediator = mediator;
        _options  = options.Value;
        _logger   = logger;
    }

    /// <summary>SendGrid signed event webhook. Batched array of events.</summary>
    [HttpPost("sendgrid")]
    public async Task<IActionResult> SendGrid(CancellationToken ct)
    {
        var raw = await ReadRawBodyAsync();

        var signature = Request.Headers["X-Twilio-Email-Event-Webhook-Signature"].FirstOrDefault();
        var timestamp = Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"].FirstOrDefault();

        if (!WebhookSignature.VerifyEcdsa(raw, signature, timestamp, _options.SendGridPublicKey) && !_options.AllowUnsigned)
        {
            _logger.LogWarning("Rejected SendGrid webhook: signature verification failed");
            return Unauthorized();
        }

        var events = SendGridWebhookParser.Parse(raw, _logger);
        if (events.Count == 0) return Ok(new { applied = 0 });

        var result = await _mediator.Send(new RecordProviderDeliveryEventsCommand("SendGrid", events), ct);
        return Ok(new { applied = result.Value });
    }

    /// <summary>
    /// Meta's subscription handshake. Meta calls this once with a challenge and
    /// will not deliver events until the exact value is echoed back as plain text.
    /// </summary>
    [HttpGet("whatsapp")]
    public IActionResult VerifyWhatsApp(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" &&
            !string.IsNullOrWhiteSpace(_options.MetaVerifyToken) &&
            token == _options.MetaVerifyToken)
        {
            _logger.LogInformation("Meta WhatsApp webhook subscription verified");
            return Content(challenge ?? string.Empty, "text/plain");
        }

        _logger.LogWarning("Rejected Meta WhatsApp webhook verification: token mismatch");
        return Forbid();
    }

    /// <summary>Meta WhatsApp status callbacks: sent, delivered, read, failed.</summary>
    [HttpPost("whatsapp")]
    public async Task<IActionResult> WhatsApp(CancellationToken ct)
    {
        var raw = await ReadRawBodyAsync();

        var signature = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();

        if (!WebhookSignature.VerifyHmacSha256(raw, signature, _options.MetaAppSecret) && !_options.AllowUnsigned)
        {
            _logger.LogWarning("Rejected Meta WhatsApp webhook: signature verification failed");
            return Unauthorized();
        }

        var events = MetaWhatsAppWebhookParser.Parse(raw, _logger);
        if (events.Count == 0) return Ok(new { applied = 0 });

        var result = await _mediator.Send(new RecordProviderDeliveryEventsCommand("MetaCloud", events), ct);
        return Ok(new { applied = result.Value });
    }

    /// <summary>
    /// AiSensy callbacks. They do not sign requests, so a shared secret in a
    /// header or query string is the available option -- weaker than a signature,
    /// and the reason correlation rides on our own delivery id rather than
    /// anything the caller could invent.
    /// </summary>
    [HttpPost("aisensy")]
    public async Task<IActionResult> AiSensy([FromQuery(Name = "token")] string? queryToken, CancellationToken ct)
    {
        var raw = await ReadRawBodyAsync();

        // Both header names are accepted. X-OpsOracle-Token is the name going forward;
        // X-EventWOS-Token is what any webhook already configured in a provider's
        // dashboard is sending today, and a rename in our code must not silently start
        // rejecting live callbacks -- the failure would look like WhatsApp receipts
        // simply stopping.
        var provided = Request.Headers["X-OpsOracle-Token"].FirstOrDefault()
                       ?? Request.Headers["X-EventWOS-Token"].FirstOrDefault()
                       ?? queryToken;
        var expected = _options.AiSensySecret;

        var authorized = !string.IsNullOrWhiteSpace(expected) &&
                         !string.IsNullOrWhiteSpace(provided) &&
                         System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                             System.Text.Encoding.UTF8.GetBytes(provided),
                             System.Text.Encoding.UTF8.GetBytes(expected));

        if (!authorized && !_options.AllowUnsigned)
        {
            _logger.LogWarning("Rejected AiSensy webhook: shared secret mismatch");
            return Unauthorized();
        }

        var events = AiSensyWebhookParser.Parse(raw, _logger);
        if (events.Count == 0) return Ok(new { applied = 0 });

        var result = await _mediator.Send(new RecordProviderDeliveryEventsCommand("AiSensy", events), ct);
        return Ok(new { applied = result.Value });
    }

    /// <summary>
    /// The signature covers the bytes exactly as sent, so the body has to be read
    /// raw -- a deserialized-and-reserialized copy would not verify.
    /// </summary>
    private async Task<string> ReadRawBodyAsync()
    {
        Request.EnableBuffering();
        Request.Body.Position = 0;

        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();

        Request.Body.Position = 0;
        return body;
    }
}
