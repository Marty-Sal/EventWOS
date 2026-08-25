using System.Net;
using System.Text.RegularExpressions;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Notifications.Rendering;

/// <summary>
/// Substitution-only template rendering: {{Token}} is replaced by a value from
/// the notification's data, and that is the entire feature set.
///
/// Deliberately not a template engine. These templates are editable by admins
/// and the values come from user-supplied data (names, reasons, event titles),
/// so supporting expressions or code would hand anyone who can edit a template
/// -- or anyone who can name an event -- a way to execute logic on the server.
/// Values are inserted, never interpreted.
///
/// For Email templates the values are HTML-encoded, because the body is HTML: a
/// crew member called "A &lt;b&gt;bold&lt;/b&gt; name" must appear as text, not
/// change the markup of the message.
/// </summary>
public sealed class NotificationTemplateRenderer : INotificationTemplateRenderer
{
    // {{Token}} with optional inner whitespace. Token names are restricted to
    // word characters so nothing resembling an expression can be addressed.
    private static readonly Regex TokenPattern =
        new(@"\{\{\s*(?<name>\w{1,40})\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public RenderedNotification Render(NotificationTemplate template, IReadOnlyDictionary<string, string?> data)
    {
        ArgumentNullException.ThrowIfNull(template);
        data ??= new Dictionary<string, string?>();

        var missing  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered  = new List<string>();
        var encode   = template.Channel == NotificationChannel.Email;

        var body = Substitute(template.Body, data, encode, missing, ordered);

        // Subject tokens are collected into the same missing-token set but not
        // into OrderedParameters: WhatsApp positional parameters follow body
        // order, and a subject only exists for email anyway.
        var subject = template.Subject is null
            ? null
            : Substitute(template.Subject, data, encode: false, missing, collect: null);

        return new RenderedNotification(subject, body, missing, ordered);
    }

    private static string Substitute(
        string input,
        IReadOnlyDictionary<string, string?> data,
        bool encode,
        ISet<string> missing,
        IList<string>? collect)
        => TokenPattern.Replace(input, match =>
        {
            var name = match.Groups["name"].Value;

            if (!TryGet(data, name, out var value) || string.IsNullOrEmpty(value))
            {
                missing.Add(name);
                // Empty rather than leaving "{{CrewName}}" in the text: a
                // recipient should never see our template syntax, even when a
                // call site forgot a value.
                collect?.Add(string.Empty);
                return string.Empty;
            }

            collect?.Add(value);
            return encode ? WebUtility.HtmlEncode(value) : value;
        });

    // Case-insensitive lookup so {{crewname}} and {{CrewName}} behave the same;
    // template authors should not have to match our C# casing exactly.
    private static bool TryGet(IReadOnlyDictionary<string, string?> data, string name, out string? value)
    {
        if (data.TryGetValue(name, out value)) return true;

        foreach (var pair in data)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
