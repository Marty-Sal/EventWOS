/*
 * EventWOS — PWA install banner helpers
 * -------------------------------------
 * Thin, well-behaved JS glue between Blazor and the browser's install
 * primitives. Blazor doesn't have first-class access to
 * beforeinstallprompt or the display-mode media query, so we expose
 * a tiny API on window.eventwosPwaInstall that MainLayout.razor
 * (via IJSRuntime) can call.
 *
 * Design principles:
 *   1. Never ask twice in the same session. Never ask again for 7 days
 *      after a user dismisses. Never ask if already installed.
 *   2. Distinguish three states explicitly — installable (Android/
 *      desktop Chrome, we have a prompt in hand), ios-hint (iOS
 *      Safari, we have to teach the user the Share > Add to Home
 *      Screen gesture), and hidden (installed / dismissed recently /
 *      not eligible). The Razor component picks its UI based on
 *      which of these getState() returns.
 *   3. All persistence is in localStorage so the choice survives
 *      across sessions AND across service-worker updates (which wipe
 *      Cache Storage but leave localStorage alone).
 */

(function () {
    const LS_DISMISSED_AT = 'eventwos.pwa.installBannerDismissedAt';
    const LS_INSTALLED_AT = 'eventwos.pwa.installedAt';
    const RESHOW_AFTER_MS = 7 * 24 * 60 * 60 * 1000; // one week

    // ── Utility: are we running as an installed PWA right now? ─────
    // Two independent signals — iOS uses navigator.standalone (a
    // proprietary flag), everyone else uses the display-mode media
    // query. If either says "yes, we're standalone", we're installed
    // and the banner has nothing to offer.
    function isStandalone() {
        try {
            if (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) {
                return true;
            }
        } catch { /* SSR / very old browser — ignore */ }
        // iOS Safari's proprietary flag. TypeScript's dom.d.ts doesn't
        // declare this, so we test via bracket access.
        if (window.navigator && window.navigator['standalone'] === true) return true;
        return false;
    }

    // ── Utility: iOS detection for the manual-instruction fallback ─
    // We only need this for the "iOS Safari can install PWAs but there's
    // no beforeinstallprompt event, so we have to TEACH the user how"
    // path. We check both the platform AND that we're really in Safari
    // (not Chrome-on-iOS, which shares the iOS chrome but doesn't
    // support home-screen install the same way).
    function isIosSafari() {
        const ua = window.navigator.userAgent || '';
        const isIOS = /iPad|iPhone|iPod/.test(ua) && !window.MSStream;
        // Chrome on iOS: "CriOS"; Firefox on iOS: "FxiOS"; Edge on iOS: "EdgiOS"
        // Safari on iOS: has "Safari" but NOT any of the above prefixes.
        const isSafari = /Safari/.test(ua) && !/CriOS|FxiOS|EdgiOS|OPiOS/.test(ua);
        return isIOS && isSafari;
    }

    function wasDismissedRecently() {
        const raw = window.localStorage.getItem(LS_DISMISSED_AT);
        if (!raw) return false;
        const at = parseInt(raw, 10);
        if (!Number.isFinite(at)) return false;
        return (Date.now() - at) < RESHOW_AFTER_MS;
    }

    // ── Public API ─────────────────────────────────────────────────
    // Returns one of:
    //   'installed'    — already running as PWA; never show anything
    //   'dismissed'    — user hit × recently; hold off for a week
    //   'installable'  — Android/desktop path, we have a prompt to fire
    //   'ios-hint'     — iOS Safari, no prompt available; show manual
    //                    Share → Add to Home Screen instructions
    //   'not-eligible' — no prompt in hand and not iOS-Safari; nothing
    //                    to do. Includes desktop Firefox and iOS-Chrome.
    function getState() {
        if (isStandalone())                 return 'installed';
        if (wasDismissedRecently())         return 'dismissed';
        if (window.__eventwosInstallPrompt) return 'installable';
        if (isIosSafari())                  return 'ios-hint';
        return 'not-eligible';
    }

    // Fire the native prompt if we have one. Returns the user's
    // choice ('accepted' | 'dismissed') or null if no prompt was
    // available. Also clears the cached prompt object either way
    // (Chrome only lets you call .prompt() once per event instance).
    async function promptInstall() {
        const p = window.__eventwosInstallPrompt;
        if (!p) return null;
        try {
            await p.prompt();
            const choice = await p.userChoice;
            window.__eventwosInstallPrompt = null;
            // If the user accepted, we won't see the "appinstalled"
            // event fire until Chrome actually completes install —
            // record the timestamp now so we don't flash the banner
            // in the meantime.
            if (choice && choice.outcome === 'accepted') {
                window.localStorage.setItem(LS_INSTALLED_AT, Date.now().toString());
            } else {
                // Treat a rejected native prompt like a dismissal —
                // don't hound the user again this week.
                window.localStorage.setItem(LS_DISMISSED_AT, Date.now().toString());
            }
            return choice ? choice.outcome : null;
        } catch (err) {
            console.warn('[pwa-install] prompt() failed:', err);
            return null;
        }
    }

    function dismiss() {
        window.localStorage.setItem(LS_DISMISSED_AT, Date.now().toString());
    }

    // Convenience — allow the app to force-clear the dismissal (e.g.
    // a dev debug button, or if we want to re-nag after a big update).
    function reset() {
        window.localStorage.removeItem(LS_DISMISSED_AT);
    }

    // Fire a synthetic event when Chrome finalises the install so the
    // Razor component can hide the banner immediately — otherwise it
    // would linger until the next page render.
    window.addEventListener('appinstalled', () => {
        window.localStorage.setItem(LS_INSTALLED_AT, Date.now().toString());
        window.dispatchEvent(new CustomEvent('pwa-installed-now'));
    });

    // Bind a .NET object reference so the Razor <PwaInstallBanner />
    // component gets notified when the browser fires
    // 'pwa-installable' (Chrome sometimes delivers the
    // beforeinstallprompt several seconds after page load, so a
    // one-shot check on mount would miss it) or
    // 'pwa-installed-now' (the user just accepted install; hide the
    // banner without waiting for a navigation).
    //
    // Guarded by __eventwosPwaBannerBound so re-mounts of the Razor
    // component (which can happen on route change) don't stack up
    // duplicate event listeners.
    function bindDotnet(dotnet) {
        if (window.__eventwosPwaBannerBound) return;
        window.__eventwosPwaBannerBound = true;
        const notify = () => dotnet.invokeMethodAsync('OnPwaEvent');
        window.addEventListener('pwa-installable',   notify);
        window.addEventListener('pwa-installed-now', notify);
    }

    window.eventwosPwaInstall = {
        getState,
        promptInstall,
        dismiss,
        reset,
        isIosSafari,   // exposed for the Razor component's UI logic
        bindDotnet,
    };
})();
