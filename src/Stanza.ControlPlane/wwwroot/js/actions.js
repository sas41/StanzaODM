(() => {
    "use strict";
    const dialog = document.getElementById("action-dialog");
    let opening = false;
    document.addEventListener("htmx:beforeRequest", event => {
        if (event.detail.target?.id === "action-dialog-content") {
            opening = true;
            const overview = document.getElementById("deployment-overview");
            if (overview) window.htmx?.trigger(overview, "htmx:abort");
        }
    });
    document.addEventListener("htmx:afterRequest", event => {
        if (event.detail.target?.id === "action-dialog-content") opening = false;
    });
    document.addEventListener("click", event => {
        if (event.target.closest("[data-close-action]")) dialog?.close();
    });
    function initialize(root) {
        root.querySelectorAll("[data-action-choice]:not([data-ready])").forEach(choice => {
            choice.dataset.ready = "true";
            const search = choice.querySelector("[data-choice-search]");
            const select = choice.querySelector("[data-choice-select]");
            const more = choice.querySelector("[data-choice-more]");
            const message = choice.querySelector("[data-choice-message]");
            let page = 0, request = null, timer;
            async function load(append = false, refresh = false) {
                request?.abort(); request = new AbortController();
                const current = request;
                select.disabled = true; message.textContent = "Loading…";
                const url = new URL(choice.dataset.url, location.origin);
                url.search = new URLSearchParams({ name: choice.dataset.name, search: search.value, page: String(page), refresh: String(refresh) });
                try {
                    const response = await fetch(url, { signal: current.signal, credentials: "same-origin" });
                    if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.error || "Choices unavailable or access revoked."); }
                    const data = await response.json();
                    if (!append) select.replaceChildren(new Option("Select…", ""));
                    for (const value of data.values) {
                        if (typeof value !== "string") continue;
                        const option = new Option(value, value);
                        option.selected = value === choice.dataset.selected;
                        select.add(option);
                    }
                    if (!append && !search.value && data.selectedAvailable && data.selected === choice.dataset.selected &&
                        !Array.from(select.options).some(o => o.value === data.selected)) {
                        const option = new Option(data.selected, data.selected); option.selected = true; select.add(option);
                    }
                    more.hidden = !data.hasMore;
                    message.textContent = data.values.length === 0 ? "No matching values." : "";
                    if (!search.value && choice.dataset.selected && !Array.from(select.options).some(o => o.value === choice.dataset.selected))
                        message.textContent = "Previous selection is unavailable or has not been verified. Choose an available value.";
                } catch (error) {
                    if (error.name !== "AbortError") { message.textContent = error.message; more.hidden = true; select.replaceChildren(new Option("Choices unavailable", "")); }
                } finally { if (request === current) select.disabled = false; }
            }
            search.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(() => { page = 0; load(); }, 250); });
            select.addEventListener("change", () => { choice.dataset.selected = select.value; });
            more.addEventListener("click", () => { page++; load(true); });
            choice.querySelector("[data-choice-refresh]").addEventListener("click", () => { page = 0; load(false, true); });
            load();
        });
    }
    initialize(document);
    document.addEventListener("htmx:afterSwap", event => {
        if (event.detail.target.id === "action-dialog-content") { initialize(event.detail.target); if (!dialog.open) dialog.showModal(); }
    });
    document.addEventListener("submit", event => {
        if (event.target.matches("[data-action-invocation]")) event.target.querySelector("button[type=submit]").disabled = true;
    });
    const overview = document.querySelector("[data-action-overview]");
    if (overview && typeof WebSocket !== "undefined") {
        const socket = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/live/deployment-actions");
        let pending = false;
        const refreshOverview = () => {
            if (!dialog?.open && !opening && pending) {
                pending = false;
                window.htmx?.ajax("GET", "/overview/deployments", { target: "#deployment-overview", swap: "innerHTML" });
            }
        };
        dialog?.addEventListener("close", refreshOverview);
        socket.onmessage = event => {
            let data; try { data = JSON.parse(event.data); } catch { return; }
            if (typeof data.version !== "string" || data.version.length !== 64) return;
            if (data.version === overview.querySelector("[data-overview-version]")?.dataset.overviewVersion) return;
            pending = true; refreshOverview();
        };
        window.addEventListener("pagehide", () => socket.close(), { once: true });
    }
})();
