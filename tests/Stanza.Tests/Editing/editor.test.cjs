const test = require('node:test');
const assert = require('node:assert/strict');
const editor = require('../../../src/Stanza.ControlPlane/wwwroot/js/editor.js');

test('tokens preserve every raw character including mixed endings and malicious HTML', () => {
    for (const text of ['# comment\r\nservices:\n  app: "<script>evil</script>"\r', '\uFEFFKEY=${VALUE}\n', "A='multi\nline'\r\nEMPTY=\n", 'FROM scratch\nCOPY . /app\n']) {
        assert.equal(editor.tokens(text, 'compose').map(x => x.text).join(''), text);
    }
});

test('environment keys and Compose keys receive visual hints, not parsing or rewriting', () => {
    assert.equal(editor.tokens('SECRET=abc', 'env')[0].kind, 'key');
    assert.equal(editor.tokens('services: {}', 'compose')[0].kind, 'key');
    assert.equal(editor.tokens('RUN echo safe', 'text')[0].kind, 'keyword');
});

test('render uses only textContent; HTML strings never become elements', () => {
    const nodes = [];
    const document = {
        createDocumentFragment: () => ({ appendChild: node => nodes.push(node) }),
        createElement: tag => { assert.equal(tag, 'span'); return {}; }
    };
    let replaced = false;
    editor.render({ replaceChildren: () => replaced = true }, '<img src=x onerror=evil>\n# hi', 'text', document);
    assert.ok(replaced);
    assert.equal(nodes.map(x => x.textContent).join(''), '<img src=x onerror=evil>\n# hi');
    assert.ok(nodes.every(x => !Object.hasOwn(x, 'innerHTML')));
});

test('initialization preserves form control value and scroll while attaching a mirror', () => {
    const original = 'services:\n  app: {}\n';
    const events = {};
    let valueWrites = 0;
    let wrapper;
    let preview;
    const element = {
        tagName: 'TEXTAREA', dataset: { language: 'compose' }, classList: { add() {} },
        scrollTop: 22, scrollLeft: 11, setAttribute() {},
        get value() { return original; }, set value(_) { valueWrites++; },
        parentNode: { insertBefore(node) { wrapper = node; } },
        addEventListener(name, callback) { events[name] = callback; }
    };
    const document = {
        querySelectorAll: () => [element],
        createDocumentFragment: () => ({ appendChild() {} }),
        createElement: tag => {
            const node = { appendChild() {}, setAttribute() {}, replaceChildren() {} };
            if (tag === 'pre') preview = node;
            return node;
        }
    };
    editor.initialize(document);
    assert.ok(wrapper);
    assert.equal(preview.scrollTop, 22);
    assert.equal(preview.scrollLeft, 11);
    events.input(); events.scroll(); editor.initialize(document);
    assert.equal(valueWrites, 0);
    assert.equal(element.value, original);
});

test('pathological token counts are bounded for DOM rendering without losing suffix bytes', () => {
    const text = '"x" '.repeat(100000);
    const nodes = [];
    const document = {
        createDocumentFragment: () => ({ appendChild: node => nodes.push(node) }),
        createElement: () => ({})
    };
    editor.render({ replaceChildren() {} }, text, 'text', document);
    assert.ok(nodes.length < Math.sqrt(text.length) + 4);
    assert.equal(nodes.map(x => x.textContent).join(''), text);
});
