/*
 * EventWOS Service Worker
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
 *      _framework/*, /js/*, /icons/*, /*.css, /*.woff2.
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

const CACHE_VERSION = 'v1-2026-07-05';
const SHELL_CACHE   = `eventwos-shell-${CACHE_VERSION}`;
const RUNTIME_CACHE = `eventwos-runtime-${CACHE_VERSION}`;

// The bare-minimum shell — small enough to precache without pain.
const SHELL_URLS = [
    '/',
    '/index.html',
    '/manifest.webmanifest',
    '/favicon.png',
    '/icons/icon-192.png',
    '/icons/icon-512.png',
    '/icons/apple-touch-icon.png',
    '/offline.html',
];

// ─── install ─────────────────────────────────────────────────────────
self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(SHELL_CACHE)
            .then((cache) => cache.addAll(SHELL_URLS))
            // Take over from any previous SW immediately on first install.
            .then(() => self.skipWaiting())
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
                .filter((k) => k.startsWith('eventwos-') && !k.endsWith(CACHE_VERSION))
                .map((k) => caches.delete(k))
        );
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
    return pathname.startsWith('/_framework/')
        || pathname.startsWith('/_content/')
        || pathname.startsWith('/icons/')
        || pathname.startsWith('/js/')
        || /\.(js|css|wasm|dat|blat|woff2?|ttf|png|svg|ico|jpg|webp)$/i.test(pathname);
}

async function cacheFirst(req) {
    const cached = await caches.match(req);
    if (cached) return cached;

    try {
        const fresh = await fetch(req);
        // Only cache good responses. `basic` = same-origin.
        if (fresh.ok && (fresh.type === 'basic' || fresh.type === 'default')) {
            const cache = await caches.open(RUNTIME_CACHE);
            cache.put(req, fresh.clone());
        }
        return fresh;
    } catch (err) {
        // No cached copy and no network — return a lightweight failure.
        return new Response('', { status: 504, statusText: 'offline' });
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
