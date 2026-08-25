using Microsoft.JSInterop;

namespace EventOpsOracle.BlazorWeb.Services;

/// <summary>
/// The signed-in user's profile photo, held once for the whole session and shared
/// by every avatar on screen -- the profile header and the sidebar both read it.
///
/// Why a shared service instead of each avatar fetching for itself: profile photos
/// are PRIVATE files. There is no public URL to put in an img tag, so the bytes
/// have to be pulled through the authenticated download endpoint and wrapped in a
/// blob URL. Doing that per component would re-download the photo on every
/// navigation, for every avatar, and leak a blob URL each time. One copy is
/// fetched, cached, and revoked when it is genuinely replaced.
///
/// An avatar is decoration: every failure path here falls back to the initial
/// rather than surfacing an error. A missing photo must never break a page.
/// </summary>
public sealed class AvatarState
{
    private readonly IFilesApiService _files;
    private readonly IJSRuntime _js;

    /// <summary>Serialises downloads so two avatars mounting together fetch once.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Guid? _fileId;
    private string? _objectUrl;

    public AvatarState(IFilesApiService files, IJSRuntime js)
    {
        _files = files;
        _js    = js;
    }

    /// <summary>Raised when the photo appears, changes, or goes away.</summary>
    public event Action? Changed;

    /// <summary>The FileDocument currently displayed, or null if none.</summary>
    public Guid? FileId => _fileId;

    /// <summary>Blob URL for an img src, or null when there is no photo to show.</summary>
    public string? PhotoUrl => _objectUrl;

    /// <summary>
    /// Point the shared avatar at a photo id, downloading it if it is not already
    /// on screen. Safe and cheap to call on every page load or navigation: the
    /// same id with a loaded photo is a no-op, which is what keeps the sidebar
    /// avatar from re-fetching all session.
    /// </summary>
    /// <param name="force">
    /// Re-download even when the id is unchanged -- used right after an upload.
    /// </param>
    public async Task SyncAsync(Guid? fileId, bool force = false)
    {
        await _gate.WaitAsync();
        try
        {
            var alreadyShowing = fileId == _fileId && (fileId is null || _objectUrl is not null);
            if (alreadyShowing && !force) return;

            await RevokeAsync();
            _fileId = fileId;

            if (fileId is null) return;

            var (ok, bytes, contentType, _) = await _files.DownloadAsync(fileId.Value);
            if (!ok || bytes is null || bytes.Length == 0)
            {
                // Metadata row without a usable object (the ephemeral-disk era left
                // a few of these). Fall back to the initial rather than showing a
                // broken image icon.
                return;
            }

            _objectUrl = await _js.InvokeAsync<string>(
                "createBlobUrlFromBytes",
                string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType,
                Convert.ToBase64String(bytes));
        }
        catch
        {
            // Decoration must never take a page down.
            _objectUrl = null;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Drops the photo -- used on sign-out so the next user never sees it.</summary>
    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await RevokeAsync();
            _fileId = null;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Blob URLs pin their bytes in memory until revoked, so the old one goes
    /// before a new one is created.
    /// </summary>
    private async Task RevokeAsync()
    {
        if (_objectUrl is null) return;

        var url = _objectUrl;
        _objectUrl = null;
        try { await _js.InvokeVoidAsync("revokeBlobUrl", url); }
        catch { /* page tearing down -- nothing to salvage */ }
    }
}
