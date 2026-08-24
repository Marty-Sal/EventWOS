/*
 * EventWOS push subscription interop
 * ----------------------------------
 * The browser half of Web Push. The service worker (wwwroot/sw.js)
 * receives and displays notifications; this file is only about getting
 * permission and handing the resulting subscription to the API.
 *
 * Every function is defensive on purpose. Push is genuinely absent or
 * half-implemented across the devices event crew actually use -- old
 * Android WebViews, iOS Safari before 16.4, iOS Safari 16.4+ that has
 * NOT been added to the home screen, and desktop browsers with
 * notifications blocked at the OS level. Each of those must produce a
 * clear reason, never an exception, so the UI can explain itself
 * instead of showing a toggle that does nothing.
 */

window.eventwosPush = (() => {

    // ---- capability -------------------------------------------------

    function isStandalone() {
        return window.matchMedia?.('(display-mode: standalone)')?.matches === true
            || window.navigator.standalone === true;
    }

    function isIos() {
        const ua = navigator.userAgent || '';
        return /iPad|iPhone|iPod/.test(ua)
            // iPadOS 13+ reports itself as a Mac; the touch points give it away.
            || (ua.includes('Macintosh') && navigator.maxTouchPoints > 1);
    }

    /**
     * Why push is unavailable, or null when it is available.
     * The strings are shown to users, so they say what to DO.
     */
    function unsupportedReason() {
        if (!('serviceWorker' in navigator))
            return 'This browser does not support background notifications.';

        if (!('PushManager' in window)) {
            // The single most common confusion on iPhone: Safari supports
            // push from 16.4, but ONLY for an installed PWA.
            return isIos() && !isStandalone()
                ? 'On iPhone and iPad, tap Share then "Add to Home Screen" first, then open EventWOS from the home screen icon.'
                : 'This browser does not support push notifications.';
        }

        if (!('Notification' in window))
            return 'This browser does not support notifications.';

        if (isIos() && !isStandalone())
            return 'On iPhone and iPad, tap Share then "Add to Home Screen" first, then open EventWOS from the home screen icon.';

        return null;
    }

    // ---- state ------------------------------------------------------

    /**
     * A single snapshot for the UI: is it possible, is it allowed, is it on.
     * One call rather than four, so the toggle cannot render a mix of
     * stale answers.
     */
    async function getStatus() {
        const reason = unsupportedReason();
        if (reason) {
            return { supported: false, reason, permission: 'unsupported', subscribed: false, endpoint: null };
        }

        const permission = Notification.permission;
        let endpoint = null;

        try {
            const reg = await navigator.serviceWorker.ready;
            const existing = await reg.pushManager.getSubscription();
            endpoint = existing?.endpoint ?? null;
        } catch {
            // A worker that has not activated yet is not an error state.
        }

        return {
            supported: true,
            reason: null,
            permission,                       // 'default' | 'granted' | 'denied'
            subscribed: endpoint !== null,
            endpoint
        };
    }

    // ---- subscribe --------------------------------------------------

    /**
     * Asks permission if needed and returns the subscription in the exact
     * shape the API expects. Returns { ok: false, reason } rather than
     * throwing, so a refusal is an outcome the UI can render.
     *
     * MUST be called from a user gesture: browsers ignore -- and Safari
     * penalises -- a permission prompt that nobody asked for.
     */
    async function subscribe(vapidPublicKey) {
        const reason = unsupportedReason();
        if (reason) return { ok: false, reason };

        if (!vapidPublicKey) return { ok: false, reason: 'Push is not configured on the server.' };

        if (Notification.permission === 'denied') {
            // Once denied, only the user can undo it in browser settings;
            // asking again is a no-op that looks like a broken button.
            return { ok: false, reason: 'Notifications are blocked for this site. Enable them in your browser settings, then try again.' };
        }

        if (Notification.permission === 'default') {
            const granted = await Notification.requestPermission();
            if (granted !== 'granted') return { ok: false, reason: 'Notification permission was not granted.' };
        }

        try {
            const reg = await navigator.serviceWorker.ready;

            // Reuse an existing subscription when the key still matches.
            // Re-subscribing needlessly changes the endpoint and orphans
            // the row we already have on the server.
            let sub = await reg.pushManager.getSubscription();

            if (sub && !keyMatches(sub, vapidPublicKey)) {
                // The server's VAPID key was rotated. The old subscription
                // can never be pushed to again, so replace it.
                await sub.unsubscribe();
                sub = null;
            }

            if (!sub) {
                sub = await reg.pushManager.subscribe({
                    // Required by Chrome: a subscription that can push
                    // silently would be a tracking vector, so every push
                    // must show something.
                    userVisibleOnly: true,
                    applicationServerKey: urlBase64ToUint8Array(vapidPublicKey)
                });
            }

            const json = sub.toJSON();

            // Reaching here means push is wanted on this browser, whether the
            // caller was the toggle or the automatic path.
            setOptedOut(false);

            return {
                ok: true,
                endpoint: json.endpoint,
                p256dh: json.keys?.p256dh ?? null,
                auth: json.keys?.auth ?? null,
                platform: describePlatform()
            };
        } catch (err) {
            // AbortError here is usually a missing/incorrect VAPID key or a
            // push service the device cannot reach.
            return { ok: false, reason: `Could not subscribe to notifications (${err?.name || 'error'}).` };
        }
    }

    /**
     * Drops the browser-side subscription and returns the endpoint that was
     * removed, so the caller can tell the API which row to retire.
     */
    async function unsubscribe() {
        try {
            const reg = await navigator.serviceWorker.ready;
            const sub = await reg.pushManager.getSubscription();
            if (!sub) { setOptedOut(true); return { ok: true, endpoint: null }; }

            const endpoint = sub.endpoint;
            await sub.unsubscribe();

            // Remembered so the automatic path does not undo this on the next
            // page load. Only turning the toggle back on clears it.
            setOptedOut(true);

            return { ok: true, endpoint };
        } catch (err) {
            return { ok: false, reason: `Could not unsubscribe (${err?.name || 'error'}).` };
        }
    }

    // ---- badge ------------------------------------------------------

    /** Mirrors the unread count onto the app icon where the OS supports it. */
    async function setBadge(count) {
        try {
            if (count > 0 && navigator.setAppBadge) await navigator.setAppBadge(count);
            else if (navigator.clearAppBadge)       await navigator.clearAppBadge();
        } catch { /* unsupported on most desktops; never fatal */ }
    }

    // ---- helpers ----------------------------------------------------

    /**
     * The VAPID public key is base64url; subscribe() needs raw bytes.
     * A mismatch here fails as an opaque AbortError, which is why this is
     * its own function rather than an inline one-liner.
     */
    function urlBase64ToUint8Array(base64Url) {
        const padding = '='.repeat((4 - (base64Url.length % 4)) % 4);
        const base64  = (base64Url + padding).replace(/-/g, '+').replace(/_/g, '/');
        const raw     = window.atob(base64);
        const bytes   = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        return bytes;
    }

    function keyMatches(subscription, vapidPublicKey) {
        try {
            const applied = subscription.options?.applicationServerKey;
            if (!applied) return true;   // cannot tell; assume fine
            const a = new Uint8Array(applied);
            const b = urlBase64ToUint8Array(vapidPublicKey);
            if (a.length !== b.length) return false;
            return a.every((v, i) => v === b[i]);
        } catch {
            return true;
        }
    }

    function describePlatform() {
        const ua = navigator.userAgent || '';
        if (/iPhone/i.test(ua)) return 'iPhone';
        if (/iPad/i.test(ua))   return 'iPad';
        if (/Android/i.test(ua)) return 'Android';
        if (/Windows/i.test(ua)) return 'Windows';
        if (/Mac OS/i.test(ua))  return 'Mac';
        return null;
    }

    // ---- auto-enable ------------------------------------------------
    // "On by default", as far as a browser permits it.
    //
    // What CANNOT be done: silently granting notification permission. That
    // decision belongs to the OS prompt and no site can pre-answer it -- if it
    // could, every site would. So there is no way to make a brand-new device
    // start subscribed.
    //
    // What CAN be done, and is what this does: once permission exists, never
    // make the user find the toggle again. Reinstalled the PWA, cleared site
    // data, got a new subscription after a browser update, switched devices on
    // the same account -- in all of those the permission survives but the
    // subscription does not, and the old behaviour left the user sitting there
    // with notifications quietly off. This re-subscribes silently instead.
    //
    // No prompt is ever triggered from here: permission is checked first, so a
    // user who has not been asked, or who said no, sees nothing.
    async function autoEnable(publicKey) {
        if (unsupportedReason()) return { ok: false, reason: 'unsupported' };

        // Checked BEFORE anything else so this can never trigger a prompt. A
        // user who has not been asked, or who said no, sees nothing at all.
        if (Notification.permission !== 'granted') return { ok: false, reason: 'not-granted' };
        if (!publicKey) return { ok: false, reason: 'no-key' };
        if (isOptedOut()) return { ok: false, reason: 'opted-out' };

        // Delegated rather than reimplemented: subscribe() already reuses a live
        // subscription, replaces one signed with a rotated key, and returns the
        // shape the caller registers with the API. With permission already
        // granted it cannot prompt, so there is nothing to be careful about here.
        return await subscribe(publicKey);
    }

    // ---- always on ---------------------------------------------------
    // Notifications are meant to be ON for everyone, so the app asks instead of
    // waiting to be found in a settings page nobody visits.
    //
    // The one thing that cannot be automated is the OS permission itself: no site
    // can pre-answer that dialog. So this asks for it, which is the closest thing
    // to "on by default" that exists on the web.
    //
    // Asking has a cost, and it is not politeness -- it is mechanical. Chrome
    // treats repeatedly-dismissed prompts as a block signal and will eventually
    // refuse to show ours at all, permanently, with no way for us to recover it.
    // So the asking is rationed: at most MAX_ASKS times, never twice within
    // ASK_COOLDOWN_MS. A dismissal is not a refusal (people dismiss because they
    // are mid-task) but three of them are, and after that only the toggle asks.
    // An explicit OFF has to survive a reload, or the toggle is a liar: revoking
    // the browser subscription leaves PERMISSION granted, so without this the
    // next page load would helpfully re-subscribe the person who just opted out.
    // Deliberately per-browser rather than per-account -- "not on this laptop" is
    // the actual intent, and it is the browser that will be buzzing.
    const OPT_OUT_KEY     = 'eventwos.push.optOut';
    const ASK_LOG_KEY     = 'eventwos.push.asked';
    const MAX_ASKS        = 3;
    const ASK_COOLDOWN_MS = 3 * 24 * 60 * 60 * 1000;


    function isOptedOut() {
        try { return localStorage.getItem(OPT_OUT_KEY) === '1'; } catch { return false; }
    }

    function setOptedOut(value) {
        try {
            if (value) localStorage.setItem(OPT_OUT_KEY, '1');
            else       localStorage.removeItem(OPT_OUT_KEY);
        } catch { /* private mode: honoured for this session only */ }
    }

    function askLog() {
        try { return JSON.parse(localStorage.getItem(ASK_LOG_KEY)) || { count: 0, last: 0 }; }
        catch { return { count: 0, last: 0 }; }
    }

    function recordAsk() {
        try {
            const log = askLog();
            localStorage.setItem(ASK_LOG_KEY, JSON.stringify({ count: log.count + 1, last: Date.now() }));
        } catch { /* private mode: asking once per page load is still better than never */ }
    }

    function mayAsk() {
        const log = askLog();
        return log.count < MAX_ASKS && (Date.now() - log.last) > ASK_COOLDOWN_MS;
    }

    /**
     * Gets this device subscribed, asking for permission if that is still open.
     * Safe to call on every page load.
     */
    async function ensureEnabled(publicKey) {
        if (unsupportedReason()) return { ok: false, reason: 'unsupported' };
        if (!publicKey)          return { ok: false, reason: 'no-key' };
        if (isOptedOut())        return { ok: false, reason: 'opted-out' };

        // Already answered. Granted re-subscribes silently (this is what heals a
        // reinstalled PWA); denied is the user's decision and is left alone --
        // re-asking is impossible anyway, only browser settings can undo it.
        if (Notification.permission === 'granted') return await subscribe(publicKey);
        if (Notification.permission === 'denied')  return { ok: false, reason: 'denied' };

        if (!mayAsk()) return { ok: false, reason: 'cooling-off' };

        try {
            recordAsk();
            const granted = await Notification.requestPermission();
            if (granted !== 'granted') return { ok: false, reason: 'dismissed' };
            return await subscribe(publicKey);
        } catch (err) {
            // Safari, including iOS standalone, REQUIRES a user gesture and throws
            // NotAllowedError for an unprompted call like this one. That is not a
            // failure to report: the toggle on the notifications page is a gesture
            // and works there. The ask is un-recorded so the budget is not spent
            // on a browser that was never going to show the dialog.
            try {
                const log = askLog();
                localStorage.setItem(ASK_LOG_KEY, JSON.stringify({ count: Math.max(0, log.count - 1), last: 0 }));
            } catch { /* ignore */ }

            return { ok: false, reason: 'needs-gesture' };
        }
    }

    // ---- service worker messages ------------------------------------
    // The worker cannot navigate a Blazor app -- routing lives in the page. So
    // it postMessages, and this forwards those into .NET.
    //
    // Registered once and idempotent: MainLayout can re-render freely, and a
    // second listener would navigate twice on one notification click.
    let messageHandlerAttached = false;

    function listen(dotNetRef) {
        if (messageHandlerAttached || !('serviceWorker' in navigator)) return false;
        messageHandlerAttached = true;

        navigator.serviceWorker.addEventListener('message', (event) => {
            const data = event.data || {};
            try {
                if (data.type === 'NOTIFICATION_CLICKED' && typeof data.path === 'string') {
                    dotNetRef.invokeMethodAsync('OnNotificationClicked', data.path);
                } else if (data.type === 'PUSH_RECEIVED') {
                    dotNetRef.invokeMethodAsync('OnPushReceived');
                } else if (data.type === 'PUSH_SUBSCRIPTION_CHANGED') {
                    // The browser reissued the subscription. Re-register it with
                    // the API now that we are in a page that has an auth token.
                    dotNetRef.invokeMethodAsync('OnSubscriptionChanged');
                }
            } catch {
                // A disposed .NET reference after navigation. Nothing to do.
            }
        });

        return true;
    }

    return { getStatus, subscribe, unsubscribe, setBadge, isStandalone, listen, autoEnable, ensureEnabled };
})();
