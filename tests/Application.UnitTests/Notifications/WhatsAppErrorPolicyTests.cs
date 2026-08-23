using System.Net;
using EventWOS.Application.Notifications.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Retry classification for both WhatsApp providers. Getting this wrong is
/// expensive in both directions: retrying an unapproved template wastes fifteen
/// minutes before anyone finds out, and failing a rate limit throws away a
/// message that would have gone out seconds later.
///
/// The policies are internal implementation details, reached here through
/// InternalsVisibleTo rather than being made public just for tests.
/// </summary>
public class WhatsAppErrorPolicyTests
{
    private static ChannelSendOutcome Meta(HttpStatusCode status, int? code)
        => EventWOS.Infrastructure.Notifications.Channels.MetaWhatsAppErrorPolicy.Classify(status, code);

    private static ChannelSendOutcome AiSensy(HttpStatusCode status, string body)
        => EventWOS.Infrastructure.Notifications.Channels.AiSensyWhatsAppSender.Classify(status, body);

    [Theory]
    [InlineData(131_026)] // not a WhatsApp user
    [InlineData(132_001)] // template does not exist
    [InlineData(132_000)] // wrong number of parameters
    [InlineData(132_015)] // template paused
    [InlineData(131_047)] // needs an approved template, not a retry
    public void Meta_configuration_and_recipient_errors_are_permanent(int code)
        => Meta(HttpStatusCode.BadRequest, code).Should().Be(ChannelSendOutcome.PermanentFailure);

    [Theory]
    [InlineData(130_429)] // cloud API rate limit
    [InlineData(131_048)] // spam rate limit
    [InlineData(131_000)] // generic internal error
    [InlineData(2)]       // temporary service problem
    public void Meta_rate_limits_and_internal_faults_are_transient(int code)
        => Meta(HttpStatusCode.BadRequest, code).Should().Be(ChannelSendOutcome.TransientFailure);

    [Fact]
    public void Meta_server_errors_are_transient_even_without_a_code()
        => Meta(HttpStatusCode.BadGateway, null).Should().Be(ChannelSendOutcome.TransientFailure);

    [Fact]
    public void An_expired_token_is_transient_so_the_message_survives_the_fix()
    {
        // Someone has to rotate the credential; the retry window buys them time
        // instead of discarding notifications in the meantime.
        Meta(HttpStatusCode.Unauthorized, null).Should().Be(ChannelSendOutcome.TransientFailure);
        AiSensy(HttpStatusCode.Unauthorized, "invalid api key").Should().Be(ChannelSendOutcome.TransientFailure);
    }

    [Fact]
    public void Meta_unrecognised_client_errors_are_permanent()
        // A request that is malformed now will be just as malformed on retry.
        => Meta(HttpStatusCode.BadRequest, null).Should().Be(ChannelSendOutcome.PermanentFailure);

    [Theory]
    [InlineData("{\"message\":\"Campaign does not exist\"}")]
    [InlineData("{\"message\":\"Template not approved\"}")]
    [InlineData("{\"message\":\"Invalid destination\"}")]
    [InlineData("{\"message\":\"templateParams parameter count mismatch\"}")]
    public void AiSensy_configuration_errors_are_permanent(string body)
        => AiSensy(HttpStatusCode.BadRequest, body).Should().Be(ChannelSendOutcome.PermanentFailure);

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"message\":\"rate limited\"}")]
    [InlineData(HttpStatusCode.InternalServerError, "server error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "maintenance")]
    public void AiSensy_rate_limits_and_outages_are_transient(HttpStatusCode status, string body)
        => AiSensy(status, body).Should().Be(ChannelSendOutcome.TransientFailure);

    [Fact]
    public void AiSensy_gives_an_unfamiliar_error_the_benefit_of_the_doubt()
        // Better to retry a few times than to silently drop a real notification
        // because the provider worded something unexpectedly.
        => AiSensy(HttpStatusCode.BadRequest, "{\"message\":\"something odd happened\"}")
            .Should().Be(ChannelSendOutcome.TransientFailure);
}
