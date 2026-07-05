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
window.eventwosCheckin.getPosition = async function () {
    // Returns "lat,lng" (6dp) on success, "unavailable:<code>" on
    // failure. No reverse geocoding here — that happens server-side
    // in EventWOS.Infrastructure.Geo.GeoLocationService, which now
    // uses OpenStreetMap's Nominatim (free, no key, 1 req/s rate-
    // limited by the server). The server splits the value into two
    // typed columns on the AttendanceRecord row:
    //   * location_coords  — this same "lat,lng" for the map link
    //   * location_address — the reverse-geocoded short address
    //                        ("Airoli, Navi Mumbai") for display
    //
    // Options tuned for on-site vendor phones:
    //   * enableHighAccuracy=false — battery-friendly, ~100 m is fine
    //     for venue-level attendance auditing.
    //   * timeout=8000 — mobile GPS cold-start can take 3-6 s in dense
    //     venues; anything shorter produces false-negative fixes.
    //   * maximumAge=60000 — reuse a fix from the last minute across
    //     back-to-back scans of 5-30 crew.
    if (!("geolocation" in navigator)) return "unavailable:no-api";
    return await new Promise((resolve) => {
        navigator.geolocation.getCurrentPosition(
            (pos) => {
                const lat = pos.coords.latitude.toFixed(6);
                const lng = pos.coords.longitude.toFixed(6);
                resolve(`${lat},${lng}`);
            },
            (err) => {
                // 1=PERMISSION_DENIED, 2=POSITION_UNAVAILABLE, 3=TIMEOUT
                resolve(`unavailable:${err && err.code}`);
            },
            { enableHighAccuracy: false, timeout: 8000, maximumAge: 60000 }
        );
    });
};

// ─── Role-based permission priming ─────────────────────────────────
// Called from MainLayout after login. For vendors (who verify crew via
// QR scans) we want camera + geolocation prompts to fire NOW — not
// mid-scan when they've already tapped "Start Scanner" and are staring
// at a black rectangle wondering why nothing's happening.
//
// Prompts:
//   * geolocation → getCurrentPosition() with short timeout
//   * camera      → getUserMedia({video: true}), then immediately stop
//                   all tracks so we don't hold the sensor
//
// Both are best-effort and independent. Denied? We move on. The
// scanner and RecordAttendance flows both tolerate null location and
// camera prompt errors, so this is purely a UX polish — surface the
// browser's OS-level prompts in a calm moment (right after login)
// rather than a busy one (mid check-in queue).
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
