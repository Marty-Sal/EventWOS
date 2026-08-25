// ── OpsOracle form-input JS interop ────────────────────────────────────────
//
// Blazor's "controlled input" pattern (value="@_field" + @oninput handler
// that filters/rewrites the C# backing field) has a well-known desync bug:
// if the filtered result equals the PREVIOUS render's value (e.g. typing a
// non-digit into an already-empty numeric field filters back to "", same
// as before), Blazor's diffing sees "no change" and skips writing the
// value attribute back to the real DOM node — so the raw, unfiltered
// keystroke the browser already rendered stays visibly stuck in the
// input, even though the C# state is correct. Classic symptom: type
// "qere" into a digits-only field and see "qere" sitting there instead
// of "".
//
// Fix: after computing the filtered value in C#, force-write it straight
// onto the DOM element via direct property assignment, bypassing
// Blazor's diff entirely. Called from an ElementReference right after
// the oninput handler runs.
window.opsOracleForms = window.opsOracleForms || {};

window.opsOracleForms.setInputValue = function (element, value) {
    if (!element) return;
    if (element.value !== value) {
        element.value = value;
    }
};

// ── Cursor-aware text insertion (invite-message composer) ────────────────────
// Powers the "Aa" formatting menu, emoji picker and "Insert invite link"
// button on the vendor's Custom Invite Message box. We deliberately do
// this against the plain <textarea> (not a contenteditable rich-text
// surface) — the composed message only ever gets consumed as plain text
// (clipboard copy / Web Share sheet to WhatsApp/SMS), and WhatsApp already
// renders *bold*, _italic_ and ~strikethrough~ markers itself, so wrapping
// selections in those characters IS the real formatting, not a fake
// preview that would be lost the moment it left the browser.
window.opsOracleForms.getSelectionRange = function (el) {
    if (!el) return [0, 0];
    return [el.selectionStart || 0, el.selectionEnd || 0];
};

window.opsOracleForms.setSelectionRange = function (el, start, end) {
    if (!el) return;
    el.focus();
    try { el.setSelectionRange(start, end); } catch { }
};

// ── Native share sheet (Web Share API) ──────────────────────────────────────
// Takes real JS parameters instead of interpolating values into an eval()
// string (the previous approach was fragile — any stray quote/backslash in
// a vendor-authored invite message would break the generated script).
// Returns true if navigator.share ran (user may still cancel the sheet —
// that's still "handled", not a fallback case), false if the API is
// unavailable so the caller can fall back to clipboard copy.
window.opsOracleForms.share = async function (title, text, url) {
    if (!navigator.share) return false;
    try {
        await navigator.share({ title, text, url });
        return true;
    } catch {
        // AbortError (user dismissed the sheet) still counts as "handled" —
        // only a missing API should trigger the clipboard fallback.
        return true;
    }
};
