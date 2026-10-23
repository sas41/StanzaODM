(() => {
    "use strict";
    const root = document.querySelector("[data-action-editor]");
    if (!root) return;
    const form = root.closest("form"), json = form.querySelector("[name=PlanJson]"), advanced = form.querySelector("[data-advanced-plan]");
    const inputs = root.querySelector("[data-editor-inputs]"), steps = root.querySelector("[data-editor-steps]");
    let plan;
    try { plan = JSON.parse(json.value); } catch { advanced.open = true; return; }
    const initialJson = json.value;
    const field = (parent, title, key, value = "", options = null, type = "text") => {
        const label = document.createElement("label"); label.textContent = title;
        const control = document.createElement(options ? "select" : type === "textarea" ? "textarea" : "input");
        control.dataset.field = key;
        if (options) for (const option of options) control.add(new Option(option, option));
        else if (type !== "textarea") control.type = type;
        if (type === "checkbox") control.checked = Boolean(value); else control.value = value ?? "";
        label.append(control); parent.append(label); return control;
    };
    const catalog = (parent, title, key, value, template) => {
        const select = field(parent, title, key, "", []);
        select.append(document.getElementById(template).content.cloneNode(true));
        if (value && !Array.from(select.options).some(o => o.value === value)) select.add(new Option("Unavailable: " + value, value));
        select.value = value ?? ""; return select;
    };
    const row = (parent, title) => {
        const section = document.createElement("section"); section.className = "panel action-editor-row";
        const heading = document.createElement("h3"); heading.textContent = title; section.append(heading);
        const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "Remove " + title.toLowerCase();
        remove.addEventListener("click", () => section.remove()); section.append(remove);
        for (const [label, up] of [["Move up", true], ["Move down", false]]) {
            const move = document.createElement("button"); move.type = "button"; move.textContent = label;
            move.addEventListener("click", () => {
                const sibling = up ? section.previousElementSibling : section.nextElementSibling;
                if (sibling) parent.insertBefore(up ? section : sibling, up ? sibling : section);
            });
            section.append(move);
        }
        parent.append(section); return section;
    };
    function addInput(p = {}) {
        const item = row(inputs, "Input");
        field(item, "Variable name", "name", p.name);
        field(item, "Label", "label", p.label);
        const input = field(item, "Input control", "input", p.input || "text", ["text", "dropdown", "version", "branch"]);
        field(item, "Value type", "type", p.type || "string", ["string", "integer", "boolean"]);
        field(item, "Required", "required", p.required !== false, null, "checkbox");
        field(item, "Default value (optional)", "default", p.default);
        const options = field(item, "Dropdown choices (one per line)", "options", (p.options || []).join("\n"), null, "textarea");
        const source = catalog(item, "Git source checkout destination", "source", p.source, "action-source-options");
        for (const option of Array.from(source.options))
            if (option.dataset.deployment && option.dataset.deployment !== root.querySelector("[data-plan-deployment]").value) option.remove();
        const repository = field(item, "Image repository (without tag)", "repository", p.repository);
        repository.placeholder = "ghcr.io/organization/application";
        const services = field(item, "Target Compose services (comma-separated)", "services", (p.services || []).join(", "));
        function visibility() {
            options.parentElement.hidden = input.value !== "dropdown";
            source.parentElement.hidden = input.value !== "branch";
            repository.parentElement.hidden = services.parentElement.hidden = input.value !== "version";
        }
        input.addEventListener("change", visibility); visibility();
    }
    function addStep(s = {}) {
        const item = row(steps, "Step");
        const kind = field(item, "Operation", "kind", s.kind || "deploy", ["validate", "git-refresh", "build", "pull", "deploy", "start", "stop", "restart", "remove", "status", "logs", "container-exec", "host-exec"]);
        const deployment = catalog(item, "Deployment", "deploymentId", s.deploymentId || root.querySelector("[data-plan-deployment]").value, "action-deployment-options");
        const agent = catalog(item, "Agent", "agentId", s.agentId, "action-agent-options");
        field(item, "Timeout (seconds)", "timeoutSeconds", s.timeoutSeconds || 300, null, "number");
        field(item, "On failure", "failure", s.failure || "stop", ["stop", "continue"]);
        const command = document.createElement("div"); item.append(command);
        const exec = s.execution || {};
        field(command, "Executable (absolute path)", "executable", exec.executable);
        const args = field(command, "Arguments (one per line; use ${variable} as a whole argument)", "arguments", (exec.arguments || []).join("\n"), null, "textarea");
        args.placeholder = "--database\ntesting";
        field(command, "Compose service (container-exec)", "service", exec.service);
        field(command, "Or explicit container (container-exec)", "container", exec.container);
        field(command, "User (optional)", "user", exec.user);
        field(command, "Working directory (optional)", "workingDirectory", exec.workingDirectory);
        field(command, "Fixed environment (JSON object, optional)", "environment", exec.environment ? JSON.stringify(exec.environment) : "", null, "textarea");
        function visibility() {
            agent.parentElement.hidden = kind.value !== "host-exec";
            deployment.parentElement.hidden = kind.value === "host-exec";
            command.hidden = !["container-exec", "host-exec"].includes(kind.value);
        }
        kind.addEventListener("change", visibility); visibility();
    }
    const metadata = {
        deploymentId: "[data-plan-deployment]", buttonLabel: "[data-plan-label]", order: "[data-plan-order]",
        confirmation: "[data-plan-confirmation]", showOnOverview: "[data-plan-visible]"
    };
    for (const [key, selector] of Object.entries(metadata)) {
        const control = root.querySelector(selector);
        if (control.type === "checkbox") control.checked = plan[key] !== false; else control.value = plan[key] ?? (key === "order" ? "0" : "");
    }
    (plan.parameters || []).forEach(addInput); (plan.steps || []).forEach(addStep);
    const displayDeployment = root.querySelector("[data-plan-deployment]");
    displayDeployment.addEventListener("change", () => {
        for (const select of steps.querySelectorAll("select[data-field=deploymentId]")) if (!select.value) select.value = displayDeployment.value;
        for (const select of inputs.querySelectorAll("select[data-field=source]")) {
            const value = select.value;
            select.replaceChildren(document.getElementById("action-source-options").content.cloneNode(true));
            for (const option of Array.from(select.options)) if (option.dataset.deployment && option.dataset.deployment !== displayDeployment.value) option.remove();
            select.value = value;
        }
    });
    displayDeployment.dispatchEvent(new Event("change"));
    root.querySelector("[data-add-input]").addEventListener("click", () => addInput());
    root.querySelector("[data-add-step]").addEventListener("click", () => addStep());
    const values = item => Object.fromEntries(Array.from(item.querySelectorAll("[data-field]")).map(c => [c.dataset.field, c.type === "checkbox" ? c.checked : c.value]));
    form.addEventListener("submit", event => {
        if (json.value !== initialJson) return;
        try {
            for (const [key, selector] of Object.entries(metadata)) {
                const c = root.querySelector(selector); plan[key] = c.type === "checkbox" ? c.checked : key === "order" ? Number(c.value) : c.value || null;
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
                    agentId: s.kind === "host-exec" ? s.agentId || null : null, timeoutSeconds: Number(s.timeoutSeconds), failure: s.failure, execution };
            });
            json.value = JSON.stringify(plan);
        } catch { event.preventDefault(); root.querySelector("[data-editor-error]").textContent = "Check the fixed environment JSON before saving."; }
    });
    root.hidden = false;
})();
