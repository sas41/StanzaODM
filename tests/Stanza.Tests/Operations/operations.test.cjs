const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.join(__dirname, '../../../src/Stanza.ControlPlane/wwwroot/js/operations.js'), 'utf8');

function setup() {
    const sockets = [];
    const controls = {
        '[data-operation-output]': { textContent: '' }, '[data-output-notice]': { textContent: '' },
        '[data-operation-state]': { textContent: '' }, '[data-operation-code]': { textContent: '' }
    };
    const root = { dataset: { operationId: '12345678-1234-1234-1234-123456789012' }, querySelector(s) { return controls[s]; } };
    class Socket { constructor() { sockets.push(this); } close() {} }
    vm.runInNewContext(script, { document: { querySelector() { return root; } }, WebSocket: Socket,
        window: { addEventListener() {} }, location: { protocol: 'https:', host: 'stanza.test' }, setTimeout() {}, clearTimeout() {} });
    return { controls, message(value) { sockets[0].onmessage({ data: JSON.stringify(value) }); } };
}

test('incomplete history never claims to contain the full process output', () => {
    const s = setup();
    s.message({ state: 'Succeeded', outputAllowed: true, outputIncomplete: true, chunks: [], output: 'retained prefix' });
    assert.match(s.controls['[data-output-notice]'].textContent, /history is incomplete/);
    assert.equal(s.controls['[data-operation-output]'].textContent, 'retained prefix');
});

test('output permission revocation clears displayed text even with incomplete output metadata', () => {
    const s = setup();
    s.message({ state: 'Succeeded', outputAllowed: false, outputIncomplete: true, output: 'secret' });
    assert.equal(s.controls['[data-operation-output]'].textContent, '');
    assert.match(s.controls['[data-output-notice]'].textContent, /do not have permission/);
});
