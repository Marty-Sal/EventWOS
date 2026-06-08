using System.Text.RegularExpressions;

namespace EventWOS.Application.Registration.Validators;

/// <summary>
/// Single source of truth for password rules. Used by registration,
/// reset, and setup validators so we never drift across flows.
///   - min 8 chars
///   - at least one letter
///   - at least one digit
/// </summary>
public static class PasswordRules
{
    public const string Description = "Password must be at least 8 characters and contain at least one letter and one number.";
    private static readonly Regex HasLetter = new(@"[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex HasDigit  = new(@"\d",      RegexOptions.Compiled);

    public static bool IsValid(string? pw) =>
        !string.IsNullOrEmpty(pw)
        && pw.Length >= 8
        && HasLetter.IsMatch(pw)
        && HasDigit.IsMatch(pw);
}
