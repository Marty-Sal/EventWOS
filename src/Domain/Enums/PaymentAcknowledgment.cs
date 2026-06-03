namespace EventWOS.Domain.Enums;

/// <summary>
/// Tracks whether the crew member has confirmed receipt of a Paid payment.
/// Updated by the crew member from /my-payments, not by Admin/Vendor.
/// </summary>
public enum PaymentAcknowledgment
{
    /// <summary>Default — payment is not Paid yet, or crew has not acted.</summary>
    None     = 0,

    /// <summary>Crew has confirmed they received the money.</summary>
    Received = 1,

    /// <summary>Crew says it's still pending — money not received despite Paid status.</summary>
    Pending  = 2
}
