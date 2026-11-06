const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

test('branch choices display commit metadata but submit and search by the original branch name', async () => {
    const listeners = {}, search = { value: 'feature', addEventListener(name, fn) { this[name] = fn; } };
    const select = { options: [], addEventListener() {}, replaceChildren(...options) { this.options = options; }, add(option) { this.options.push(option); } };
    const more = { addEventListener() {} }, refresh = { addEventListener() {} }, message = {};
    const controls = { '[data-choice-search]': search, '[data-choice-select]': select, '[data-choice-more]': more, '[data-choice-message]': message, '[data-choice-refresh]': refresh };
    const choice = { dataset: { url: '/automation/actions/123/choices', name: 'branch', selected: 'feature/qa' }, querySelector(s) { return controls[s]; } };
    let url;
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../../src/Stanza.ControlPlane/wwwroot/js/actions.js'), 'utf8'), {
        document: { getElementById() { return null; }, addEventListener(name, fn) { listeners[name] = fn; }, querySelectorAll() { return [choice]; }, querySelector() { return null; } },
        window: {}, location: { origin: 'https://stanza.test' }, URL, URLSearchParams, AbortController,
        Option: class { constructor(text, value) { this.text = text; this.value = value; } },
        fetch: async value => { url = value; return { ok: true, json: async () => ({ values: ['feature/qa'], labels: { 'feature/qa': 'feature/qa · abc12345 · 2026-10-30 · Alice' }, hasMore: false }) }; },
        setTimeout() {}, clearTimeout() {}
    });
    await new Promise(setImmediate);
    assert.equal(url.searchParams.get('search'), 'feature');
    assert.equal(select.options[1].value, 'feature/qa');
    assert.match(select.options[1].text, /abc12345.*Alice/);
    assert.equal(select.options[1].selected, true);
});

test('a failed branch refresh retains previously loaded choices and the selected value', async () => {
    const select = { options: [{ value: 'feature/qa' }], addEventListener() {}, replaceChildren(...options) { this.options = options; }, add(option) { this.options.push(option); } };
    const message = {}, controls = { '[data-choice-search]': { value: '', addEventListener() {} }, '[data-choice-select]': select,
        '[data-choice-more]': { addEventListener() {} }, '[data-choice-message]': message, '[data-choice-refresh]': { addEventListener() {} } };
    const choice = { dataset: { url: '/choices', name: 'branch', selected: 'feature/qa' }, querySelector(s) { return controls[s]; } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../../src/Stanza.ControlPlane/wwwroot/js/actions.js'), 'utf8'), {
        document: { getElementById() { return null; }, addEventListener() {}, querySelectorAll() { return [choice]; }, querySelector() { return null; } },
        window: {}, location: { origin: 'https://stanza.test' }, URL, URLSearchParams, AbortController,
        Option: class { constructor(text, value) { this.text = text; this.value = value; } },
        fetch: async () => ({ ok: false, json: async () => ({ error: 'Agent discovery is temporarily busy' }) }), setTimeout() {}, clearTimeout() {}
    });
    await new Promise(setImmediate);
    assert.equal(select.options[0].value, 'feature/qa'); assert.match(message.textContent, /temporarily busy/); assert.equal(select.disabled, false);
});
