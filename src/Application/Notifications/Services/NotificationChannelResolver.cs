using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Notifications.Services;

/// <summary>A recipient's contact details, snapshotted when deliveries are created.</summary>
public sealed record NotificationRecipient(Guid UserId, string FullName, string? Email, string? Mobile);

/// <summary>
/// Decides which channels a notification actually goes out on, and where.
///
/// Four gates, in order, each of which can only narrow the set:
///   1. policy      -- is this type of notification worth this channel at all
///   2. templates   -- is there an ACTIVE template for that code+channel
///   3. senders     -- is that channel's provider actually configured
///   4. recipient   -- does this person have the contact detail it needs
///                     (in-app and push need none -- see RequiresDestination)
///
/// Gate 2 is what lets an admin turn a channel off for one notification type by
/// deactivating its template, with no deployment. Gate 3 is why a missing
/// AiSensy key degrades to in-app instead of queueing messages that can only
/// fail. Gate 4 is why crew with no email address quietly get WhatsApp only,
/// rather than accumulating permanent failures nobody wants to read.
/// </summary>
public sealed class NotificationChannelResolver
{
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationChannelSender> _senders;

    public NotificationChannelResolver(IEnumerable<INotificationChannelSender> senders)
    {
        // Last registration wins per channel, which is how config picks between
        // two implementations of the same channel (AiSensy vs Meta, SES vs SendGrid).
        _senders = senders
            .GroupBy(s => s.Channel)
            .ToDictionary(g => g.Key, g => g.Last());
    }

    public IReadOnlyList<ResolvedChannel> Resolve(
        string templateCode,
        NotificationRecipient recipient,
        IReadOnlyDictionary<NotificationChannel, NotificationTemplate> templates,
        IReadOnlyCollection<NotificationChannel>? requestedChannels)
    {
        var candidates = requestedChannels is { Count: > 0 }
            ? requestedChannels
            : NotificationPolicy.DefaultChannels(templateCode);

        var resolved = new List<ResolvedChannel>(candidates.Count);

        foreach (var channel in candidates.Distinct())
        {
            if (!templates.TryGetValue(channel, out var template)) continue;
            if (!_senders.TryGetValue(channel, out var sender) || !sender.IsConfigured) continue;

            var destination = DestinationFor(channel, recipient);
            if (destination is null && RequiresDestination(channel)) continue;

            resolved.Add(new ResolvedChannel(channel, destination, sender.ProviderName, template));
        }

        return resolved;
    }

    /// <summary>
    /// Which channels need a contact detail before they can be attempted.
    ///
    /// In-app is delivered inside our own system, so it needs no address.
    ///
    /// Push needs no address EITHER, and for a more interesting reason: a person
    /// has no single push address. They have zero or more browser subscriptions,
    /// each of which can die without warning, and the live set is only known at
    /// send time -- which is why a Push delivery row addresses the USER and
    /// PushNotificationSender fans out across device_registrations itself.
    ///
    /// Getting this wrong is why push produced nothing at all: Push fell through
    /// to the default branch of DestinationFor, got back null, and was silently
    /// dropped here on every single notification.
    /// </summary>
    private static bool RequiresDestination(NotificationChannel channel)
        => channel is not (NotificationChannel.InApp or NotificationChannel.Push);

    private static string? DestinationFor(NotificationChannel channel, NotificationRecipient recipient)
        => channel switch
        {
            NotificationChannel.Email    => string.IsNullOrWhiteSpace(recipient.Email) ? null : recipient.Email.Trim(),
            NotificationChannel.WhatsApp => string.IsNullOrWhiteSpace(recipient.Mobile) ? null : recipient.Mobile.Trim(),
            NotificationChannel.Sms      => string.IsNullOrWhiteSpace(recipient.Mobile) ? null : recipient.Mobile.Trim(),

            // InApp and Push both land here on purpose: neither addresses a
            // contact detail. See RequiresDestination.
            _ => null
        };
}

public sealed record ResolvedChannel(
    NotificationChannel Channel,
    string? Destination,
    string ProviderName,
    NotificationTemplate Template);
