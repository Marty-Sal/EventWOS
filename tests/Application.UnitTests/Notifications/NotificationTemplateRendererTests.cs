using EventWOS.Application.Notifications.Rendering;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Covers template rendering, which sits between admin-editable text and
/// user-supplied data -- the two least trustworthy inputs in the system.
/// </summary>
public class NotificationTemplateRendererTests
{
    private readonly NotificationTemplateRenderer _renderer = new();

    private static NotificationTemplate Template(
        string body, NotificationChannel channel = NotificationChannel.WhatsApp, string? subject = null)
        => new("CREW_ASSIGNMENT", channel, body, subject);

    [Fact]
    public void Substitutes_tokens_from_data()
    {
        var result = _renderer.Render(
            Template("Hi {{CrewName}}, you are on {{EventName}} on {{EventDate}}."),
            new Dictionary<string, string?>
            {
                ["CrewName"]  = "Asha",
                ["EventName"] = "Sunburn Goa",
                ["EventDate"] = "12 Sep 2026",
            });

        result.Body.Should().Be("Hi Asha, you are on Sunburn Goa on 12 Sep 2026.");
        result.MissingTokens.Should().BeEmpty();
    }

    [Fact]
    public void Token_matching_is_case_and_whitespace_insensitive()
    {
        // Template authors should not have to match our C# casing exactly.
        var result = _renderer.Render(
            Template("Hi {{ crewname }}!"),
            new Dictionary<string, string?> { ["CrewName"] = "Asha" });

        result.Body.Should().Be("Hi Asha!");
    }

    [Fact]
    public void Missing_values_are_reported_and_never_leak_template_syntax()
    {
        var result = _renderer.Render(
            Template("Hi {{CrewName}}, venue is {{VenueName}}."),
            new Dictionary<string, string?> { ["CrewName"] = "Asha" });

        // A recipient must never see "{{VenueName}}" in a real message.
        result.Body.Should().Be("Hi Asha, venue is .");
        result.MissingTokens.Should().Contain("VenueName");
    }

    [Fact]
    public void Email_bodies_html_encode_values_so_data_cannot_alter_markup()
    {
        var result = _renderer.Render(
            Template("<p>Hi {{CrewName}}</p>", NotificationChannel.Email),
            new Dictionary<string, string?> { ["CrewName"] = "<b>bold</b> <script>alert(1)</script>" });

        result.Body.Should().NotContain("<script>");
        result.Body.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void WhatsApp_bodies_are_not_html_encoded()
    {
        // WhatsApp is plain text; encoding would show literal "&amp;" to the user.
        var result = _renderer.Render(
            Template("Hi {{CrewName}}"),
            new Dictionary<string, string?> { ["CrewName"] = "Ram & Co" });

        result.Body.Should().Be("Hi Ram & Co");
    }

    [Fact]
    public void Does_not_evaluate_anything_that_looks_like_an_expression()
    {
        // Substitution only. Anything richer would be a template-injection
        // surface, since both the template and the values are user-controlled.
        var result = _renderer.Render(
            Template("A {{2+2}} B {{ System.DateTime.Now }} C {{CrewName}}"),
            new Dictionary<string, string?> { ["CrewName"] = "Asha" });

        result.Body.Should().Be("A {{2+2}} B {{ System.DateTime.Now }} C Asha");
    }

    [Fact]
    public void Injected_token_syntax_in_a_value_is_not_expanded_recursively()
    {
        // Someone naming an event "{{Otp}}" must not be able to read another token.
        var result = _renderer.Render(
            Template("Event: {{EventName}}"),
            new Dictionary<string, string?> { ["EventName"] = "{{Otp}}", ["Otp"] = "123456" });

        result.Body.Should().Be("Event: {{Otp}}");
        result.Body.Should().NotContain("123456");
    }

    [Fact]
    public void Ordered_parameters_follow_body_order_for_positional_whatsapp_templates()
    {
        var result = _renderer.Render(
            Template("{{EventName}} on {{EventDate}} at {{VenueName}}"),
            new Dictionary<string, string?>
            {
                ["VenueName"] = "NSCI Dome",
                ["EventName"] = "Sunburn",
                ["EventDate"] = "12 Sep",
            });

        result.OrderedParameters.Should().Equal("Sunburn", "12 Sep", "NSCI Dome");
    }

    [Fact]
    public void Subject_is_rendered_for_email_templates()
    {
        var result = _renderer.Render(
            Template("<p>body</p>", NotificationChannel.Email, "You are assigned to {{EventName}}"),
            new Dictionary<string, string?> { ["EventName"] = "Sunburn" });

        result.Subject.Should().Be("You are assigned to Sunburn");
    }
}
