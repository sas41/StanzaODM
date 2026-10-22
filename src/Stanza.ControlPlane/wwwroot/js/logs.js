(() => {
    'use strict';
    const form = document.getElementById('logs-connect');
    if (!form) return;
    const display = document.getElementById('container-logs');
    const status = document.getElementById('logs-status');
    const disconnect = document.getElementById('logs-disconnect');
    const connect = form.querySelector('button[type="submit"]');
    const maximumDisplayBytes = 256 * 1024;
    const encoder = new TextEncoder();
    let decoder = new TextDecoder();
    let socket = null;
    let ready = false;
    let ended = false;
    let closeNotice = null;
    display.style.maxHeight = '60vh';
    display.style.overflow = 'auto';

    function append(text) {
        if (!text) return;
        const following = display.scrollTop + display.clientHeight >= display.scrollHeight - 32;
        const bytes = encoder.encode(display.textContent + text);
        let start = Math.max(0, bytes.length - maximumDisplayBytes);
        // Never begin the retained text halfway through a UTF-8 code point.
        while (start < bytes.length && (bytes[start] & 0xc0) === 0x80) start++;
        display.textContent = new TextDecoder().decode(bytes.subarray(start));
        if (following) display.scrollTop = display.scrollHeight;
    }
    function send(message) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return;
        socket.send(JSON.stringify(message));
    }
    function stop(message) {
        closeNotice = message;
        ready = false;
        if (socket && socket.readyState < WebSocket.CLOSING) socket.close(1000, 'Logs closed.');
        status.textContent = message;
    }
    form.addEventListener('submit', event => {
        event.preventDefault();
        if (socket && socket.readyState < WebSocket.CLOSED) return;
        const fields = new FormData(form);
        const target = fields.get('target');
        if (!/^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$/.test(target)) {
            status.textContent = 'Enter a valid Compose service or container name/ID (at most 128 characters).';
            return;
        }
        const handshake = {
            type: 'open', service: fields.get('targetType') === 'service' ? target : null,
            container: fields.get('targetType') === 'container' ? target : null
        };
        const url = new URL(`/live/logs/${encodeURIComponent(form.dataset.deploymentId)}`, location.origin);
        url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        decoder = new TextDecoder();
        display.textContent = '';
        ready = ended = false;
        closeNotice = null;
        const current = socket = new WebSocket(url);
        connect.disabled = true;
        disconnect.disabled = false;
        status.textContent = 'Connecting to the assigned Agent…';
        current.onopen = () => { if (current === socket) send(handshake); };
        current.onmessage = event => {
            if (current !== socket) return;
            try {
                if (typeof event.data !== 'string' || event.data.length > 92160) throw new Error();
                const message = JSON.parse(event.data);
                if (message.type === 'ready' && !ready && !ended) {
                    ready = true;
                    status.textContent = 'Following container logs. Reconnects are manual and may repeat recent lines.';
                } else if (message.type === 'output' && ready && typeof message.data === 'string') {
                    const raw = atob(message.data);
                    if (raw.length > 65536) throw new Error();
                    append(decoder.decode(Uint8Array.from(raw, character => character.charCodeAt(0)), { stream: true }));
                } else if (message.type === 'end' && ready && Number.isInteger(message.code)) {
                    append(decoder.decode());
                    ready = false;
                    ended = true;
                    status.textContent = `Log stream ended (status ${message.code}). Reconnect explicitly to read recent logs again.`;
                } else throw new Error();
            } catch (_) { stop('Invalid log response. Stream closed.'); }
        };
        current.onerror = () => {
            if (current !== socket) return;
            closeNotice = 'Log connection failed. Check permissions, target name, and Agent availability.';
            status.textContent = closeNotice;
        };
        current.onclose = event => {
            if (current !== socket) return;
            ready = false;
            connect.disabled = false;
            disconnect.disabled = true;
            if (!ended) status.textContent = closeNotice || (event.code === 1008 ? 'Log access revoked or invalid request. Reconnect only after permissions are restored.'
                : event.code === 1009 ? 'Log stream closed: message too large.'
                : event.code === 1011 ? 'Log stream failed or the assigned Agent is unavailable. Check the target and Agent before retrying.'
                : 'Disconnected. Reconnect explicitly; recent lines may repeat.');
        };
    });
    disconnect.addEventListener('click', () => { send({ type: 'close' }); stop('Disconnected. Reconnect explicitly; recent lines may repeat.'); });
    document.getElementById('logs-clear').addEventListener('click', () => { display.textContent = ''; });
    window.addEventListener('pagehide', () => stop('Disconnected.'));
})();
