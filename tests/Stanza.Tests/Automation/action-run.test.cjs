const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.join(__dirname, '../../../src/Stanza.ControlPlane/wwwroot/js/action-run.js'), 'utf8');

function setup({ preference = null, initial = 'queued', blocked = false, storageBlocked = false } = {}) {
    const listeners = {}, soundListeners = {}, testListeners = {}, sockets = [], notes = [], saved = [];
    const sound = { checked: true, addEventListener(name, fn) { soundListeners[name] = fn; } };
    const status = { textContent: '' }, soundStatus = { textContent: '' };
    const sections = [];
    const controls = {
        '[data-completion-sound]': sound, '[data-sound-status]': soundStatus, '[data-feed-status]': status,
        '[data-test-sound]': { addEventListener(name, fn) { testListeners[name] = fn; } },
        '[data-feed-operations]': { append(section) { sections.push(section); } }, '[data-feed-follow]': { checked: false }
    };
    const feed = { dataset: { actionOutput: '12345678-1234-1234-1234-123456789012', initialState: initial }, querySelector(s) { return controls[s]; } };
    class Socket {
        constructor(url) { this.url = url; sockets.push(this); }
        close() { }
    }
    class Audio {
        state = blocked ? 'suspended' : 'running'; currentTime = 0;
        async resume() { }
        createOscillator() {
            const note = { frequency: { value: 0 }, connect() { }, start() { notes.push({ type: this.type, frequency: this.frequency.value }); }, stop() { } };
            return note;
        }
        createGain() { return { gain: { setValueAtTime() { }, exponentialRampToValueAtTime() { } }, connect() { } }; }
    }
    vm.runInNewContext(script, {
        document: { querySelector() { return feed; }, getElementById() { return { dataset: {} }; }, addEventListener(name, fn) { listeners[name] = fn; },
            createElement() { return { textContent: '', dataset: {}, children: [], append(...children) { this.children.push(...children); } }; } },
        window: { AudioContext: Audio, addEventListener(name, fn) { listeners[name] = fn; } },
        WebSocket: Socket, location: { protocol: 'https:', host: 'stanza.test' },
        localStorage: {
            getItem() { if (storageBlocked) throw Error('private storage'); return preference; },
            setItem(key, value) { if (storageBlocked) throw Error('private storage'); saved.push([key, value]); }
        }, setTimeout() { }, clearTimeout() { }
    });
    return { sound, soundStatus, notes, saved, listeners, soundListeners, testListeners, sockets, sections,
        async message(state, operations = []) { sockets.at(-1).onmessage({ data: JSON.stringify({ version: 'a'.repeat(64), operations, state }) }); await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); } };
}

test('sound defaults on and succeeds with a bell exactly once despite duplicate frames', async () => {
    const s = setup(); assert.equal(s.sound.checked, true);
    await s.message('running'); assert.equal(s.notes.length, 0);
    await s.message('succeeded'); assert.deepEqual(s.notes.map(n => n.frequency), [880, 1320]);
    await s.message('succeeded'); assert.equal(s.notes.length, 2);
});

for (const state of ['failed', 'cancelled', 'denied', 'skipped', 'uncertain']) {
    test(`non-success terminal ${state} plays a distinct bad sound`, async () => {
        const s = setup(); await s.message(state);
        assert.deepEqual(s.notes.map(n => n.frequency), [220, 165]);
    });
}

test('browser preference persists and off suppresses completion audio', async () => {
    const s = setup({ preference: 'off' }); assert.equal(s.sound.checked, false);
    await s.message('succeeded'); assert.equal(s.notes.length, 0);
    s.sound.checked = true; s.soundListeners.change();
    assert.deepEqual(s.saved, [['stanza.completion-sound', 'on']]);
});

test('opening a historical completed run never rings', async () => {
    const s = setup({ initial: 'succeeded' }); await s.message('succeeded'); assert.equal(s.notes.length, 0);
});

test('blocked browser audio offers an explicit enable button rather than claiming success', async () => {
    const s = setup({ blocked: true }); await s.message('failed');
    assert.equal(s.notes.length, 0); assert.match(s.soundStatus.textContent, /blocked/);
});

test('private storage failure does not break the live feed or default-on audio', async () => {
    const s = setup({ storageBlocked: true }); await s.message('succeeded'); assert.equal(s.notes.length, 2);
    s.soundListeners.change();
});

test('test-sound button provides an audio gesture independently of the notification preference', async () => {
    const s = setup({ preference: 'off' }); await s.testListeners.click(); assert.equal(s.notes.length, 2);
});

test('storage-related output loss is explicitly shown independently of execution success', async () => {
    const s = setup();
    await s.message('succeeded', [{ id: '12345678-1234-1234-1234-123456789012', state: 'Succeeded', stage: 'Checking',
        outputAllowed: true, outputIncomplete: true, chunks: [] }]);
    assert.match(s.sections[0].children[1].textContent, /Output history incomplete/);
    assert.match(s.sections[0].children[1].textContent, /^Succeeded/);
});
