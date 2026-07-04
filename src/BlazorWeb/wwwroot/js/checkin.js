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
