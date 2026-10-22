(function () {
    "use strict";
    document.addEventListener("change", function (event) {
        const select = event.target.closest("[data-branch-choice]");
        if (!select) return;
        const form = select.closest("[data-branch-form]");
        const reference = form && form.querySelector('input[name="Reference"]');
        if (reference && !reference.readOnly) {
            reference.value = select.value;
            reference.dispatchEvent(new Event("input", { bubbles: true }));
        }
    });
    function showError(event) {
        const target = event.detail && event.detail.target;
        if (target && target.id.startsWith("branches-")) {
            target.textContent = "References could not be loaded. Access may have changed. Refresh or sign in again; the saved reference is unchanged.";
        }
    }
    document.addEventListener("htmx:responseError", showError);
    document.addEventListener("htmx:sendError", showError);
    function loading(event, active) {
        const target = event.detail && event.detail.target;
        if (!target || !target.id.startsWith("branches-")) return;
        const indicator = document.getElementById(target.id.replace("branches-", "loading-"));
        if (indicator) indicator.hidden = !active;
    }
    document.addEventListener("htmx:beforeRequest", function (event) { loading(event, true); });
    document.addEventListener("htmx:afterRequest", function (event) { loading(event, false); });
})();
