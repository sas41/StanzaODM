(() => {
    "use strict";
    const status = document.getElementById("system-status");
    if (!status || !window.WebSocket) return;

    let socket;
    let timer;
    let stopped = false;
    let attempts = 0;
    const update = (selector, text) => {
        status.querySelectorAll(selector).forEach(element => { element.textContent = text; });
    };
    const connect = () => {
        if (stopped || !status.isConnected) return;
        const url = new URL("/live/status", window.location.href);
        url.protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
        socket = new WebSocket(url);
        socket.onmessage = event => {
            try {
                const data = JSON.parse(event.data);
                if (typeof data.databaseReady !== "boolean" ||
                    ![data.agentCount, data.pendingAgentCount].every(value => value === null ||
                        (Number.isSafeInteger(value) && value >= 0))) return;
                attempts = 0;
                update("[data-database-status]", data.databaseReady ? "Connected" : "Unavailable");
                update("[data-agent-count]", data.agentCount === null ? "—" : String(data.agentCount));
                update("[data-pending-agent-count]", data.pendingAgentCount === null ? "—" : String(data.pendingAgentCount));
            } catch { /* Ignore malformed status without interpreting it as markup. */ }
        };
        socket.onclose = event => {
            if (stopped || event.code === 1008 || event.code === 1000) return;
            update("[data-database-status]", "Disconnected");
            const delay = Math.min(30000, 1000 * (2 ** Math.min(attempts++, 5)));
            timer = window.setTimeout(connect, delay + Math.floor(Math.random() * 500));
        };
    };
    window.addEventListener("pagehide", () => {
        stopped = true;
        window.clearTimeout(timer);
        if (socket) socket.close(1000);
    });
    connect();
})();
