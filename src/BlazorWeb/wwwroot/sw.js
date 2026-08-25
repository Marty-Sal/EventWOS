/*
 * OpsOracle Service Worker
 * ------------------------
 * Strategy summary — kept minimal and safe for a Blazor WASM app:
 *
 *   1. Precache the app shell on install:
 *      /, /index.html, manifest, favicon, icons, offline fallback.
 *      Nothing more — Blazor's _framework files carry their own
 *      cache-busting hashes and are huge. We let them cache lazily
 *      via the runtime-cache below.
 *
 *   2. Runtime cache-first for immutable static assets:
 *      _framework/*, /icons/*, /*.css, /*.woff2 (NOT /js/* -- see
 *      isImmutableAsset).
 *      These have fingerprinted URLs, so cache-first is safe —
 *      a code change produces a new URL, which misses the cache
 *      and hits the network fresh.
 *
 *   3. Network-first for /api/* calls:
 *      Attendance, check-in, payments — everything time-sensitive.
 *      No caching of API responses. If offline, we return a 503
 *      JSON envelope so the app's ApiResult parser can surface
 *      "Offline — please reconnect."
 *
 *   4. Network-first with cache fallback for navigation (HTML):
 *      Lets a returning user open the app while offline and see the
 *      shell / last-known page instead of the browser's dino.
 *
 *   5. Version bumping:
 *      Change CACHE_VERSION when you deploy a shell change. The
 *      activate handler prunes older caches automatically.
 *
 * NOT trying to be an offline-first PWA. This is Level 1: install-
 * ability + fast reload + graceful offline shell. Mutations
 * (check-in, ratings) still require network by design.
 */

const CACHE_VERSION = 'v10-2026-08-26-landing-stats';
const SHELL_CACHE   = `opsoracle-shell-${CACHE_VERSION}`;
const RUNTIME_CACHE = `opsoracle-runtime-${CACHE_VERSION}`;

// The bare-minimum shell — small enough to precache without pain.
const SHELL_URLS = [
    '/',
    '/index.html',
    '/manifest.webmanifest',
    '/favicon.png',
    '/icons/icon-192.png',
    '/icons/icon-512.png',
    '/icons/apple-touch-icon.png',
    '/icons/notification-badge.png',
    '/offline.html',
];

// ─── install ─────────────────────────────────────────────────────────
self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(SHELL_CACHE)
            .then((cache) => cache.addAll(SHELL_URLS))
            .then(() => {
                // Only auto-activate on a genuine FIRST install (no prior
                // worker was ever controlling this origin) — there's
                // nothing to disrupt yet, so skipping the wait is free.
                //
                // On an UPDATE (self.registration.active already exists,
                // i.e. an older worker is controlling open tabs right
                // now), we must NOT call skipWaiting() here. Doing so
                // unconditionally was a real production bug: it forced
                // immediate activation on every deploy, which fires
                // "controllerchange" on every open tab, and index.html's
                // controllerchange listener calls location.reload() —
                // an automatic reload storm for every visitor the moment
                // any new version deployed, completely bypassing the
                // "New version available" toast that's supposed to let
                // the user pick when to reload. The new worker now stays
                // in the "waiting" state on updates; only the toast's
                // button click (which posts SKIP_WAITING, handled below)
                // triggers activation + reload, on the user's own timing.
                if (!self.registration.active) return self.skipWaiting();
            })
            .catch((err) => {
                // Precache failures shouldn't kill the SW — the app still
                // works, we just lose offline shell. Log for debugging.
                console.warn('[sw] precache partial:', err);
            })
    );
});

// ─── activate ────────────────────────────────────────────────────────
self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        // Prune old shell/runtime caches from previous deploys.
        const keys = await caches.keys();
        await Promise.all(
            keys
                // Both prefixes: the app was renamed from EventWOS to OpsOracle, and a browser
                // that installed the old service worker still holds eventwos-* caches. Drop
                // the old prefix here or those bundles are pinned in every existing install
                // for good.
                .filter((k) => (k.startsWith('opsoracle-') || k.startsWith('eventwos-'))
                               && !k.endsWith(CACHE_VERSION))
                .map((k) => caches.delete(k))
        );

        // Belt-and-braces: even within our CURRENT-version caches,
        // evict any stale copy of the manifest / version stamp that
        // a prior sw.js may have written. isImmutableAsset() now
        // excludes both, so on next boot they will go straight to
        // network — but if the prior SW cached them, they're still
        // in the cache dictionary. Nuke them explicitly.
        for (const cacheName of [SHELL_CACHE, RUNTIME_CACHE]) {
            try {
                const cache = await caches.open(cacheName);
                await cache.delete('/_framework/blazor.boot.json');
                await cache.delete('/version.json');
            } catch { /* cache missing — nothing to evict */ }
        }

        await self.clients.claim();
    })());
});

// ─── fetch router ────────────────────────────────────────────────────
self.addEventListener('fetch', (event) => {
    const req = event.request;

    // Only intercept GETs. POST/PUT/DELETE go straight to the network
    // — we don't want to break the check-in write path.
    if (req.method !== 'GET') return;

    const url = new URL(req.url);

    // Never touch cross-origin — SignalR handshakes, CDN scripts,
    // OTP delivery pings. Let the browser handle those.
    if (url.origin !== self.location.origin) return;

    // API calls → network-first with a JSON offline fallback.
    if (url.pathname.startsWith('/api/')) {
        event.respondWith(networkFirstApi(req));
        return;
    }

    // Framework / immutable-URL assets → cache-first.
    if (isImmutableAsset(url.pathname)) {
        event.respondWith(cacheFirst(req));
        return;
    }

    // Navigation → network-first with cache fallback → offline page.
    if (req.mode === 'navigate') {
        event.respondWith(navigationHandler(req));
        return;
    }

    // Everything else — try network, fall back to cache if we've seen it.
    event.respondWith(networkFirstGeneric(req));
});

// ─── helpers ─────────────────────────────────────────────────────────

function isImmutableAsset(pathname) {
    // blazor.boot.json is the MANIFEST — it lists the fingerprinted
    // filenames of every .wasm/.dll. It is itself NOT fingerprinted,
    // so caching it across a deploy is what caused every SRI failure
    // we've been chasing: the SW returned an old boot.json listing
    // filenames that no longer exist on the server, Blazor requested
    // those ghost files, nginx served a 404 HTML page, and the browser
    // computed SHA-256 over the 404 body and reported "integrity
    // mismatch" against every wasm asset. Same story for version.json.
    if (pathname === '/_framework/blazor.boot.json') return false;
    if (pathname === '/version.json')                return false;

    // blazor.webassembly.js and dotnet.js are the boot LOADER — the
    // code that reads blazor.boot.json and pulls in the fingerprinted
    // runtime/assembly files it lists. Unlike everything else under
    // _framework/, these two keep the SAME filename across every
    // deploy (no content hash in the URL), so cache-first here means
    // "run whatever loader version happened to get cached first,
    // forever" — against a manifest that keeps changing underneath
    // it. That version-skew is exactly the kind of bug that hangs the
    // boot process without throwing a catchable error (nothing to
    // retry on), so it must always go to network, same as the
    // manifest it drives.
    if (pathname === '/_framework/blazor.webassembly.js') return false;
    if (pathname === '/_framework/dotnet.js')              return false;

    // NONE of our own hand-written scripts are fingerprinted -- they are
    // referenced by a plain <script src="js/..."> with no content hash and no
    // cache-busting query string -- so cache-first would pin whichever copy
    // happened to be cached first, forever.
    //
    // This used to name the three scripts that existed when it was written, and
    // push.js was added later and silently inherited cache-first. The result:
    // every fix to the push client shipped to nobody who had already opened the
    // app, which is exactly the audience it was for. Enumerating was the bug, so
    // the whole directory is excluded -- these are a few KB each and still get
    // ordinary HTTP caching from nginx.
    if (pathname.startsWith('/js/')) return false;

    // '/js/' is deliberately absent here -- it returned false above.
    return pathname.startsWith('/_framework/')
        || pathname.startsWith('/_content/')
        || pathname.startsWith('/icons/')
        || /\.(js|css|wasm|dat|blat|woff2?|ttf|png|svg|ico|jpg|webp)$/i.test(pathname);
}

async function cacheFirst(req) {
    const cached = await caches.match(req);
    if (cached) return cached;

    // Cache miss — fetch fresh, cache ONLY if it succeeded. If the
    // network hop returns 404/5xx (e.g. we're requesting a ghost
    // fingerprint from a previous deploy), let that response
    // propagate to the caller AS-IS. Do NOT synthesize a fake 504:
    // that was the previous behaviour and it hid the real cause of
    // failures ("offline" showing up in the log when the server was
    // actually fine, just serving a 404 for a missing asset).
    try {
        const fresh = await fetch(req);
        if (fresh.ok && (fresh.type === 'basic' || fresh.type === 'default')) {
            const cache = await caches.open(RUNTIME_CACHE);
            cache.put(req, fresh.clone());
        }
        return fresh;
    } catch (err) {
        // Genuine network failure (offline / DNS). Surface it honestly.
        return new Response(
            'Service worker: network fetch failed for ' + req.url,
            { status: 504, statusText: 'network-error' }
        );
    }
}

async function networkFirstApi(req) {
    try {
        return await fetch(req);
    } catch {
        // Match the ApiResponse envelope so the client's ApiResult<T>
        // parser folds this into `.Message` and surfaces "Offline" in
        // the UI instead of the generic "Unexpected response."
        const body = JSON.stringify({
            success: false,
            data: null,
            message: null,
            errors: ["Offline — please reconnect."],
        });
        return new Response(body, {
            status: 503,
            statusText: 'offline',
            headers: { 'Content-Type': 'application/json' },
        });
    }
}

async function navigationHandler(req) {
    try {
        const fresh = await fetch(req);
        // Cache the index for future offline navigations.
        const cache = await caches.open(RUNTIME_CACHE);
        cache.put('/index.html', fresh.clone());
        return fresh;
    } catch {
        // Prefer the cached shell — it can re-hydrate from the runtime
        // cache. Fall back to the offline page if even /index.html isn't
        // cached (first visit while already offline — rare).
        return (await caches.match('/index.html'))
            || (await caches.match('/offline.html'))
            || new Response('offline', { status: 503 });
    }
}

async function networkFirstGeneric(req) {
    try {
        const fresh = await fetch(req);
        if (fresh.ok) {
            const cache = await caches.open(RUNTIME_CACHE);
            cache.put(req, fresh.clone());
        }
        return fresh;
    } catch {
        return (await caches.match(req))
            || new Response('', { status: 504, statusText: 'offline' });
    }
}

// ─── skipWaiting message channel ─────────────────────────────────────
// The page's install handler can post {type:'SKIP_WAITING'} to force
// an immediate takeover after a deploy — used by the "New version
// available, reload?" toast.
self.addEventListener('message', (event) => {
    if (event.data?.type === 'SKIP_WAITING') self.skipWaiting();
});

// ---- push ----------------------------------------------------------
// Everything below is the notification side of the worker. It is
// deliberately independent of the caching strategy above: a push can
// arrive when no tab is open at all, which is the entire point.
//
// The server chooses the title, body and target path, so adding a
// notification type is a backend change -- NOT a service worker
// change. That matters because a worker update only reaches a user on
// their next visit, so anything encoded here is frozen for people who
// do not come back for a week.

// The large icon in the notification body: full colour, shown as-is.
const NOTIFICATION_ICON = '/icons/icon-192.png';

// The status-bar badge is NOT a small version of the icon. Android masks it to a
// single colour using ALPHA ONLY -- every opaque pixel becomes white whatever its
// RGB. Pointing this at the full-colour square icon produced a solid white blob
// in the status bar, which is what "no icon" actually looked like. This asset is
// a transparent silhouette of the brand glyph, which is what the API wants.
const NOTIFICATION_BADGE = '/icons/notification-badge.png';

self.addEventListener('push', (event) => {
    event.waitUntil(handlePush(event));
});

async function handlePush(event) {
    const payload = readPayload(event);

    // A push with no usable data still gets shown. Browsers require a
    // visible notification for a userVisibleOnly subscription, and
    // silently swallowing it can cost us the push permission entirely.
    const title = payload.title || 'OpsOracle';
    const body  = payload.body  || 'You have a new notification.';

    // tag collapses an updated version of the same notification instead
    // of stacking duplicates on the lock screen; renotify still alerts.
    const tag = payload.notificationId || payload.notificationType || 'opsoracle';

    await self.registration.showNotification(title, {
        body,
        tag,
        renotify: true,
        icon:  NOTIFICATION_ICON,
        badge: NOTIFICATION_BADGE,

        // Explicit, not defaulted. A notification is silent when silent:true OR
        // when the browser decides it looks like a background update, and being
        // explicit costs nothing while guessing costs the alert.
        silent: false,

        // Every notification now vibrates; urgent ones vibrate longer. The old
        // code passed undefined for routine news, which on Android also
        // suppresses the alert SOUND, not just the buzz -- so "routine" news
        // arrived completely unannounced. Distinguishing urgency by pattern
        // keeps the distinction without silencing anything.
        vibrate: payload.priority === 'Critical' || payload.priority === 'High'
            ? [200, 100, 200, 100, 200]
            : [200],
        // Kept out of requireInteraction on purpose: a notification that
        // will not dismiss itself is the fastest way to get muted.
        data: {
            deepLink: sanitizePath(payload.deepLink),
            notificationId: payload.notificationId || null,
            notificationType: payload.notificationType || null
        }
    });

    // The count comes from the server, which is authoritative -- reading
    // something on a phone should make the laptop badge fall too.
    if (typeof payload.badgeCount === 'number') await setAppBadge(payload.badgeCount);

    // Any open tab refreshes its bell immediately rather than waiting
    // for its next poll.
    await broadcast({ type: 'PUSH_RECEIVED', notificationId: payload.notificationId || null });
}

function readPayload(event) {
    if (!event.data) return {};
    try {
        return event.data.json() || {};
    } catch {
        // Not JSON. Treat the raw text as a body rather than dropping it.
        try { return { body: event.data.text() }; } catch { return {}; }
    }
}

// ---- click ----------------------------------------------------------
// Focus a tab the user already has open instead of piling up new ones,
// and only open a window when there is genuinely nothing to focus.
self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    event.waitUntil(openTarget(sanitizePath(event.notification.data?.deepLink)));
});

async function openTarget(path) {
    const target = new URL(path || '/', self.location.origin);
    const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });

    for (const client of clients) {
        // Same-origin only. A cross-origin client cannot be navigated,
        // and trying is how a click ends up doing nothing at all.
        if (new URL(client.url).origin !== self.location.origin) continue;

        await client.focus();

        // Blazor routing lives in the page, so the tab is told where to
        // go rather than being hard-navigated -- a full navigation would
        // reload the whole WASM runtime for what is an in-app link.
        client.postMessage({ type: 'NOTIFICATION_CLICKED', path: target.pathname + target.search });
        return;
    }

    await self.clients.openWindow(target.href);
}

// ---- subscription rotation ------------------------------------------
// Browsers retire and reissue subscriptions on their own schedule. When
// that happens the old endpoint starts returning 410 and the user goes
// quiet with nobody noticing, so the worker immediately re-subscribes.
//
// It cannot register the new subscription with the API itself: the auth
// token lives in the page, not here. So it re-subscribes to keep the
// browser side live and tells any open tab; the app also re-registers on
// every visit (subscribe is an upsert), which closes the gap for a user
// who had no tab open.
self.addEventListener('pushsubscriptionchange', (event) => {
    event.waitUntil(resubscribe(event));
});

async function resubscribe(event) {
    try {
        const key = event.oldSubscription?.options?.applicationServerKey;
        if (!key) return;

        const fresh = await self.registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: key
        });

        await broadcast({ type: 'PUSH_SUBSCRIPTION_CHANGED', subscription: fresh.toJSON() });
    } catch {
        // Nothing useful to do here. The next visit re-registers.
    }
}

// ---- helpers ---------------------------------------------------------

// Only ever navigate to a path on our own origin. The deep link arrives
// inside a push payload, so treating it as a full URL would turn a
// notification into an open redirect if a payload were ever tampered
// with. The server sanitises too; this is the second lock.
function sanitizePath(value) {
    if (typeof value !== 'string' || value.length === 0) return '/';
    if (!value.startsWith('/')) return '/';
    if (value.startsWith('//')) return '/';   // protocol-relative URL
    if (value.includes('\\')) return '/';
    return value;
}

async function setAppBadge(count) {
    try {
        if (count > 0 && self.navigator?.setAppBadge) await self.navigator.setAppBadge(count);
        else if (self.navigator?.clearAppBadge)       await self.navigator.clearAppBadge();
    } catch {
        // Badging is unsupported on most desktop browsers. Never fatal.
    }
}

async function broadcast(message) {
    const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of clients) {
        try { client.postMessage(message); } catch { /* a closing tab */ }
    }
}
