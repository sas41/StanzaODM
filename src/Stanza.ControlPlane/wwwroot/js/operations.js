(() => {
    "use strict";
    const root = document.querySelector("[data-operation-id]");
    if (!root || typeof WebSocket === "undefined") return;
    const id = root.dataset.operationId;
    if (!/^[a-f0-9-]{36}$/i.test(id)) return;
    const output = root.querySelector("[data-operation-output]");
    const notice = root.querySelector("[data-output-notice]");
    let cursor = 0, stopped = false, finished = false, socket, retry;
    function connect() {
        if (stopped) return;
        socket = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/live/operations/" + id + "?after=" + cursor);
        socket.onmessage = event => {
            let value; try { value = JSON.parse(event.data); } catch { return; }
            if (typeof value.state !== "string" || value.state.length > 40) return;
            const state = root.querySelector("[data-operation-state]");
            if (state) state.textContent = value.state + (typeof value.stage === "string" ? " · " + value.stage : "");
            const code = root.querySelector("[data-operation-code]"); if (code) code.textContent = typeof value.code === "string" ? value.code : "";
            if (!value.outputAllowed) {
                if (output) { output.textContent = ""; output.hidden = true; }
                if (notice) notice.textContent = "You do not have permission to read this operation's output.";
                return;
            }
            if (output && Array.isArray(value.chunks)) for (const chunk of value.chunks) {
                if (!Number.isSafeInteger(chunk.sequence) || chunk.sequence <= cursor || typeof chunk.text !== "string") continue;
                if (cursor === 0) output.textContent = "";
                const fragment = document.createElement("span");
                fragment.dataset.stream = ["stdout", "stderr", "system"].includes(chunk.stream) ? chunk.stream : "stdout";
                fragment.title = fragment.dataset.stream;
                fragment.textContent = chunk.text;
                output.appendChild(fragment);
                cursor = chunk.sequence; output.hidden = false;
                // Bound rendered DOM only. Full history is retained and downloadable.
                while (output.textContent.length > 1024 * 1024 && output.childNodes.length > 1) output.firstChild.remove();
            }
            if (output && cursor === 0 && typeof value.output === "string") { output.hidden = false; output.textContent = value.output; }
            finished = ["Succeeded", "Failed", "Cancelled", "Unknown"].includes(value.state);
            if (notice) notice.textContent = cursor > 0 || output?.textContent ? "Output recorded. Download contains the full retained history; the live view shows recent output." : finished
                ? "No retained output: none was produced, retention expired, or this older execution did not record diagnostics."
                : "Waiting for operation output…";
            if (output && root.querySelector("[data-output-follow]")?.checked) output.scrollTop = output.scrollHeight;
        };
        socket.onclose = event => {
            if (event.code === 1008 || event.code === 1000 && finished) { stopped = true; return; }
            if (!stopped) { if (notice) notice.textContent = "Connection interrupted. Reconnecting to retained output…"; retry = setTimeout(connect, 1500); }
        };
    }
    connect();
    window.addEventListener("pagehide", () => { stopped = true; clearTimeout(retry); socket?.close(); }, { once: true });
})();
