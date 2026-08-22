using Blazored.LocalStorage;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Supplies a STABLE per-browser device identifier for the login calls.
///
/// Both login paths used to mint a fresh Guid.NewGuid() on every single login,
/// which meant the server saw every re-login from the very same browser as a
/// brand-new device: it created another UserSession row plus another 30-day
/// RefreshToken, and the previous pair stayed valid because nothing could ever
/// match them up again. That is what produced duplicate rows for one person on
/// the Sessions page (e.g. the same admin twice) and left rows looking "active"
/// long after that browser had been logged out.
///
/// The id lives under its own local-storage key so it deliberately SURVIVES
/// logout (MarkLoggedOutAsync only clears the token keys) -- the point is for
/// the same browser to keep identifying itself as the same device across
/// logins, so the server can supersede the previous session instead of
/// stacking a new one next to it.
/// </summary>
public sealed class DeviceIdService
{
    private const string DeviceIdKey = "ew_device";

    private readonly ILocalStorageService _storage;

    public DeviceIdService(ILocalStorageService storage) => _storage = storage;

    /// <summary>
    /// Returns this browser's device id, creating and persisting one on first
    /// use. Falls back to a transient id if local storage is unavailable
    /// (private-mode lockdowns) -- login must never fail over this.
    /// </summary>
    public async Task<string> GetOrCreateAsync()
    {
        try
        {
            var existing = await _storage.GetItemAsStringAsync(DeviceIdKey);
            if (!string.IsNullOrWhiteSpace(existing))
                return existing.Trim('"');

            var fresh = NewId();
            await _storage.SetItemAsStringAsync(DeviceIdKey, fresh);
            return fresh;
        }
        catch
        {
            return NewId();
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];
}
