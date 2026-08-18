// ── EventWOS form-input JS interop ────────────────────────────────────────
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
window.eventwosForms = window.eventwosForms || {};

window.eventwosForms.setInputValue = function (element, value) {
    if (!element) return;
    if (element.value !== value) {
        element.value = value;
    }
};
