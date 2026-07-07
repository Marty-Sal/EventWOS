// ── EventWOS check-in JS interop ──────────────────────────────────────────────
//
// Two responsibilities, both intentionally kept dead-simple so a page can call
// one function and forget:
//   1. renderQr(elementId, text)       — draws a QR into a div.
//   2. startScanner(elementId, dotnetRef)  — turns on the camera + scans QR.
//      On success it calls dotnetRef.invokeMethodAsync("OnCodeScanned", code).
//      Caller must invoke stopScanner() before disposing.
//
// Libraries (loaded on first use — no cost if the page never uses them):
//   - qrcode.js (davidshimjs)  MIT, ~10KB gz  — pure-JS QR renderer.
//   - html5-qrcode             Apache-2.0     — camera-based scanner.
//
// Both are pinned to major versions on jsdelivr to avoid the "cdn broke"
// class of production incidents.

const CDN_QRCODE  = "https://cdn.jsdelivr.net/npm/qrcode-generator@1.4.4/qrcode.min.js";
const CDN_SCANNER = "https://cdn.jsdelivr.net/npm/html5-qrcode@2.3.8/html5-qrcode.min.js";

// Cached load promises so repeat calls don't re-inject <script> tags.
const _loaded = {};
function loadScript(url) {
    if (_loaded[url]) return _loaded[url];
    _loaded[url] = new Promise((resolve, reject) => {
        const s = document.createElement("script");
        s.src   = url;
        s.async = true;
        s.onload  = () => resolve();
        s.onerror = () => reject(new Error("Failed to load " + url));
        document.head.appendChild(s);
    });
    return _loaded[url];
}

// ── QR render ─────────────────────────────────────────────────────────────
window.eventwosCheckin = window.eventwosCheckin || {};

window.eventwosCheckin.renderQr = async function (elementId, text) {
    await loadScript(CDN_QRCODE);
    const host = document.getElementById(elementId);
    if (!host) return;

    // Type 0 = auto pick a size that fits; L = low error correction (our
    // codes are short + high-entropy so we don't need heavy ECC).
    // Cell size 6 renders at ~250px for a 12-char payload — a comfortable
    // scan distance on a phone.
    const qr = qrcode(0, "L");
    qr.addData(text);
    qr.make();

    // createImgTag returns a self-contained data-URI <img>. Simpler than
    // canvas/svg and scales cleanly with CSS.
    host.innerHTML = qr.createImgTag(6, 8);
    // Force the injected <img> to be responsive and centered.
    const img = host.querySelector("img");
    if (img) {
        img.style.width       = "100%";
        img.style.maxWidth    = "260px";
        img.style.height      = "auto";
        img.style.display     = "block";
        img.style.margin      = "0 auto";
        img.style.imageRendering = "pixelated"; // sharp modules on scale-up
    }
};

// ── Camera scanner ────────────────────────────────────────────────────────
let _scanner = null;

window.eventwosCheckin.startScanner = async function (elementId, dotnetRef) {
    await loadScript(CDN_SCANNER);

    // Stop any prior scanner first — starting twice on the same element
    // deadlocks the html5-qrcode internal state machine.
    await window.eventwosCheckin.stopScanner();

    // eslint-disable-next-line no-undef
    _scanner = new Html5Qrcode(elementId);

    // fps=10 is plenty for QR at conversational scan distance; higher just
    // wastes battery. qrbox = 250 keeps the aim reticle tight so partial
    // background QRs (posters etc.) don't get misread.
    const config = {
        fps: 10,
        qrbox: { width: 240, height: 240 },
        aspectRatio: 1.0
    };

    try {
        await _scanner.start(
            { facingMode: "environment" }, // rear camera on phones
            config,
            (decodedText) => {
                // Fire-and-forget — the .NET side stops us if it accepts
                // the code, or lets us keep scanning if it doesn't.
                dotnetRef.invokeMethodAsync("OnCodeScanned", decodedText);
            },
            (_err) => { /* per-frame decode misses — ignore, spammy */ }
        );
        return { ok: true };
    } catch (e) {
        // NotAllowedError = user denied camera; NotFoundError = no camera.
        return { ok: false, error: (e && e.message) || String(e) };
    }
};

window.eventwosCheckin.stopScanner = async function () {
    if (_scanner) {
        try {
            if (_scanner.isScanning) await _scanner.stop();
            await _scanner.clear();
        } catch { /* best-effort teardown */ }
        _scanner = null;
    }
};


// ─── Geolocation ─────────────────────────────────────────────────────
// One-shot GPS lookup for the Scan Check-In tab. The vendor's phone
// pops the OS permission prompt once — we cache the result server-side
// on the AttendanceRecord.location column ("lat,lng" string, or
// "unavailable" if the browser refused/couldn't fix). Deliberately
// resolves instead of rejecting on failure so the caller can just await
// and get a value to send with every /verify — location is nice-to-have,
// never a blocker for the check-in itself.
//
// Timeout is 8s: mobile GPS cold-start on the first tap of the day can
// take 3-6s, so anything less produces false "unavailable" values in
// crowded venues. maximumAge=60000 lets us reuse a fix from the last
// minute across many scans (typical vendor scans 5-30 crew back-to-back).
// ─── requireLocation(): STRICT location acquisition ─────────────────
// Returns a structured result the Blazor caller can branch on cleanly:
//   { ok: true,  coords: "lat,lng" }                             success
//   { ok: false, reason: "no-api"           }   browser has no Geolocation API
//   { ok: false, reason: "no-secure-context" } page is not HTTPS/localhost
//   { ok: false, reason: "denied"           }   user rejected the prompt
//   { ok: false, reason: "unavailable"      }   OS/hardware says "no fix"
//   { ok: false, reason: "timeout"          }   8s elapsed with no fix
//
// Used to GATE the check-in / check-out UI entry points: the crew
// screen calls this BEFORE opening the QR modal (or before firing the
// check-out POST) and, on !ok, renders a "Location access is required"
// card explaining exactly what to do next. Returns a structured
// { ok, reason, coords } shape so the caller can both use the fresh
// fix on the happy path AND branch on the failure code for tailored
// user guidance — see LocationRequiredModal for the reason → copy
// mapping.
//
// The distinction between denied vs unavailable vs timeout matters:
// "denied" means the crew needs to unblock the site in browser
// settings (very different UX from "we couldn't get a fix — step
// outside"). We surface the reason so the Razor component can show
// the right guidance instead of a generic error.
window.eventwosCheckin.requireLocation = async function () {
    if (!("geolocation" in navigator)) {
        return { ok: false, reason: "no-api" };
    }
    // Geolocation is silently blocked on non-secure origins by every
    // modern browser (Chrome/Edge/Firefox/Safari). Detect explicitly
    // so the error message can point at HTTPS instead of the user's
    // permission settings.
    if (typeof window.isSecureContext === "boolean" && !window.isSecureContext) {
        return { ok: false, reason: "no-secure-context" };
    }
    return await new Promise((resolve) => {
        navigator.geolocation.getCurrentPosition(
            (pos) => {
                const lat = pos.coords.latitude.toFixed(6);
                const lng = pos.coords.longitude.toFixed(6);
                resolve({ ok: true, coords: `${lat},${lng}` });
            },
            (err) => {
                // GeolocationPositionError codes:
                //   1 = PERMISSION_DENIED
                //   2 = POSITION_UNAVAILABLE (GPS off, no signal, etc.)
                //   3 = TIMEOUT
                const code = err && err.code;
                const reason =
                    code === 1 ? "denied" :
                    code === 3 ? "timeout" :
                    "unavailable";
                resolve({ ok: false, reason });
            },
            { enableHighAccuracy: false, timeout: 8000, maximumAge: 60000 }
        );
    });
};

// ─── Role-based permission priming ─────────────────────────────────
// Called from MainLayout after login. Surfaces OS-level permission
// prompts in a calm moment (right after login) instead of a busy one
// (mid-scan / mid-check-in tap) so users don't stare at a black
// camera rectangle or a stalled "Show QR" button wondering what's
// happening.
//
// Role→prompt mapping is set by the caller in MainLayout:
//   * Vendors / verifiers → camera (QR scanning). No location — the
//     crew's device is the source of truth for attendance coords.
//   * Crew → location (Check-In and Check-Out both require it).
//
// Prompts:
//   * geolocation → getCurrentPosition() with short timeout, discarded
//     — we only want the permission grant, not the fix itself.
//   * camera      → getUserMedia({video: true}), tracks stopped
//     immediately so we don't hold the sensor.
//
// Both are best-effort and independent. Denied? We move on — the
// downstream flows (LocationRequiredModal for crew, the "Could not
// access the camera" state for vendors) surface the real reject
// with proper guidance.
//
// The `permissions` object controls which prompts to fire:
//   { location: true, camera: true }
// Both default to false, so callers must opt in per role.
window.eventwosCheckin.primePermissions = async function (permissions) {
    const results = { location: null, camera: null };
    permissions = permissions || {};

    if (permissions.location && "geolocation" in navigator) {
        try {
            await new Promise((resolve) => {
                navigator.geolocation.getCurrentPosition(
                    () => { results.location = "granted"; resolve(); },
                    (e) => { results.location = `denied:${e && e.code}`; resolve(); },
                    { enableHighAccuracy: false, timeout: 6000, maximumAge: 300000 }
                );
            });
        } catch { results.location = "error"; }
    }

    if (permissions.camera && navigator.mediaDevices?.getUserMedia) {
        try {
            const stream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: "environment" }
            });
            // Immediately release the camera — we only wanted the prompt.
            stream.getTracks().forEach((t) => t.stop());
            results.camera = "granted";
        } catch (e) {
            results.camera = `denied:${e && e.name}`;
        }
    }

    return results;
};
