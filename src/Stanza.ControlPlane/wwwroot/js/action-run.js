(() => {
    "use strict";
    const root = document.querySelector("[data-action-run]");
    if (!root || typeof WebSocket === "undefined") return;
    const id = root.dataset.actionRun;
    if (!/^[a-f0-9-]{36}$/i.test(id)) return;
    const socket = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/live/actions/" + id);
    socket.onmessage = event => {
        let data; try { data = JSON.parse(event.data); } catch { return; }
        if (typeof data.version !== "string" || data.version.length !== 64) return;
        if (data.version === document.getElementById("action-run-content")?.dataset.version) return;
        window.htmx?.ajax("GET", "/automation/runs/" + id, { target: "#action-run-content", select: "#action-run-content", swap: "outerHTML" });
    };
    window.addEventListener("pagehide", () => socket.close(), { once: true });
})();
