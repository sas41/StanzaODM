(() => {
    "use strict";
    const initialized = new WeakSet();
    let serial = 0;
    const normalizeLines = value => value.replace(/\r\n?/g, "\n");
    function initialize(root) {
        if (initialized.has(root)) return;
        initialized.add(root);
        const form = root.closest("form"), json = root.querySelector("[name=PlanJson]"), initial = json.value;
        let plan;
        try { plan = JSON.parse(initial); } catch { root.querySelector("details").open = true; return; }
        const variables = root.querySelector("[data-workflow-variables]"), steps = root.querySelector("[data-workflow-steps]");
        function selectedScope() {
            const select = form.querySelector("[name=DeploymentId]");
            return select ? select.value || null : root.dataset.deployment || plan.deploymentId || null;
        }
        function field(parent, label, value, options = null, type = "text") {
            const wrapper = document.createElement("label"); wrapper.className = "field";
            const text = document.createElement("span"); text.textContent = label;
            const input = document.createElement(options ? "select" : type === "textarea" ? "textarea" : "input");
            input.id = "workflow-" + (++serial); wrapper.htmlFor = input.id;
            if (options) for (const option of options) input.add(new Option(option, option));
            else if (type !== "textarea") input.type = type;
            if (type === "checkbox") input.checked = value; else input.value = value ?? "";
            wrapper.append(text, input); parent.append(wrapper); return input;
        }
        function row(parent, title) {
            const section = document.createElement("section"); section.className = "panel";
            const heading = document.createElement("h4"); heading.textContent = title; section.append(heading);
            for (const [label, action] of [["Remove", () => section.remove()], ["Move up", () => {
                if (section.previousElementSibling) parent.insertBefore(section, section.previousElementSibling);
            }], ["Move down", () => {
                if (section.nextElementSibling) parent.insertBefore(section.nextElementSibling, section);
            }]]) {
                const button = document.createElement("button"); button.type = "button"; button.textContent = label;
                button.addEventListener("click", action); section.append(button);
            }
            parent.append(section); return section;
        }
        function catalog(parent, title, value, template) {
            const select = field(parent, title, "", []);
            select.append(template.content.cloneNode(true));
            if (value && !Array.from(select.options).some(o => o.value === value)) select.add(new Option("Unavailable: " + value, value));
            select.value = value ?? ""; return select;
        }
        const metadata = root.querySelector("[data-workflow-metadata]");
        const label = field(metadata, "Button label", plan.buttonLabel), order = field(metadata, "Button order", plan.order ?? 0, null, "number");
        const visible = field(metadata, "Show on overview", plan.showOnOverview !== false, null, "checkbox");
        const confirmation = field(metadata, "Confirmation message", plan.confirmation);
        function addVariable(p = {}) {
            const r = row(variables, "Variable");
            const name = field(r, "Variable name", p.name), label = field(r, "Label", p.label);
            const input = field(r, "Input", p.input ?? "text", ["text", "dropdown", "branch", "version"]);
            const type = field(r, "Value type", p.type ?? "string", ["string", "integer", "boolean"]);
            const required = field(r, "Required", p.required !== false, null, "checkbox"), fallback = field(r, "Default value", p.default);
            const options = field(r, "Dropdown choices (one per line)", (p.options ?? []).join("\n"), null, "textarea");
            const source = catalog(r, "Git source checkout destination", p.source, root.querySelector("[data-workflow-sources]"));
            source.dataset.workflowSource = "";
            const scope = selectedScope();
            for (const option of Array.from(source.options)) if (option.dataset.deployment && option.dataset.deployment !== scope) option.remove();
            if (p.source && !Array.from(source.options).some(o => o.value === p.source)) source.add(new Option("Unavailable: " + p.source, p.source));
            source.value = p.source ?? "";
            const repository = field(r, "Image repository (without tag)", p.repository);
            const services = field(r, "Image target services (comma-separated)", (p.services ?? []).join(", "));
            function visibility() {
                options.parentElement.hidden = input.value !== "dropdown";
                source.parentElement.hidden = input.value !== "branch";
                repository.parentElement.hidden = services.parentElement.hidden = input.value !== "version";
                type.parentElement.hidden = input.value !== "text";
                if (input.value !== "text") type.value = "string";
                required.parentElement.hidden = ["branch", "version"].includes(input.value);
                if (required.parentElement.hidden) required.checked = true;
            }
            input.addEventListener("change", visibility); visibility();
            r.readValue = () => ({ ...p, name: name.value, label: label.value || null, input: input.value, type: type.value,
                required: required.checked, default: fallback.value === "" && p.default === "" ? "" : fallback.value || null,
                options: input.value === "dropdown" ? options.value.split("\n").filter(Boolean) : null,
                source: input.value === "branch" ? source.value : null,
                repository: input.value === "version" ? repository.value : null,
                services: input.value === "version" ? services.value.split(",").map(s => s.trim()).filter(Boolean) : null });
        }
        function addStep(s = {}) {
            const r = row(steps, "Step"), e = s.execution ?? {};
            const kind = field(r, "Operation", s.kind ?? "container-script", ["container-script", "deploy", "build", "validate", "git-refresh", "pull", "start", "stop", "restart", "remove", "status", "logs", "docker-script", "container-exec", "host-exec"]);
            const names = { "container-script": "Bash in containers", deploy: "Build and deploy", build: "Build", validate: "Validate", "git-refresh": "Refresh Git sources", pull: "Pull images",
                start: "Start", stop: "Stop", restart: "Restart", remove: "Remove deployment", status: "Status", logs: "Logs", "docker-script": "Agent Docker script", "container-exec": "Fixed container command", "host-exec": "Privileged host command" };
            for (const option of kind.options) option.textContent = names[option.value];
            kind.dataset.workflowKind = "";
            const deployment = catalog(r, "Deployment (empty for Agent actions)", s.deploymentId ?? plan.deploymentId ?? root.dataset.deployment, root.querySelector("[data-workflow-deployments]"));
            const agentOptions = document.createElement("template");
            const topAgent = form.querySelector("[name=AgentId]");
            if (topAgent) agentOptions.content.append(...Array.from(topAgent.options, o => o.cloneNode(true)));
            const agent = catalog(r, "Agent (host actions only)", s.agentId ?? root.dataset.agent, agentOptions);
            const failure = field(r, "On failure", s.failure ?? "stop", ["stop", "continue"]);
            const script = field(r, "Bash script (variables are environment values, e.g. \"$APP_BRANCH\")", e.script, null, "textarea"); script.rows = 10;
            script.dataset.workflowScript = "";
            const services = field(r, "Container services (comma-separated)", (e.services ?? (e.service ? [e.service] : [])).join(", "));
            services.dataset.workflowServices = "";
            const replicas = field(r, "Run script", e.replicas ?? "once", ["once", "all"]);
            replicas.options[0].textContent = "Once per selected service"; replicas.options[1].textContent = "Every running replica";
            const executable = field(r, "Executable (absolute path)", e.executable);
            const args = field(r, "Arguments (one per line)", (e.arguments ?? []).join("\n"), null, "textarea");
            const service = field(r, "Compose service", e.service), container = field(r, "Or explicit container", e.container);
            const user = field(r, "User", e.user), directory = field(r, "Working directory", e.workingDirectory);
            const environment = field(r, "Fixed environment (JSON object)", e.environment ? JSON.stringify(e.environment) : "", null, "textarea");
            function visibility() {
                const isScript = ["container-script", "docker-script"].includes(kind.value), exec = ["container-exec", "host-exec"].includes(kind.value);
                script.parentElement.hidden = !isScript;
                services.parentElement.hidden = replicas.parentElement.hidden = kind.value !== "container-script";
                for (const f of [executable, args, user, directory]) f.parentElement.hidden = !exec;
                service.parentElement.hidden = container.parentElement.hidden = kind.value !== "container-exec";
                environment.parentElement.hidden = !isScript && !exec;
                agent.parentElement.hidden = kind.value !== "host-exec" && kind.value !== "docker-script";
                deployment.parentElement.hidden = Boolean(selectedScope());
            }
            kind.addEventListener("change", visibility); visibility();
            r.readValue = () => {
                const isScript = ["container-script", "docker-script"].includes(kind.value), exec = ["container-exec", "host-exec"].includes(kind.value);
                let execution = null;
                if (isScript) execution = { ...e, executable: "/bin/bash", arguments: [], mode: "script",
                    script: typeof e.script === "string" && normalizeLines(script.value) === normalizeLines(e.script) ? e.script : script.value,
                    service: null, container: null, user: null, workingDirectory: null,
                    services: kind.value === "container-script" ? services.value.split(",").map(s => s.trim()).filter(Boolean) : null,
                    replicas: kind.value === "container-script" ? replicas.value : null };
                if (exec) execution = { ...e, executable: executable.value,
                    arguments: normalizeLines(args.value) === normalizeLines((e.arguments ?? []).join("\n")) ? e.arguments ?? [] : args.value ? args.value.split("\n") : [], mode: "exec", script: null,
                    service: kind.value === "container-exec" ? service.value || null : null, container: kind.value === "container-exec" ? container.value || null : null,
                    user: user.value || null, workingDirectory: directory.value || null, services: null, replicas: null };
                if (execution) execution.environment = environment.value ? JSON.parse(environment.value) : null;
                const target = kind.value === "host-exec" ? null : deployment.value || null;
                return { ...s, kind: kind.value, deploymentId: target, agentId: !target ? agent.value || form.querySelector("[name=AgentId]")?.value || null : null,
                    failure: failure.value, execution };
            };
        }
        (plan.parameters ?? []).forEach(addVariable); (plan.steps ?? []).forEach(addStep);
        root.querySelector('[data-workflow-add="variable"]').addEventListener("click", () => addVariable());
        root.querySelector('[data-workflow-add="step"]').addEventListener("click", () => addStep());
        form.querySelector("[name=DeploymentId]")?.addEventListener("change", event => {
            for (const select of variables.querySelectorAll("[data-workflow-source]")) {
                const current = select.value;
                select.replaceChildren(root.querySelector("[data-workflow-sources]").content.cloneNode(true));
                for (const option of Array.from(select.options)) if (option.dataset.deployment && option.dataset.deployment !== event.target.value) option.remove();
                select.value = current;
            }
            for (const kind of steps.querySelectorAll("[data-workflow-kind]")) kind.dispatchEvent(new Event("change"));
        });
        function serialize(event) {
            if (json.value !== initial && !json.dataset.generated) return;
            try {
                plan = { ...plan, deploymentId: selectedScope(),
                    buttonLabel: label.value || null, order: Number(order.value), showOnOverview: visible.checked, confirmation: confirmation.value || null,
                    parameters: Array.from(variables.children, r => r.readValue()), steps: Array.from(steps.children, r => r.readValue()) };
                if (plan.deploymentId) plan.steps = plan.steps.map(s => ({ ...s, deploymentId: plan.deploymentId, agentId: null }));
                json.value = JSON.stringify(plan); json.dataset.generated = "true";
            } catch { event.preventDefault(); root.querySelector("[data-workflow-error]").textContent = "Check the fixed environment JSON before saving."; }
        }
        json.addEventListener("input", () => delete json.dataset.generated);
        form.addEventListener("submit", serialize, true);
        form.addEventListener("htmx:configRequest", event => {
            if (event.detail.elt !== form) return;
            serialize(event); event.detail.parameters.PlanJson = json.value;
        });
        // A swapped form can be submitted before HTMX's delayed settle phase. Attach its
        // request handlers before exposing controls so rapid successive saves stay partial.
        if (typeof window !== "undefined") window.htmx?.process(form);
        root.querySelector("[data-workflow-ui]").hidden = false;
    }
    function initializeAll() { document.querySelectorAll("[data-workflow-editor]").forEach(initialize); }
    initializeAll(); document.addEventListener("htmx:afterSwap", initializeAll);
})();
