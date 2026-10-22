const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const script = fs.readFileSync(path.resolve(__dirname, "../../../src/Stanza.ControlPlane/wwwroot/js/status.js"), "utf8");

function start({ present = true, protocol = "http:", websocket = true } = {}) {
    const nodes = new Map(["[data-database-status]", "[data-agent-count]", "[data-pending-agent-count]"]
        .map(selector => [selector, { textContent: "initial" }]));
    const status = { isConnected: true, querySelectorAll: selector => [nodes.get(selector)] };
    const sockets = [];
    const timers = [];
    const listeners = {};
    class FakeSocket {
        constructor(url) { this.url = String(url); sockets.push(this); }
        close(code) { this.closed = code; }
    }
    const window = {
        WebSocket: websocket ? FakeSocket : undefined,
        location: { href: `${protocol}//localhost/`, protocol },
        setTimeout: (callback, delay) => { timers.push({ callback, delay }); return timers.length; },
        clearTimeout: id => { if (timers[id - 1]) timers[id - 1].cancelled = true; },
        addEventListener: (name, callback) => { listeners[name] = callback; }
    };
    vm.runInNewContext(script, {
        window, document: { getElementById: id => id === "system-status" && present ? status : null },
        WebSocket: FakeSocket, URL, Math: Object.assign(Object.create(Math), { random: () => 0 })
    });
    return { nodes, sockets, timers, listeners, status };
}

test("does nothing without dashboard target or WebSocket support", () => {
    assert.equal(start({ present: false }).sockets.length, 0);
    assert.equal(start({ websocket: false }).sockets.length, 0);
});

test("connects to same-origin websocket with TLS matching the document", () => {
    assert.equal(start().sockets[0].url, "ws://localhost/live/status");
    assert.equal(start({ protocol: "https:" }).sockets[0].url, "wss://localhost/live/status");
});

test("updates only textContent, including null counts", () => {
    const { nodes, sockets } = start();
    sockets[0].onmessage({ data: JSON.stringify({ databaseReady: true, agentCount: 5, pendingAgentCount: 2 }) });
    assert.equal(nodes.get("[data-database-status]").textContent, "Connected");
    assert.equal(nodes.get("[data-agent-count]").textContent, "5");
    assert.equal(nodes.get("[data-pending-agent-count]").textContent, "2");
    sockets[0].onmessage({ data: JSON.stringify({ databaseReady: false, agentCount: null, pendingAgentCount: null }) });
    assert.equal(nodes.get("[data-database-status]").textContent, "Unavailable");
    assert.equal(nodes.get("[data-agent-count]").textContent, "—");
});

test("malformed, negative, unsafe, and markup values cannot update status", () => {
    const { nodes, sockets } = start();
    for (const data of ["<script>bad</script>", "null", "{}", ...[-1, 0.5, Number.MAX_SAFE_INTEGER + 1, "<img src=x>"]
        .map(agentCount => JSON.stringify({ databaseReady: true, agentCount, pendingAgentCount: 0 }))]) {
        sockets[0].onmessage({ data });
        assert.equal(nodes.get("[data-database-status]").textContent, "initial");
        assert.equal(nodes.get("[data-agent-count]").textContent, "initial");
    }
});

test("abnormal disconnect reconnects with capped exponential backoff", () => {
    const { sockets, timers } = start();
    const expected = [1000, 2000, 4000, 8000, 16000, 30000, 30000];
    expected.forEach((delay, index) => {
        sockets[index].onclose({ code: 1006 });
        assert.equal(timers[index].delay, delay);
        timers[index].callback();
    });
});

test("a valid status resets reconnect backoff", () => {
    const { sockets, timers } = start();
    sockets[0].onclose({ code: 1006 });
    timers[0].callback();
    sockets[1].onmessage({ data: '{"databaseReady":true,"agentCount":0,"pendingAgentCount":0}' });
    sockets[1].onclose({ code: 1006 });
    assert.equal(timers[1].delay, 1000);
});

test("policy revocation and normal closure do not reconnect", () => {
    for (const code of [1008, 1000]) {
        const { sockets, timers } = start();
        sockets[0].onclose({ code });
        assert.equal(timers.length, 0);
    }
});

test("page exit closes the socket and cancels pending reconnect", () => {
    const { sockets, timers, listeners } = start();
    sockets[0].onclose({ code: 1006 });
    listeners.pagehide();
    assert.equal(sockets[0].closed, 1000);
    assert.equal(timers[0].cancelled, true);
    timers[0].callback();
    assert.equal(sockets.length, 1);
});

test("removed dashboard does not reconnect", () => {
    const { sockets, timers, status } = start();
    sockets[0].onclose({ code: 1006 });
    status.isConnected = false;
    timers[0].callback();
    assert.equal(sockets.length, 1);
});
