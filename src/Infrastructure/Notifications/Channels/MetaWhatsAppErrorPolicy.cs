using System.Net;
using EventOpsOracle.Application.Notifications.Abstractions;

namespace EventOpsOracle.Infrastructure.Notifications.Channels;

/// <summary>
/// Decides whether a WhatsApp failure is worth retrying. Pure and separate from
/// the senders so it can be tested without HTTP, and so the reasoning is visible
/// rather than buried in a catch block.
///
/// The distinction is not cosmetic. Retrying a permanent failure five times
/// wastes fifteen minutes before an operator learns the template was never
/// approved; treating a rate limit as permanent throws away a message that would
/// have gone out seconds later.
/// </summary>
internal static class MetaWhatsAppErrorPolicy
{
    /// <summary>
    /// Meta error codes that cannot succeed on retry: bad recipient, bad or
    /// unapproved template, malformed parameters.
    /// </summary>
    private static readonly HashSet<int> PermanentCodes = new()
    {
        100,     // invalid parameter
        131_008, // required parameter missing
        131_009, // parameter value invalid
        131_026, // message undeliverable (not a WhatsApp user / cannot receive)
        131_047, // re-engagement required -- needs an approved template, not a retry
        131_051, // unsupported message type
        132_000, // template param count mismatch
        132_001, // template does not exist
        132_005, // template hydrated text too long
        132_007, // template format policy violation
        132_012, // template parameter format mismatch
        132_015, // template is paused
        132_016, // template is disabled
        132_068, // flow is blocked
        132_069, // flow is throttled
    };

    /// <summary>
    /// Codes that mean "not now": rate limits and Meta's own transient faults.
    /// </summary>
    private static readonly HashSet<int> TransientCodes = new()
    {
        1,       // unknown API error
        2,       // temporary service problem
        4,       // application request limit reached
        80_007,  // rate limit
        130_429, // cloud API rate limit
        131_000, // generic internal error
        131_048, // spam rate limit hit
        131_056, // pair rate limit hit
        133_016, // account in maintenance
    };

    public static ChannelSendOutcome Classify(HttpStatusCode status, int? errorCode)
    {
        if (errorCode is { } code)
        {
            if (PermanentCodes.Contains(code)) return ChannelSendOutcome.PermanentFailure;
            if (TransientCodes.Contains(code)) return ChannelSendOutcome.TransientFailure;
        }

        return status switch
        {
            HttpStatusCode.TooManyRequests    => ChannelSendOutcome.TransientFailure,
            HttpStatusCode.RequestTimeout     => ChannelSendOutcome.TransientFailure,

            // Credentials rotated or revoked. Deliberately transient: a human has
            // to fix it, and the retry window keeps the message alive long enough
            // for them to do so instead of discarding it in the meantime.
            HttpStatusCode.Unauthorized       => ChannelSendOutcome.TransientFailure,
            HttpStatusCode.Forbidden          => ChannelSendOutcome.TransientFailure,

            >= HttpStatusCode.InternalServerError => ChannelSendOutcome.TransientFailure,

            // Any other 4xx is our request being wrong, and it will be just as
            // wrong next time.
            _ => ChannelSendOutcome.PermanentFailure
        };
    }
}
