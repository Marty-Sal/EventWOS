using EventWOS.Domain.Entities;

namespace EventWOS.Application.Notifications.Rendering;

/// <summary>Renders a stored template against a notification's placeholder data.</summary>
public interface INotificationTemplateRenderer
{
    RenderedNotification Render(NotificationTemplate template, IReadOnlyDictionary<string, string?> data);
}

/// <param name="Subject">Rendered subject, or null for channels without one.</param>
/// <param name="Body">Rendered body.</param>
/// <param name="MissingTokens">
/// Tokens the template asked for that the data did not supply. Rendering still
/// succeeds -- a half-filled message beats no message -- but these are logged so
/// a template referencing a token nobody populates gets noticed.
/// </param>
/// <param name="OrderedParameters">
/// The token values in the order they appear in the body. WhatsApp templates are
/// approved provider-side and accept positional parameters only, so this is what
/// the AiSensy provider sends.
/// </param>
public sealed record RenderedNotification(
    string? Subject,
    string Body,
    IReadOnlyCollection<string> MissingTokens,
    IReadOnlyList<string> OrderedParameters);
