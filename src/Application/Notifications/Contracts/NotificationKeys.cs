using System.Security.Cryptography;
using System.Text;

namespace EventOpsOracle.Application.Notifications.Contracts;

/// <summary>
/// Builds the idempotency key stored on notifications.idempotency_key and
/// protected by a unique index.
///
/// The key is (business event, template, recipient): the same business fact, the
/// same notification type, the same person. Channel is deliberately NOT part of
/// it -- a notification fans out to several channels and they are one logical
/// message, kept unique per channel by the delivery table's own constraint.
/// </summary>
public static class NotificationKeys
{
    /// <summary>Matches the idempotency_key column width.</summary>
    public const int MaxLength = 200;

    public static string Build(string businessEventKey, string templateCode, Guid recipientUserId)
    {
        if (string.IsNullOrWhiteSpace(businessEventKey))
            throw new ArgumentException("BusinessEventKey is required.", nameof(businessEventKey));
        if (string.IsNullOrWhiteSpace(templateCode))
            throw new ArgumentException("TemplateCode is required.", nameof(templateCode));

        var key = $"{businessEventKey.Trim()}|{templateCode.Trim().ToUpperInvariant()}|{recipientUserId:N}";

        // Long keys are hashed rather than truncated: truncation would make two
        // different business events share a key and silently swallow the second
        // notification, which is the exact failure this class exists to prevent.
        if (key.Length <= MaxLength) return key;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var prefix = key[..(MaxLength - digest.Length - 1)];
        return $"{prefix}#{digest}";
    }
}
