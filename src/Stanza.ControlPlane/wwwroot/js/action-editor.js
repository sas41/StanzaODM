(() => {
    "use strict";
    const initialized = new WeakSet();
    let editorCounter = 0;
    function initialize(root) {
    if (initialized.has(root)) return;
    initialized.add(root);
    const form = root.closest("form"), json = form.querySelector("[name=PlanJson]"), advanced = form.querySelector("[data-advanced-plan]");
    const inputs = root.querySelector("[data-editor-inputs]"), steps = root.querySelector("[data-editor-steps]");
    let plan;
    try { plan = JSON.parse(json.value); } catch { advanced.open = true; return; }
    const initialJson = json.value;
    const prefix = "action-editor-" + (++editorCounter);
    let nextControl = 0;
    const field = (parent, title, key, value = "", options = null, type = "text") => {
        const label = document.createElement("label"); label.className = type === "checkbox" ? "check-field" : "field";
        const control = document.createElement(options ? "select" : type === "textarea" ? "textarea" : "input");
        control.id = prefix + "-control-" + (++nextControl);
        label.htmlFor = control.id;
        control.dataset.field = key;
        if (options) for (const option of options) control.add(new Option(option, option));
        else if (type !== "textarea") control.type = type;
        if (type === "checkbox") control.checked = Boolean(value); else control.value = value ?? "";
        if (type === "checkbox") label.append(control, document.createTextNode(title));
        else { const text = document.createElement("span"); text.textContent = title; label.append(text, control); }
        parent.append(label); return control;
    };
    const catalog = (parent, title, key, value, template) => {
        const select = field(parent, title, key, "", []);
        select.append(document.getElementById(template).content.cloneNode(true));
        if (value && !Array.from(select.options).some(o => o.value === value)) select.add(new Option("Unavailable: " + value, value));
        select.value = value ?? ""; return select;
    };
    const row = (parent, title) => {
        const section = document.createElement("section"); section.className = "panel action-editor-row";
        const header = document.createElement("div"); header.className = "panel-heading";
        const heading = document.createElement("h3"); heading.textContent = title; header.append(heading);
        const toolbar = document.createElement("div"); toolbar.className = "toolbar"; header.append(toolbar); section.append(header);
        const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "Remove " + title.toLowerCase();
        remove.className = "danger";
        remove.addEventListener("click", () => section.remove()); toolbar.append(remove);
        for (const [label, up] of [["Move up", true], ["Move down", false]]) {
            const move = document.createElement("button"); move.type = "button"; move.textContent = label;
            move.setAttribute("aria-label", label + " " + title.toLowerCase());
            move.addEventListener("click", () => {
                const sibling = up ? section.previousElementSibling : section.nextElementSibling;
                if (sibling) parent.insertBefore(up ? section : sibling, up ? sibling : section);
            });
            toolbar.append(move);
        }
        parent.append(section); return section;
    };
    function addInput(p = {}) {
        const item = row(inputs, "Input");
        item.classList.add("action-input-row");
        field(item, "Variable name", "name", p.name);
        field(item, "Label", "label", p.label);
        const input = field(item, "Input control", "input", p.input || "text", ["text", "dropdown", "version", "branch"]);
        const valueType = field(item, "Value type", "type", p.type || "string", ["string", "integer", "boolean"]);
        const required = field(item, "Required", "required", p.required !== false, null, "checkbox");
        field(item, "Default value (optional)", "default", p.default);
        const options = field(item, "Dropdown choices (one per line)", "options", (p.options || []).join("\n"), null, "textarea");
        options.parentElement.classList.add("span-full");
        const source = catalog(item, "Git source checkout destination", "source", p.source, "action-source-options");
        for (const option of Array.from(source.options))
            if (option.dataset.deployment && option.dataset.deployment !== form.querySelector("[data-plan-deployment]").value) option.remove();
        const repository = field(item, "Image repository (without tag)", "repository", p.repository);
        repository.placeholder = "ghcr.io/organization/application";
        const services = field(item, "Target Compose services (comma-separated)", "services", (p.services || []).join(", "));
        function visibility() {
            valueType.parentElement.hidden = input.value !== "text";
            if (input.value !== "text") valueType.value = "string";
            required.parentElement.hidden = ["branch", "version"].includes(input.value);
            if (required.parentElement.hidden) required.checked = true;
            options.parentElement.hidden = input.value !== "dropdown";
            source.parentElement.hidden = input.value !== "branch";
            repository.parentElement.hidden = services.parentElement.hidden = input.value !== "version";
        }
        input.addEventListener("change", visibility); visibility();
    }
    function addStep(s = {}) {
        const item = row(steps, "Step");
        item.classList.add("action-step-row");
        const kind = field(item, "Operation", "kind", s.kind || "deploy", ["validate", "git-refresh", "build", "pull", "deploy", "start", "stop", "restart", "remove", "status", "logs", "container-exec", "host-exec"]);
        const deployment = catalog(item, "Deployment", "deploymentId", s.deploymentId || form.querySelector("[data-plan-deployment]").value, "action-deployment-options");
        const agent = catalog(item, "Agent", "agentId", s.agentId, "action-agent-options");
        field(item, "On failure", "failure", s.failure || "stop", ["stop", "continue"]);
        const command = document.createElement("div"); command.className = "execution-fields"; item.append(command);
        const exec = s.execution || {};
        field(command, "Executable (absolute path)", "executable", exec.executable);
        const args = field(command, "Arguments (one per line; use ${variable} as a whole argument)", "arguments", (exec.arguments || []).join("\n"), null, "textarea");
        args.placeholder = "--database\ntesting";
        const service = field(command, "Compose service (container-exec)", "service", exec.service);
        const container = field(command, "Or explicit container (container-exec)", "container", exec.container);
        const advanced = document.createElement("details"); const summary = document.createElement("summary"); summary.textContent = "User, working directory & fixed environment"; advanced.append(summary);
        const advancedFields = document.createElement("div"); advancedFields.className = "form-grid"; advanced.append(advancedFields); command.append(advanced);
        field(advancedFields, "User (optional)", "user", exec.user);
        field(advancedFields, "Working directory (optional)", "workingDirectory", exec.workingDirectory);
        field(advancedFields, "Fixed environment (JSON object, optional)", "environment", exec.environment ? JSON.stringify(exec.environment) : "", null, "textarea").parentElement.classList.add("span-full");
        advanced.open = Boolean(exec.user || exec.workingDirectory || exec.environment);
        function visibility() {
            const scope = form.querySelector("[data-plan-deployment]").value;
            for (const option of kind.options) option.disabled = Boolean(scope) && option.value === "host-exec";
            for (const option of deployment.options) option.disabled = Boolean(scope) && option.value !== scope;
            if (scope && !deployment.value && kind.value !== "host-exec") deployment.value = scope;
            agent.parentElement.hidden = kind.value !== "host-exec";
            deployment.parentElement.hidden = kind.value === "host-exec";
            command.hidden = !["container-exec", "host-exec"].includes(kind.value);
            service.parentElement.hidden = container.parentElement.hidden = kind.value === "host-exec";
        }
        kind.addEventListener("change", visibility); visibility();
    }
    const metadata = {
        deploymentId: "[data-plan-deployment]", buttonLabel: "[data-plan-label]", order: "[data-plan-order]",
        confirmation: "[data-plan-confirmation]", showOnOverview: "[data-plan-visible]"
    };
    for (const [key, selector] of Object.entries(metadata)) {
        const control = form.querySelector(selector);
        if (key === "deploymentId" && plan[key] && !Array.from(control.options).some(o => o.value === plan[key])) control.add(new Option("Unavailable deployment scope: " + plan[key], plan[key]));
        if (control.type === "checkbox") control.checked = plan[key] !== false; else control.value = plan[key] ?? (key === "order" ? "0" : "");
    }
    (plan.parameters || []).forEach(addInput); (plan.steps || []).forEach(addStep);
    const displayDeployment = form.querySelector("[data-plan-deployment]");
    let initializingScope = true;
    displayDeployment.addEventListener("change", () => {
        for (const item of steps.children) {
            const select = item.querySelector("select[data-field=deploymentId]");
            if (!initializingScope && displayDeployment.value) select.value = displayDeployment.value;
            item.querySelector("[data-field=kind]").dispatchEvent(new Event("change"));
        }
        for (const select of inputs.querySelectorAll("select[data-field=source]")) {
            const value = select.value;
            select.replaceChildren(document.getElementById("action-source-options").content.cloneNode(true));
            for (const option of Array.from(select.options)) if (option.dataset.deployment && option.dataset.deployment !== displayDeployment.value) option.remove();
            select.value = value;
        }
    });
    displayDeployment.dispatchEvent(new Event("change"));
    initializingScope = false;
    root.querySelector("[data-add-input]").addEventListener("click", () => addInput());
    root.querySelector("[data-add-step]").addEventListener("click", () => addStep());
    const values = item => Object.fromEntries(Array.from(item.querySelectorAll("[data-field]")).map(c => [c.dataset.field, c.type === "checkbox" ? c.checked : c.value]));
    form.addEventListener("submit", event => {
        if (json.value !== initialJson) return;
        try {
            for (const [key, selector] of Object.entries(metadata)) {
                const c = form.querySelector(selector); plan[key] = c.type === "checkbox" ? c.checked : key === "order" ? Number(c.value) : c.value || null;
            }
            plan.parameters = Array.from(inputs.children).map(item => {
                const p = values(item);
                p.default = p.default === "" ? null : p.default; p.label ||= null;
                p.options = p.input === "dropdown" ? p.options.split("\n").filter(v => v !== "") : null;
                p.source = p.input === "branch" ? p.source : null;
                p.repository = p.input === "version" ? p.repository : null;
                p.services = p.input === "version" ? p.services.split(",").map(v => v.trim()).filter(Boolean) : null;
                return p;
            });
            plan.steps = Array.from(steps.children).map(item => {
                const s = values(item);
                const execution = ["container-exec", "host-exec"].includes(s.kind) ? {
                    executable: s.executable, arguments: s.arguments === "" ? [] : s.arguments.split("\n"), service: s.kind === "host-exec" ? null : s.service || null,
                    container: s.kind === "host-exec" ? null : s.container || null, user: s.user || null, workingDirectory: s.workingDirectory || null,
                    environment: s.environment ? JSON.parse(s.environment) : null
                } : null;
                return { kind: s.kind, deploymentId: s.kind === "host-exec" ? null : s.deploymentId || null,
                    agentId: s.kind === "host-exec" ? s.agentId || null : null, failure: s.failure, execution };
            });
            json.value = JSON.stringify(plan);
        } catch { event.preventDefault(); root.querySelector("[data-editor-error]").textContent = "Check the fixed environment JSON before saving."; }
    }, true);
    root.hidden = false;
    form.querySelector("[data-action-button]").hidden = false;
    }
    function initializeAll() { document.querySelectorAll("[data-action-editor]").forEach(initialize); }
    initializeAll();
    document.addEventListener("htmx:afterSwap", initializeAll);
})();
