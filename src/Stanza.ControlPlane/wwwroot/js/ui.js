(() => {
    "use strict";
    const sidebar = document.querySelector(".sidebar");
    const toggle = sidebar?.querySelector(".nav-toggle");
    if (!toggle) return;
    const desktop = window.matchMedia("(min-width: 761px)");
    const open = value => {
        sidebar.toggleAttribute("data-open", value);
        toggle.setAttribute("aria-expanded", String(value));
    };
    toggle.addEventListener("click", () => open(!sidebar.hasAttribute("data-open")));
    document.addEventListener("keydown", event => {
        if (event.key === "Escape" && sidebar.hasAttribute("data-open")) { open(false); toggle.focus(); }
    });
    document.addEventListener("click", event => {
        if (!desktop.matches && sidebar.hasAttribute("data-open") && !sidebar.contains(event.target)) open(false);
    });
    desktop.addEventListener("change", () => open(false));
})();
