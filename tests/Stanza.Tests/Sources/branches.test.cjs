const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function setup(readOnly = false) {
    const listeners = {};
    const input = { value: 'main', readOnly, dispatchEvent(event) { this.event = event; } };
    const form = { querySelector() { return input; } };
    const select = { value: 'refs/heads/new', closest() { return form; } };
    const indicator = { hidden: true };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../../src/Stanza.ControlPlane/wwwroot/js/branches.js'), 'utf8'), {
        document: { addEventListener(name, fn) { listeners[name] = fn; }, getElementById() { return indicator; } },
        Event: class { constructor(type, options) { this.type = type; this.bubbles = options.bubbles; } }
    });
    return { listeners, input, select, indicator };
}

test('selecting a remote reference updates the authored selection without submitting', () => {
    const { listeners, input, select } = setup();
    listeners.change({ target: { closest() { return select; } } });
    assert.equal(input.value, 'refs/heads/new');
    assert.equal(input.event.type, 'input');
    assert.equal(input.event.bubbles, true);
});

test('read-only source reference is never changed', () => {
    const { listeners, input, select } = setup(true);
    listeners.change({ target: { closest() { return select; } } });
    assert.equal(input.value, 'main');
});

test('unrelated changes do not modify selection', () => {
    const { listeners, input } = setup();
    listeners.change({ target: { closest() { return null; } } });
    assert.equal(input.value, 'main');
});

test('branch HTTP errors show actionable fixed text, never server response content', () => {
    const { listeners } = setup();
    const target = { id: 'branches-test', textContent: '' };
    listeners['htmx:responseError']({ detail: { target, xhr: { responseText: 'secret' } } });
    assert.match(target.textContent, /saved reference is unchanged/);
    assert.doesNotMatch(target.textContent, /secret/);
});

test('HTTP errors in unrelated panels remain untouched', () => {
    const { listeners } = setup();
    const target = { id: 'other', textContent: 'original' };
    listeners['htmx:responseError']({ detail: { target } });
    assert.equal(target.textContent, 'original');
});

test('network errors replace the loading state with an actionable retry message', () => {
    const { listeners } = setup();
    const target = { id: 'branches-test', textContent: 'Loading…' };
    listeners['htmx:sendError']({ detail: { target } });
    assert.match(target.textContent, /Refresh or sign in again/);
});

test('loading indicator is shown only while a branch request runs', () => {
    const { listeners, indicator } = setup();
    const event = { detail: { target: { id: 'branches-test' } } };
    assert.equal(indicator.hidden, true);
    listeners['htmx:beforeRequest'](event);
    assert.equal(indicator.hidden, false);
    listeners['htmx:afterRequest'](event);
    assert.equal(indicator.hidden, true);
});
