using System.Text.RegularExpressions;

namespace EventWOS.BlazorWeb.Validation;

/// <summary>
/// Client-side mirror of the server-side validation rules (FluentValidation
/// validators in Application.Registration.Validators / FileValidationPolicy).
/// BlazorWeb is a standalone WASM client with no project reference to
/// Application/Domain, so these rules are deliberately duplicated here —
/// the server remains the single source of TRUTH (nothing here is ever
/// trusted instead of the server check), this is purely so users see the
/// same rejection instantly instead of after a round-trip.
/// KEEP IN SYNC WITH: RegisterCrewValidator, RegisterVendorValidator,
/// PasswordRules, FileValidationPolicy.
/// </summary>
public static class ClientValidation
{
    private static readonly Regex FullNamePattern = new(@"^[A-Za-z ]+$", RegexOptions.Compiled);
    private static readonly Regex UsernamePattern = new(@"^[a-zA-Z0-9_.-]+$", RegexOptions.Compiled);
    private static readonly Regex MobilePattern   = new(@"^\d{10}$", RegexOptions.Compiled);
    private static readonly Regex EmailPattern    = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex HasLetter       = new(@"[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex HasDigit        = new(@"\d", RegexOptions.Compiled);

    public const string PasswordDescription = "Min 8 chars · 1 letter · 1 number";

    public static bool IsValidFullName(string? v) => !string.IsNullOrWhiteSpace(v) && FullNamePattern.IsMatch(v);
    public static bool IsValidUsername(string? v) => !string.IsNullOrWhiteSpace(v) && v.Length is >= 3 and <= 50 && UsernamePattern.IsMatch(v);
    public static bool IsValidMobile(string? v)   => !string.IsNullOrWhiteSpace(v) && MobilePattern.IsMatch(v);
    public static bool IsValidEmail(string? v)    => !string.IsNullOrWhiteSpace(v) && EmailPattern.IsMatch(v);
    public static bool IsValidPassword(string? v) => !string.IsNullOrEmpty(v) && v.Length >= 8 && HasLetter.IsMatch(v) && HasDigit.IsMatch(v);

    public static string FilterDigits(string? raw, int maxLength)
    {
        var digitsOnly = new string((raw ?? "").Where(char.IsDigit).ToArray());
        return digitsOnly.Length > maxLength ? digitsOnly[..maxLength] : digitsOnly;
    }

    public const int MinimumAge = 18;
    public const int MaximumAge = 70; // must be strictly below this

    /// <summary>
    /// True only for a DOB that is not-in-the-future AND yields an age from
    /// 18 up to (not including) 70. Mirrors RegisterCrewValidator's
    /// DateOfBirth rule exactly. The upper bound is a real business rule,
    /// and also incidentally catches nonsensical dates — a browser date
    /// input with no `min` attribute happily accepts something like
    /// 0001-01-01, which naively computes as "age 2025+" and would sail
    /// through an age>=18-only check. Without this, the client showed a
    /// green "OK" the server then rejected.
    /// </summary>
    public static bool IsAdult(DateTime dob)
    {
        var today = DateTime.UtcNow.Date;
        if (dob.Date > today) return false;
        var age = CalculateAge(dob);
        return age >= MinimumAge && age < MaximumAge;
    }

    public static int CalculateAge(DateTime dob)
    {
        var today = DateTime.UtcNow.Date;
        var age = today.Year - dob.Year;
        if (dob.Date > today.AddYears(-age)) age--;
        return age;
    }
}

/// <summary>
/// Client-side mirror of FileValidationPolicy — checks declared Content-Type
/// AND file extension against an allow-list before ever reading/uploading
/// the file. This is what was MISSING for photo/ID-proof pickers: the
/// browser's file-dialog "accept" filter is only a UI hint and is trivially
/// bypassed (e.g. selecting "All files"), so without this check any file —
/// including something like a spreadsheet — sailed straight through to
/// "selected" with zero feedback. The server still re-validates (including
/// magic-byte signature checking) regardless of what this reports.
/// </summary>
public static class ClientFileValidation
{
    public sealed record Rule(long MaxSizeBytes, string[] AllowedContentTypes, string[] AllowedExtensions, string Description);

    private static readonly string[] ImageTypes = { "image/jpeg", "image/png", "image/webp" };
    private static readonly string[] ImageExts  = { ".jpg", ".jpeg", ".png", ".webp" };
    private static readonly string[] ImageOrPdfTypes = { "image/jpeg", "image/png", "application/pdf" };
    private static readonly string[] ImageOrPdfExts  = { ".jpg", ".jpeg", ".png", ".pdf" };

    public static readonly Rule ProfilePhoto = new(5 * 1024 * 1024, ImageTypes, ImageExts, "JPEG/PNG/WebP, up to 5MB");
    public static readonly Rule IdentificationProof = new(5 * 1024 * 1024, ImageOrPdfTypes, ImageOrPdfExts, "JPEG, PNG or PDF, up to 5MB");

    public static (bool IsValid, string? Error) Validate(Rule rule, long sizeBytes, string? contentType, string? fileName)
    {
        if (sizeBytes <= 0)
            return (false, "File is empty.");
        if (sizeBytes > rule.MaxSizeBytes)
            return (false, $"File exceeds the {rule.MaxSizeBytes / (1024 * 1024)}MB limit.");
        if (string.IsNullOrEmpty(contentType) || !rule.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            return (false, $"'{contentType}' isn't an accepted file type. Allowed: {rule.Description}.");

        var ext = System.IO.Path.GetExtension(fileName ?? "");
        if (string.IsNullOrEmpty(ext) || !rule.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return (false, $"'{ext}' isn't an accepted file extension. Allowed: {rule.Description}.");

        return (true, null);
    }
}
