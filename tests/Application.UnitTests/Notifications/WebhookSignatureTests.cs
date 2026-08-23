using System.Security.Cryptography;
using System.Text;
using EventWOS.Infrastructure.Notifications.Webhooks;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Signature verification for the webhook endpoints.
///
/// These endpoints are anonymous -- providers cannot present a JWT -- and they
/// mutate delivery state, so the signature IS the authentication. If it can be
/// bypassed, anyone who learns the URL can mark a crew member's shift alert as
/// delivered, or bounce it, and the audit trail will record the lie as fact.
/// </summary>
public class WebhookSignatureTests
{
    private const string Body = """[{"event":"delivered","sg_message_id":"abc123"}]""";

    private static string Hmac(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void Hmac_accepts_a_correct_signature()
        => WebhookSignature.VerifyHmacSha256(Body, "sha256=" + Hmac(Body, "app-secret"), "app-secret")
            .Should().BeTrue();

    [Fact]
    public void Hmac_accepts_the_signature_without_the_prefix()
        // Meta sends "sha256=..."; other providers send bare hex.
        => WebhookSignature.VerifyHmacSha256(Body, Hmac(Body, "app-secret"), "app-secret")
            .Should().BeTrue();

    [Fact]
    public void Hmac_rejects_a_tampered_body()
    {
        // The exact scenario that matters: a valid signature replayed against an
        // altered payload, e.g. flipping a failure into a delivery.
        var signature = "sha256=" + Hmac(Body, "app-secret");
        var tampered  = Body.Replace("delivered", "bounce");

        WebhookSignature.VerifyHmacSha256(tampered, signature, "app-secret").Should().BeFalse();
    }

    [Fact]
    public void Hmac_rejects_the_wrong_secret()
        => WebhookSignature.VerifyHmacSha256(Body, "sha256=" + Hmac(Body, "attacker-guess"), "app-secret")
            .Should().BeFalse();

    [Theory]
    [InlineData(null, "app-secret")]
    [InlineData("", "app-secret")]
    [InlineData("sha256=zzz-not-hex", "app-secret")]
    [InlineData("sha256=abcd", "app-secret")]
    public void Hmac_rejects_missing_or_malformed_headers(string? header, string secret)
        => WebhookSignature.VerifyHmacSha256(Body, header, secret).Should().BeFalse();

    [Fact]
    public void Hmac_rejects_everything_when_no_secret_is_configured()
        // Fails closed. An unconfigured secret must never mean "accept anything".
        => WebhookSignature.VerifyHmacSha256(Body, "sha256=" + Hmac(Body, "x"), null)
            .Should().BeFalse();

    [Fact]
    public void Ecdsa_accepts_a_correctly_signed_payload()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        var timestamp = "1787000000";
        var signature = Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(timestamp + Body),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

        WebhookSignature.VerifyEcdsa(Body, signature, timestamp, publicKey).Should().BeTrue();
    }

    [Fact]
    public void Ecdsa_rejects_a_replay_with_a_different_timestamp()
    {
        // The timestamp is inside the signed payload precisely so a captured
        // request cannot be replayed later.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        var signature = Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes("1787000000" + Body),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

        WebhookSignature.VerifyEcdsa(Body, signature, "1787009999", publicKey).Should().BeFalse();
    }

    [Fact]
    public void Ecdsa_rejects_a_signature_from_a_different_key()
    {
        using var real     = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var timestamp = "1787000000";
        var forged = Convert.ToBase64String(attacker.SignData(
            Encoding.UTF8.GetBytes(timestamp + Body),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

        WebhookSignature.VerifyEcdsa(Body, forged, timestamp, Convert.ToBase64String(real.ExportSubjectPublicKeyInfo()))
            .Should().BeFalse();
    }

    [Fact]
    public void Ecdsa_rejects_junk_without_throwing()
    {
        // Malformed base64 or a garbage key must be a rejection, not a 500 -- a
        // crashing endpoint is one a provider eventually disables.
        WebhookSignature.VerifyEcdsa(Body, "not-base64!", "1787000000", "also-not-base64").Should().BeFalse();
        WebhookSignature.VerifyEcdsa(Body, null, null, null).Should().BeFalse();
    }
}
