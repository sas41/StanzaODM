const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const script = fs.readFileSync(path.resolve(__dirname, '../../../src/Stanza.ControlPlane/wwwroot/js/logs.js'), 'utf8');

function start({ present = true, protocol = 'https:', targetType = 'service', target = 'web' } = {}) {
    function node() {
        return { textContent: '', style: {}, handlers: {}, disabled: false, scrollTop: 0, clientHeight: 100, scrollHeight: 100,
            addEventListener(name, handler) { this.handlers[name] = handler; },
            set innerHTML(_) { throw new Error('Untrusted HTML must never be interpreted.'); } };
    }
    const display = node(), status = node(), connect = node(), disconnect = node(), clear = node(), form = node();
    form.dataset = { deploymentId: '00000000-0000-0000-0000-000000000001' };
    form.fields = { targetType, target };
    form.querySelector = () => connect;
    const nodes = { 'logs-connect': present ? form : null, 'container-logs': display, 'logs-status': status,
        'logs-disconnect': disconnect, 'logs-clear': clear };
    const sockets = [], listeners = {};
    class FakeSocket {
        static OPEN = 1;
        static CLOSING = 2;
        static CLOSED = 3;
        constructor(url) { this.url = String(url); this.readyState = 0; this.sent = []; sockets.push(this); }
        send(data) { this.sent.push(JSON.parse(data)); }
        close(code) { this.closed = code; this.readyState = FakeSocket.CLOSED; this.onclose?.({ code }); }
        open() { this.readyState = FakeSocket.OPEN; this.onopen(); }
        message(value) { this.onmessage({ data: JSON.stringify(value) }); }
    }
    vm.runInNewContext(script, { document: { getElementById: id => nodes[id] }, window: { addEventListener: (name, handler) => listeners[name] = handler },
        WebSocket: FakeSocket, FormData: class { constructor(element) { this.fields = element.fields; } get(key) { return this.fields[key]; } },
        location: { protocol, origin: `${protocol}//control.example` }, URL, TextEncoder, TextDecoder, Uint8Array, atob });
    function submit() { form.handlers.submit({ preventDefault() {} }); return sockets.at(-1); }
    function open() { const socket = submit(); socket.open(); socket.message({ type: 'ready' }); return socket; }
    return { display, status, connect, disconnect, clear, form, sockets, listeners, submit, open };
}

const output = data => ({ type: 'output', data: Buffer.from(data).toString('base64') });

test('does nothing without the log form and never connects before explicit submit', () => {
    assert.equal(start({ present: false }).sockets.length, 0);
    assert.equal(start().sockets.length, 0);
});

test('native WebSocket uses exact origin/TLS and never query tokens or HTTP polling', () => {
    for (const protocol of ['http:', 'https:']) {
        const app = start({ protocol });
        const socket = app.open();
        const url = new URL(socket.url);
        assert.equal(url.protocol, protocol === 'https:' ? 'wss:' : 'ws:');
        assert.equal(url.host, 'control.example');
        assert.equal(url.pathname, '/live/logs/00000000-0000-0000-0000-000000000001');
        assert.equal(url.search, '');
        assert.equal(url.username, '');
        assert.equal(url.password, '');
        assert.equal(app.sockets.length, 1);
    }
});

test('handshake contains only a typed service or container selection', () => {
    for (const targetType of ['service', 'container']) {
        const socket = start({ targetType, target: 'app_1.2-3' }).open();
        assert.deepEqual(socket.sent, [{ type: 'open', service: targetType === 'service' ? 'app_1.2-3' : null,
            container: targetType === 'container' ? 'app_1.2-3' : null }]);
    }
});

test('invalid and oversized targets cannot start a connection', () => {
    for (const target of ['', '-host', '/host', 'web;other', 'web other', 'é', 'a'.repeat(129)]) {
        const app = start({ target });
        app.submit();
        assert.equal(app.sockets.length, 0);
        assert.match(app.status.textContent, /valid Compose service/);
    }
});

test('log contents are textContent only, never HTML or executable script', () => {
    const app = start(), socket = app.open();
    const text = '<img src=x onerror=alert(1)>\n<script>throw Error()</script>\n';
    socket.message(output(text));
    assert.equal(app.display.textContent, text);
});

test('UTF-8 decoder preserves multi-byte characters across output frames', () => {
    const app = start(), socket = app.open();
    const bytes = Buffer.from('😀漢');
    socket.message(output(bytes.subarray(0, 2)));
    assert.equal(app.display.textContent, '');
    socket.message(output(bytes.subarray(2, 5)));
    socket.message(output(bytes.subarray(5)));
    assert.equal(app.display.textContent, '😀漢');
});

test('scrollback remains at most 256 KiB of UTF-8 and truncation preserves codepoint boundaries', () => {
    const app = start(), socket = app.open();
    for (let index = 0; index < 8; index++) socket.message(output('漢'.repeat(21000)));
    socket.message(output('last line\n'));
    assert.ok(Buffer.byteLength(app.display.textContent, 'utf8') <= 256 * 1024);
    assert.ok(app.display.textContent.endsWith('last line\n'));
    assert.ok(!app.display.textContent.includes('\ufffd'));
});

test('scroll follows output only when the user remains at the bottom', () => {
    const app = start(), socket = app.open();
    socket.message(output('line\n'));
    assert.equal(app.display.scrollTop, 100);
    app.display.scrollHeight = 1000;
    app.display.scrollTop = 0;
    socket.message(output('another\n'));
    assert.equal(app.display.scrollTop, 0);
});

test('malformed, unexpected, oversized and invalid base64 server data fails closed', () => {
    const invalid = ['invalid', 'null', '{}', JSON.stringify({ type: 'unexpected' }),
        JSON.stringify({ type: 'output', data: 'invalid!' }), JSON.stringify({ type: 'output', data: 3 }),
        JSON.stringify(output(Buffer.alloc(65537))), JSON.stringify({ type: 'end', code: '0' }), 'x'.repeat(92161)];
    for (const data of invalid) {
        const app = start(), socket = app.open();
        socket.onmessage({ data });
        assert.equal(socket.closed, 1000);
        assert.match(app.status.textContent, /Invalid log response/);
    }
    const app = start(), socket = app.submit();
    socket.open();
    socket.message(output('before-ready'));
    assert.equal(socket.closed, 1000);
    assert.equal(app.display.textContent, '');
});

test('revocation and errors never automatically reconnect; manual reconnect clears duplicate history', () => {
    for (const code of [1000, 1006, 1008, 1009, 1011]) {
        const app = start(), socket = app.open();
        socket.message(output('old history'));
        socket.close(code);
        assert.equal(app.sockets.length, 1);
        assert.equal(app.connect.disabled, false);
        if (code === 1008) assert.match(app.status.textContent, /revoked/);
        app.open();
        assert.equal(app.sockets.length, 2);
        assert.equal(app.display.textContent, '');
    }
});

test('end, explicit disconnect, clear and page exit never send terminal input or resize', () => {
    const app = start(), socket = app.open();
    socket.message(output('contents'));
    app.clear.handlers.click();
    assert.equal(app.display.textContent, '');
    socket.message({ type: 'end', code: 0 });
    socket.close(1000);
    assert.match(app.status.textContent, /Log stream ended/);
    const next = app.open();
    app.disconnect.handlers.click();
    assert.equal(next.sent.at(-1).type, 'close');
    assert.deepEqual(next.sent.map(message => message.type), ['open', 'close']);
    const last = app.open();
    app.listeners.pagehide();
    assert.equal(last.closed, 1000);
    assert.equal(app.sockets.length, 3);
});

test('old socket events cannot mutate a manually reconnected stream', () => {
    const app = start(), first = app.open();
    first.close(1000);
    const second = app.open();
    second.message(output('new'));
    first.message(output('old'));
    first.onclose({ code: 1008 });
    assert.equal(app.display.textContent, 'new');
    assert.match(app.status.textContent, /Following/);
});
