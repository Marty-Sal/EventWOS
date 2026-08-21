// Thin wrapper around Quill (loaded from CDN in index.html) so Blazor's
// RichTextEditor.razor component can init/read/write a WYSIWYG editor via
// JS interop. One Quill instance per element id; Blazor owns the container
// div (an empty <div id="...">), Quill owns everything inside it — Blazor
// never re-renders into that div's children, so there's no DOM-diffing
// conflict between the two.
window.eventwosRichText = (function () {
    const editors = {};

    const toolbarOptions = [
        [{ header: [1, 2, 3, false] }],
        ["bold", "italic", "underline", "strike"],
        [{ script: "sub" }, { script: "super" }],
        [{ color: [] }, { background: [] }],
        [{ list: "ordered" }, { list: "bullet" }],
        [{ indent: "-1" }, { indent: "+1" }],
        [{ align: [] }],
        ["blockquote", "link"],
        ["clean"],
    ];

    return {
        init: function (elementId, initialHtml, dotnetRef) {
            const el = document.getElementById(elementId);
            if (!el || typeof Quill === "undefined") return;

            const quill = new Quill(el, {
                theme: "snow",
                modules: { toolbar: toolbarOptions },
            });

            if (initialHtml) quill.root.innerHTML = initialHtml;

            quill.on("text-change", function () {
                if (dotnetRef) {
                    dotnetRef.invokeMethodAsync("OnContentChanged", quill.root.innerHTML);
                }
            });

            editors[elementId] = quill;
        },

        getHtml: function (elementId) {
            const q = editors[elementId];
            return q ? q.root.innerHTML : "";
        },

        setHtml: function (elementId, html) {
            const q = editors[elementId];
            if (q) q.root.innerHTML = html || "";
        },

        destroy: function (elementId) {
            delete editors[elementId];
        },
    };
})();
