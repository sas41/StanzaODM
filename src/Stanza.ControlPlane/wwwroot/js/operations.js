(() => {
    "use strict";
    const root = document.querySelector("[data-operation-id]");
    if (!root || typeof WebSocket === "undefined") return;
    const id = root.dataset.operationId;
    if (!/^[a-f0-9-]{36}$/i.test(id)) return;
    const socket = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/live/operations/" + id);
    socket.onmessage = event => {
        let value; try { value = JSON.parse(event.data); } catch { return; }
        if (typeof value.state !== "string" || value.state.length > 40) return;
        const state = root.querySelector("[data-operation-state]"); if (state) state.textContent = value.state + (typeof value.stage === "string" ? " · " + value.stage : "");
        const code = root.querySelector("[data-operation-code]"); if (code) code.textContent = typeof value.code === "string" ? value.code : "";
        const output = root.querySelector("[data-operation-output]");
        if (output && typeof value.output === "string") { output.hidden = false; output.textContent = value.output.slice(-65536); }
    };
    window.addEventListener("pagehide", () => socket.close(), { once: true });
})();
