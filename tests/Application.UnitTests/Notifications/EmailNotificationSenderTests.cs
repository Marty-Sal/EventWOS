using System.Net;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Infrastructure.Notifications.Channels;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// Email retry classification and the plain-text alternative. The text part is
/// not cosmetic: HTML-only mail is filtered as spam far more often, and an event
/// notification landing in junk is functionally an undelivered one.
/// </summary>
public class EmailNotificationSenderTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void Rate_limits_and_outages_are_transient(HttpStatusCode status)
        => EmailNotificationSender.Classify(status).Should().Be(ChannelSendOutcome.TransientFailure);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Credential_problems_are_transient_so_mail_survives_the_fix(HttpStatusCode status)
        // A rotated key or an unverified sender identity needs a human; the retry
        // window keeps notifications alive until then.
        => EmailNotificationSender.Classify(status).Should().Be(ChannelSendOutcome.TransientFailure);

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public void A_malformed_request_is_permanent(HttpStatusCode status)
        => EmailNotificationSender.Classify(status).Should().Be(ChannelSendOutcome.PermanentFailure);

    [Fact]
    public void Plain_text_keeps_the_words_and_drops_the_markup()
    {
        var html = """
            <div style="font-family:Arial">
              <p style="font-weight:600">Assignment approved</p>
              <p>Hi Asha, your assignment for <b>Sunburn</b> on 24 Aug is confirmed.</p>
            </div>
            """;

        var text = EmailNotificationSender.ToPlainText(html);

        text.Should().Contain("Assignment approved");
        text.Should().Contain("Hi Asha, your assignment for Sunburn on 24 Aug is confirmed.");
        text.Should().NotContain("<");
        text.Should().NotContain("font-family");
    }

    [Fact]
    public void Paragraphs_do_not_run_into_each_other()
    {
        var text = EmailNotificationSender.ToPlainText("<p>First line</p><p>Second line</p>");

        // Without block-boundary handling this would read "First lineSecond line".
        text.Should().Be("First line\n\nSecond line");
    }

    [Fact]
    public void Line_breaks_survive()
        => EmailNotificationSender.ToPlainText("Shift 9am<br/>Venue gate 3")
            .Should().Be("Shift 9am\nVenue gate 3");

    [Fact]
    public void Entities_are_decoded_so_names_read_correctly()
        // The renderer HTML-encodes values for email, so an apostrophe arrives as
        // &#39; and must come back out as itself in the text part.
        => EmailNotificationSender.ToPlainText("<p>D&#39;Souza &amp; Co</p>")
            .Should().Be("D'Souza & Co");

    [Fact]
    public void Empty_html_is_handled()
        => EmailNotificationSender.ToPlainText("").Should().BeEmpty();
}
