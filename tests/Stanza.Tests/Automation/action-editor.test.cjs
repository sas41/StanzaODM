const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

// Small DOM double: browser integration tests exercise the real form/HTMX wiring.
class Element {
    constructor(tag = 'div') { this.tag = tag; this.children = []; this.dataset = {}; this.listeners = {}; this.hidden = false; this._value = ''; }
    set value(value) { this._value = this.tag === 'textarea' ? String(value).replace(/\r\n?/g, '\n') : String(value); }
    get value() { return this._value; }
    get options() { return this.children; }
    append(...children) { for (const child of children) { this.children.push(child); child.parentElement = this; } }
    add(option) { this.append(option); }
    replaceChildren(...children) { this.children = []; this.append(...children); }
    addEventListener(name, fn) { (this.listeners[name] ??= []).push(fn); }
    fire(name, detail = {}) { const event = { detail, target: this, prevented: false, preventDefault() { this.prevented = true; } }; for (const fn of this.listeners[name] ?? []) fn(event); return event; }
    cloneNode() { const clone = new Element(this.tag); clone.value = this.value; clone.dataset = { ...this.dataset }; return clone; }
    remove() { this.parentElement.children = this.parentElement.children.filter(c => c !== this); }
}
const option = class extends Element { constructor(text, value) { super('option'); this.textContent = text; this.value = value; } };

function editor(plan, htmx = undefined) {
    const root = new Element(), form = new Element('form'), json = new Element('textarea');
    json.value = JSON.stringify(plan); root.dataset = { deployment: plan.deploymentId ?? '', agent: '' };
    const variables = new Element(), steps = new Element(), metadata = new Element(), error = new Element();
    const addVariable = new Element('button'), addStep = new Element('button'), details = new Element('details'), ui = new Element();
    const template = { content: { cloneNode() { return new Element(); } } };
    const nodes = { '[name=PlanJson]': json, '[data-workflow-variables]': variables, '[data-workflow-steps]': steps,
        '[data-workflow-metadata]': metadata, '[data-workflow-error]': error, '[data-workflow-ui]': ui,
        '[data-workflow-sources]': template, '[data-workflow-deployments]': template,
        '[data-workflow-add="variable"]': addVariable, '[data-workflow-add="step"]': addStep, details };
    root.querySelector = s => nodes[s]; root.closest = () => form;
    form.querySelector = () => null;
    const document = { querySelectorAll: () => [root], addEventListener() {}, createElement: tag => {
        if (tag === 'template') return { content: new Element() };
        return new Element(tag);
    } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../../src/Stanza.ControlPlane/wwwroot/js/action-editor.js'), 'utf8'), { document, window: { htmx }, Option: option, Event: class {} });
    function find(parent, title) {
        for (const child of parent.children) {
            if (child.tag === 'label' && child.children[0]?.textContent === title) return child.children[1];
        }
        throw new Error('Missing control: ' + title);
    }
    return { form, json, variables, steps, metadata, error, addVariable, addStep, find, save() {
        const event = form.fire('submit'); return { event, plan: JSON.parse(json.value) };
    } };
}

const plan = () => ({ deploymentId: 'deployment', buttonLabel: 'Custom', order: 19, showOnOverview: false, confirmation: 'Proceed?',
    parameters: [{ name: 'MESSAGE', type: 'string', required: false, default: '', input: 'text' }],
    steps: [{ kind: 'container-script', deploymentId: 'deployment', timeoutSeconds: 123, failure: 'continue',
        execution: { executable: '/bin/bash', arguments: [], mode: 'script', script: 'echo "$MESSAGE"\r\n', service: null,
            services: ['app', 'worker'], replicas: 'all', environment: { FIXED: 'literal' } } }] });

test('workflow editor preserves scripts byte-for-byte, variables, replica choice and customized metadata', () => {
    const original = plan(), e = editor(original), saved = e.save().plan;
    assert.equal(saved.steps[0].execution.script, original.steps[0].execution.script);
    assert.deepEqual(saved.steps[0].execution.services, ['app', 'worker']);
    assert.equal(saved.steps[0].execution.replicas, 'all');
    assert.equal(saved.steps[0].timeoutSeconds, 123); assert.equal(saved.steps[0].failure, 'continue');
    assert.equal(saved.parameters[0].default, ''); assert.equal(saved.buttonLabel, 'Custom');
    assert.equal(saved.order, 19); assert.equal(saved.showOnOverview, false); assert.equal(saved.confirmation, 'Proceed?');
});

test('adding variables and script steps produces structured plans, with once per service as the default', () => {
    const e = editor(plan()); e.addVariable.fire('click'); e.addStep.fire('click');
    e.find(e.variables.children[1], 'Variable name').value = 'MODE';
    const last = e.steps.children[1];
    e.find(last, 'Container services (comma-separated)').value = 'api, jobs';
    const script = last.children.find(c => c.tag === 'label' && c.children[1]?.dataset.workflowScript !== undefined).children[1];
    script.value = 'echo "$MODE"';
    const saved = e.save().plan;
    assert.equal(saved.parameters[1].name, 'MODE');
    assert.equal(saved.steps[1].kind, 'container-script');
    assert.deepEqual(saved.steps[1].execution.services, ['api', 'jobs']);
    assert.equal(saved.steps[1].execution.replicas, 'once');
    assert.equal(saved.steps[1].execution.script, 'echo "$MODE"');
});

test('explicit advanced JSON edits are not overwritten by visual controls', () => {
    const e = editor(plan()), advanced = { ...plan(), buttonLabel: 'Advanced' };
    e.json.value = JSON.stringify(advanced); e.json.fire('input');
    assert.equal(e.save().plan.buttonLabel, 'Advanced');
});

test('explicitly clearing deployment scope produces a global plan without silently retargeting its steps', () => {
    const e = editor(plan());
    e.form.querySelector = s => s === '[name=DeploymentId]' ? { value: '' } : null;
    const saved = e.save().plan;
    assert.equal(saved.deploymentId, null); assert.equal(saved.steps[0].deploymentId, 'deployment');
});

test('invalid environment JSON prevents submission and leaves the saved plan unchanged', () => {
    const e = editor(plan()), initial = e.json.value;
    e.find(e.steps.children[0], 'Fixed environment (JSON object)').value = '{bad';
    const event = e.form.fire('submit');
    assert.equal(event.prevented, true); assert.equal(e.json.value, initial);
    assert.match(e.error.textContent, /fixed environment JSON/);
});

test('HTMX request parameters include the generated plan, including changes after an earlier submit', () => {
    const e = editor(plan()); e.save();
    e.find(e.metadata, 'Button label').value = 'After validation';
    const event = e.form.fire('htmx:configRequest', { elt: e.form, parameters: {} });
    assert.equal(JSON.parse(event.detail.parameters.PlanJson).buttonLabel, 'After validation');
});

test('newly initialized forms have HTMX handlers attached immediately rather than waiting for settle', () => {
    const processed = [];
    const e = editor(plan(), { process(form) { processed.push(form); } });
    assert.deepEqual(processed, [e.form]);
});
