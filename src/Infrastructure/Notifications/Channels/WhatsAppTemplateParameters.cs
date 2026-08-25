using EventOpsOracle.Application.Notifications.Abstractions;

namespace EventOpsOracle.Infrastructure.Notifications.Channels;

/// <summary>
/// Works out the positional parameters a provider template expects ({{1}}, {{2}} ...).
/// Shared by both WhatsApp senders so they cannot disagree about it.
/// </summary>
internal static class WhatsAppTemplateParameters
{
    /// <summary>
    /// Prefers the template's explicit ProviderParams order. Falls back to the
    /// order tokens appear in our own body text, which is right whenever the
    /// approved provider wording mirrors ours -- true for the seeded defaults,
    /// but only a fallback, because the two are edited in different places.
    /// </summary>
    public static IReadOnlyList<string> Build(NotificationSendContext context)
    {
        var declared = context.Template.ParameterOrder();
        if (declared.Count == 0)
            return context.Message.OrderedParameters;

        var values = new List<string>(declared.Count);

        foreach (var token in declared)
        {
            context.Data.TryGetValue(token, out var value);

            // Providers reject empty parameters outright, so an absent value
            // becomes a visible placeholder rather than a rejected send. The
            // renderer has already logged the token as missing.
            values.Add(string.IsNullOrWhiteSpace(value) ? "-" : value.Trim());
        }

        return values;
    }
}
