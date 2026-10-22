(() => {
    'use strict';
    const form = document.getElementById('terminal-connect');
    if (!form) return;
    const container = document.getElementById('container-terminal');
    const status = document.getElementById('terminal-status');
    const disconnect = document.getElementById('terminal-disconnect');
    const connect = form.querySelector('button[type="submit"]');
    const encoder = new TextEncoder();
    let socket = null;
    let terminal = null;
    let fit = null;
    let ready = false;
    let pendingOutput = 0;
    let closeNotice = null;
    container.style.height = '60vh';
    container.style.minHeight = '240px';

    function dimensions() {
        fit.fit();
        return { columns: Math.max(2, Math.min(500, terminal.cols)), rows: Math.max(1, Math.min(300, terminal.rows)) };
    }
    function stop(message) {
        ready = false;
        closeNotice = message;
        if (socket && socket.readyState < WebSocket.CLOSING) socket.close(1000, 'Terminal closed.');
        status.textContent = message;
    }
    function send(message) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return false;
        if (socket.bufferedAmount > 262144) { stop('Connection is too slow. Reconnect explicitly.'); return false; }
        socket.send(JSON.stringify(message));
        return true;
    }
    function sendInput(data) {
        if (!ready) return;
        sendBytes(encoder.encode(data));
    }
    function sendBytes(bytes) {
        for (let offset = 0; offset < bytes.length; offset += 16384) {
            const chunk = bytes.subarray(offset, offset + 16384);
            let binary = '';
            for (const value of chunk) binary += String.fromCharCode(value);
            if (!send({ type: 'input', data: btoa(binary) })) break;
        }
    }
    form.addEventListener('submit', event => {
        event.preventDefault();
        if (socket && socket.readyState < WebSocket.CLOSED) return;
        const fields = new FormData(form);
        let args;
        try {
            args = JSON.parse(fields.get('arguments'));
            if (!Array.isArray(args) || args.length > 64 || args.some(value => typeof value !== 'string' || value.length > 4096)) throw new Error();
        } catch (_) { status.textContent = 'Arguments must be a JSON array of at most 64 strings.'; return; }
        if (terminal) terminal.dispose();
        terminal = new Terminal({ cursorBlink: true, scrollback: 1000, allowProposedApi: false, convertEol: false });
        fit = new FitAddon.FitAddon();
        terminal.loadAddon(fit);
        terminal.open(container);
        terminal.onData(sendInput);
        terminal.onBinary(data => { if (ready) sendBytes(Uint8Array.from(data, character => character.charCodeAt(0))); });
        pendingOutput = 0;
        closeNotice = null;
        ready = false;
        const target = fields.get('target');
        const handshake = {
            type: 'open', executable: fields.get('executable'), arguments: args,
            service: fields.get('targetType') === 'service' ? target : null,
            container: fields.get('targetType') === 'container' ? target : null,
            user: fields.get('user') || null, workingDirectory: fields.get('workingDirectory') || null,
            ...dimensions()
        };
        const url = new URL(`/live/terminal/${encodeURIComponent(form.dataset.deploymentId)}`, location.origin);
        url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const current = socket = new WebSocket(url);
        connect.disabled = true;
        disconnect.disabled = false;
        status.textContent = 'Connecting to the assigned Agent…';
        current.onopen = () => send(handshake);
        current.onmessage = event => {
            if (current !== socket) return;
            try {
                if (typeof event.data !== 'string' || event.data.length > 92160) throw new Error();
                const message = JSON.parse(event.data);
                if (message.type === 'ready') {
                    ready = true;
                    status.textContent = 'Connected.';
                    container.scrollIntoView({ block: 'nearest' });
                    terminal.focus();
                }
                else if (message.type === 'output') {
                    const raw = atob(message.data);
                    pendingOutput += raw.length;
                    if (pendingOutput > 1048576) { stop('Terminal output exceeded the display buffer. Reconnect explicitly.'); return; }
                    const display = terminal;
                    const bytes = Uint8Array.from(raw, character => character.charCodeAt(0));
                    display.write(bytes, () => { if (current === socket) pendingOutput -= bytes.length; });
                } else if (message.type === 'exit') {
                    ready = false;
                    status.textContent = `Process exited with status ${Number(message.exitCode)}.`;
                } else throw new Error();
            } catch (_) { stop('Invalid terminal response. Session closed.'); }
        };
        current.onerror = () => {
            if (current === socket) {
                closeNotice = 'Terminal connection failed. Check permissions and Agent availability.';
                status.textContent = closeNotice;
            }
        };
        current.onclose = event => {
            if (current !== socket) return;
            ready = false;
            connect.disabled = false;
            disconnect.disabled = true;
            if (!status.textContent.startsWith('Process exited'))
                status.textContent = closeNotice || (event.code === 1008 ? 'Session closed: access revoked or invalid input.'
                    : event.code === 1009 ? 'Session closed: message too large.'
                    : event.code === 1011 ? 'Terminal failed or the assigned Agent is unavailable. Check the Agent connection before retrying.'
                    : 'Disconnected. Reconnect explicitly to start a new terminal.');
        };
    });
    disconnect.addEventListener('click', () => { send({ type: 'close' }); stop('Disconnected.'); });
    let resizeFrame = 0;
    const observer = new ResizeObserver(() => {
        cancelAnimationFrame(resizeFrame);
        resizeFrame = requestAnimationFrame(() => { if (ready) send({ type: 'resize', ...dimensions() }); });
    });
    observer.observe(container);
    window.addEventListener('pagehide', () => { observer.disconnect(); stop('Disconnected.'); });
})();
