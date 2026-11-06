(() => {
    "use strict";
    const feed = document.querySelector("[data-action-output]");
    if (!feed || typeof WebSocket === "undefined") return;
    const id = feed.dataset.actionOutput;
    if (!/^[a-f0-9-]{36}$/i.test(id)) return;
    const cursors = {}, panels = new Map(), status = feed.querySelector("[data-feed-status]");
    const sound = feed.querySelector("[data-completion-sound]"), soundStatus = feed.querySelector("[data-sound-status]");
    let audio, notified = !["queued", "running"].includes(feed.dataset.initialState);
    try { sound.checked = localStorage.getItem("stanza.completion-sound") !== "off"; } catch { /* Private storage may be unavailable. */ }
    async function prepareAudio() {
        const Audio = window.AudioContext || window.webkitAudioContext;
        if (!Audio) return;
        audio ||= new Audio();
        await audio.resume();
    }
    async function playSound(success) {
        try {
            await prepareAudio();
            if (!audio || audio.state !== "running") throw new Error("Audio blocked");
            const start = audio.currentTime;
            for (const [offset, frequency] of (success ? [[0, 880], [0.18, 1320]] : [[0, 220], [0.22, 165]])) {
                const oscillator = audio.createOscillator(), gain = audio.createGain();
                oscillator.type = success ? "sine" : "triangle";
                oscillator.frequency.value = frequency;
                gain.gain.setValueAtTime(0.0001, start + offset);
                gain.gain.exponentialRampToValueAtTime(0.18, start + offset + 0.015);
                gain.gain.exponentialRampToValueAtTime(0.0001, start + offset + 0.6);
                oscillator.connect(gain); gain.connect(audio.destination);
                oscillator.start(start + offset); oscillator.stop(start + offset + 0.65);
            }
            soundStatus.textContent = "Sound enabled for this page.";
        } catch { soundStatus.textContent = "Sound was blocked. Click Enable / test sound to allow audio."; }
    }
    sound.addEventListener("change", () => {
        try { localStorage.setItem("stanza.completion-sound", sound.checked ? "on" : "off"); } catch { }
        if (sound.checked) prepareAudio().catch(() => {});
    });
    feed.querySelector("[data-test-sound]").addEventListener("click", () => playSound(true));
    document.addEventListener("pointerdown", () => { if (sound.checked) prepareAudio().catch(() => {}); }, { once: true });
    let socket, stopped = false, terminal = false, retry, refreshing = false;
    function connect() {
        if (stopped) return;
        socket = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/live/actions/" + id + "/output?after=" + encodeURIComponent(JSON.stringify(cursors)));
        socket.onmessage = event => {
            let data; try { data = JSON.parse(event.data); } catch { return; }
            if (typeof data.version !== "string" || data.version.length !== 64 || !Array.isArray(data.operations) || typeof data.state !== "string") return;
            if (!refreshing && data.version !== document.getElementById("action-run-content")?.dataset.version && window.htmx) {
                refreshing = true;
                window.htmx.ajax("GET", "/automation/runs/" + id, { target: "#action-run-content", select: "#action-run-content", swap: "outerHTML" }).finally(() => { refreshing = false; });
            }
            terminal = !["queued", "running"].includes(data.state);
            if (terminal && !notified) { notified = true; if (sound.checked) playSound(data.state === "succeeded"); }
            status.textContent = terminal ? "Run finished. Retained output is available below." : "Live execution output · " + data.state;
            for (const operation of data.operations) {
                if (!/^[a-f0-9-]{36}$/i.test(operation.id)) continue;
                let panel = panels.get(operation.id);
                if (!panel) {
                    const section = document.createElement("section"), heading = document.createElement("h3"), state = document.createElement("p"), output = document.createElement("pre"), link = document.createElement("a");
                    heading.textContent = operation.step ? "Step " + operation.step : "Operation";
                    output.dataset.operationOutput = "";
                    link.href = "/operations/" + operation.id + "/output"; link.textContent = "Download retained output";
                    section.append(heading, state, link, output); feed.querySelector("[data-feed-operations]").append(section);
                    panel = { section, state, output, link }; panels.set(operation.id, panel);
                }
                panel.state.textContent = operation.state + " · " + operation.stage + (operation.code ? " · " + operation.code : "") +
                    (operation.outputIncomplete === true ? " · Output history incomplete (Agent storage failure)" : "");
                panel.link.hidden = !operation.outputAllowed;
                if (!operation.outputAllowed) { panel.output.textContent = "Output access is not granted for this operation."; continue; }
                for (const chunk of operation.chunks || []) {
                    if (!Number.isSafeInteger(chunk.sequence) || chunk.sequence <= (cursors[operation.id] || 0) || typeof chunk.text !== "string") continue;
                    if (!cursors[operation.id]) panel.output.textContent = "";
                    const span = document.createElement("span"); span.textContent = chunk.text;
                    span.dataset.stream = ["stdout", "stderr", "system"].includes(chunk.stream) ? chunk.stream : "stdout";
                    panel.output.append(span); cursors[operation.id] = chunk.sequence;
                    while (panel.output.textContent.length > 1024 * 1024 && panel.output.childNodes.length > 1) panel.output.firstChild.remove();
                }
                if (!cursors[operation.id] && typeof operation.output === "string") panel.output.textContent = operation.output;
                if (feed.querySelector("[data-feed-follow]").checked) panel.output.scrollTop = panel.output.scrollHeight;
            }
        };
        socket.onclose = event => {
            if (event.code === 1008) { stopped = true; panels.forEach(p => { p.output.textContent = ""; p.link.hidden = true; }); status.textContent = "Access revoked or session expired. Sign in again to view the feed."; return; }
            if (event.code === 1000 && terminal) { stopped = true; return; }
            if (!stopped) { status.textContent = "Connection interrupted. Reconnecting…"; retry = setTimeout(connect, 1500); }
        };
    }
    connect();
    window.addEventListener("pagehide", () => { stopped = true; clearTimeout(retry); socket?.close(); }, { once: true });
})();
