using System.Security.Cryptography;
using System.Text;

namespace EventOpsOracle.Infrastructure.Notifications.Webhooks;

/// <summary>
/// Signature verification for inbound provider webhooks.
///
/// These endpoints have to be anonymous -- SendGrid and Meta cannot hold a JWT --
/// and they mutate delivery state. Unverified, anyone who learned the URL could
/// mark a crew member's shift alert as delivered, or bounce it, and the audit
/// trail would repeat the lie. So the signature IS the authentication.
///
/// Pure functions, no HTTP types, so the crypto can be tested directly.
/// </summary>
public static class WebhookSignature
{
    /// <summary>
    /// Meta / AiSensy style: HMAC-SHA256 over the raw body, hex encoded, sent as
    /// "sha256=...". Compared in fixed time -- a byte-by-byte comparison leaks
    /// how much of a forged signature was right.
    /// </summary>
    public static bool VerifyHmacSha256(string rawBody, string? header, string? secret)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(secret)) return false;

        var provided = header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? header["sha256=".Length..]
            : header;

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));

            var providedBytes = Convert.FromHexString(provided.Trim());

            return CryptographicOperations.FixedTimeEquals(computed, providedBytes);
        }
        catch (FormatException)
        {
            // Not valid hex -- a malformed or hand-crafted header.
            return false;
        }
    }

    /// <summary>
    /// SendGrid's signed event webhook: ECDSA over (timestamp + raw body), with
    /// the verification key published in their UI as base64 DER.
    ///
    /// The timestamp is part of the signed payload, which is what stops a captured
    /// request being replayed later -- so it must be included exactly as sent.
    /// </summary>
    public static bool VerifyEcdsa(string rawBody, string? signature, string? timestamp, string? publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(signature) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            var payload = Encoding.UTF8.GetBytes(timestamp + rawBody);
            var sig     = Convert.FromBase64String(signature);

            // SendGrid emits a DER sequence, not the raw r||s pair.
            return ecdsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
