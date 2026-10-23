# Stanza Open Deployment Manager

## TODO

All work starts here. Move a task, including its acceptance criteria and test requirements, to `IN-PROGRESS` when implementation begins and to `DONE` only after implementation and verification are complete. Keep task identifiers stable. The order below reflects implementation dependencies; every listed capability is part of the initial release unless explicitly marked future-only.

All remaining original feature areas have implementation work recorded under IN-PROGRESS. Execution resumed at the user's request on 2026-10-23, starting with Agent configuration synchronization. A task is not complete merely because part of it is implemented.

## IN-PROGRESS

### P27 — Full-stack examples, deployment guide and friendly Agent names

- Add example.env covering every Compose variable with nonsecret defaults or explicit CHANGE_ME placeholders. Add a default compose.yaml containing Control Plane, PostgreSQL and one Agent, retaining separate component/privileged manifests and default-off insecure HTTP.
- Add STANZA_AGENT_NAME/Agent:Name as a validated friendly registration label. Use it consistently in enrollment and authenticated reconnect proofs so the Control Plane list/pickers retain the label rather than replacing it with a container hash. Keep cryptographic identity/resource IDs and Docker's actual hostname unchanged.
- Replace the minimal README with concise initial setup/enrollment steps, the user's exact nginx/environment smoke deployment, and step-by-step Compose-only, Git-build (repository Dockerfile or managed custom Dockerfile), mixed-source, managed-file mount and Agent move workflows. Describe actual current UI and limitations: Compose is maintained in Stanza; workload data is not automatically moved.
- Verify default full-stack service parity, all-variable template coverage, example interpolation/escaping, safe template values, friendly-name validation and persistence/reconnect identity stability, and integrated checks.
- Instruction conflict: the user requested retiring PLAN.md, but active AGENTS.md instructions require keeping it as source of truth. Retain/update PLAN.md under those active instructions; do not delete it or silently change repository policy to bypass the requirement.
- Implemented: example.env covers all Compose inputs (including optional project naming), with secure defaults and explicit URL/token/socket-GID placeholders. Default compose.yaml contains the Control Plane/PostgreSQL/Agent stack; component manifests remain available. PR CI validates the additional default manifest. Template files remain trackable while populated .env and local Agent state stay ignored.
- Implemented friendly names: STANZA_AGENT_NAME maps to Agent__Name; absent/blank values default to a project-derived label in Compose. The Agent validates length/control characters, trims the registration label, and signs it consistently during initial enrollment and channel reconnect. Control Plane lists/pickers use the persisted label; resource/identity IDs and Docker hostname remain unchanged. Name changes retain the existing Agent identity.
- README includes the exact requested nginx/three-variable smoke example and concise setup/approval/update instructions, both repository-provided and managed custom Dockerfiles, mixed pulled/Git/local builds, managed file/environment mounts, and the guided move/storage/recovery limitations. It does not claim Git-managed Compose or automatic persistent-data transfer.
- Verified source: 212 initial focused repository/options/worker/channel cases and all five deployment-guide cases pass; the final integrated Release run passes **1,658 cases, zero failures, 8 runtime cases skipped; 1,666 total**. All 33 client cases and actionlint pass. Actual Compose rendering confirms default/component service parity, privileged merging, template usability, friendly-name defaults and repository/custom Dockerfile resolution; the README smoke command generates expected HTML in an isolated shell fixture. These checks do not start nginx/Docker workloads.
- Remains IN-PROGRESS for new-image/fresh full-stack startup and actual UI-to-Agent smoke deployment verification, unavailable without Docker here. No image was published or deployed by this agent. PLAN retirement remains blocked by active repository instructions; no file was deleted to evade that requirement.

### P26 — Per-installation local Agent storage and host-path mapping

- Supersede P25's fixed host directory with ./agent_data relative to each Agent Compose file. Keep /var/lib/stanza-agent fixed inside the Agent and make initialization mount that same local directory. Do not move/delete existing state or generate replacement identities on the user's host.
- Remove the shared fixed Compose project name; separate installation directories use separate default project names, with -p/COMPOSE_PROJECT_NAME available for explicit names. Do not use a fixed container name or publish Agent management ports.
- Resolve the Agent's actual data mount source from its own container's Docker mount metadata, then translate managed workload bind/config/secret file sources into daemon-host paths through a private temporary Compose override. Preserve authored files, build contexts, environment inputs, named volumes and intentionally authored external host bind paths. Fail safely if required mount metadata is unavailable/ambiguous.
- Keep default non-root execution and explicit privileged override. Document that distinct Agent folders/identities do not isolate access to a shared Docker daemon or avoid workload port/name conflicts.
- Tests: ordinary/privileged relative mounts and internal-root consistency, independent project rendering, self-mount discovery/refusal, path-boundary handling, exact override content/escaping, preservation of original files and options, distinct installation mappings, and integrated solution checks. Real packaged Docker bind/config/secret verification remains required before release.
- Implemented: both Agent services now bind ./agent_data from their installation directory, with the internal runtime root fixed at /var/lib/stanza-agent. Removed the shared `name: stanza-agent`; independent installation folders get independent default Compose project names. Local Agent state is Git-ignored. Separate instances must not share a data folder or identity; access to a shared Docker daemon remains effectively host administration, not isolation.
- Implemented runtime mapping: the packaged Agent inspects its own default Docker container ID to find its writable data mount. Managed bind/config/secret paths use the discovered daemon-host root through a private temporary override only during apply/status, after client-side builds/pulls. Original Compose/build/environment files and external host binds/named volumes are preserved. Dollar escaping is decoded from Compose's canonical output and re-escaped once in generated input. Missing/ambiguous/nested/unavailable mount metadata fails before applying workloads with agent_data_mount_unavailable. Keep Docker's default container hostname for automatic self-discovery.
- Verified source: unit checks and actual daemon-free Compose rendering cover relative mounts, two independent installation/project names, mount discovery failures/boundaries, bind options, configs/secrets, literal dollar paths, unchanged authored files and private override cleanup. The first full run caught an incorrect two-service fixture in the new image-only test; it was corrected without changing production scope checks. The final Release run after the mapping-error diagnostic refinement passes **1,642 cases, zero failures, 8 runtime cases skipped; 1,650 total**. All 33 client cases and actionlint pass.
- Remains IN-PROGRESS for actual rebuilt-Agent Docker execution of the mapped bind/config/secret flow, including non-root access and installed Compose compatibility. No data was moved/deleted on the user's host, and no image was built, published or deployed by this agent. Updated Compose files plus an Agent image containing the mapping change are required; changing mounts alone on an old image does not establish workload-bind correctness.

### P25 — Opt-in HTTP Agent transport and fixed Agent storage

- Extend STANZA_ALLOW_INSECURE_HTTP (default false) to Agent enrollment and the authenticated outbound WebSocket command channel. The Control Plane and Agent must each explicitly opt in; HTTPS certificate validation remains mandatory when HTTPS is selected.
- Keep identity proofs, token expiry/binding, replay protection, administrative approval, revocation and execution authorization unchanged. Reject cleartext enrollment on the Control Plane before registering anything when the transport policy disallows it.
- Fix the ordinary and privileged Agent deployment storage to host /var/lib/stanza-agent, mounted at the same location inside the Agent. The initializer must prepare that exact host directory. Remove the data-root Compose variable, keep workload bind paths correct, and do not move/delete existing data or reuse identities from a different mount silently.
- Subsequent user change P26 supersedes the fixed host path: use per-installation ./agent_data with a fixed internal path and automatic workload host-path mapping. The matching initializer/runtime storage requirement and data-preservation restrictions remain in force.
- Explain the unencrypted credential/command risk in Compose and UI guidance; use a single transport policy for browser and Agent traffic without weakening Git/registry TLS or proxy trust.
- Tests: HTTP default rejection, opt-in on each side, HTTPS acceptance, unsafe URL refusal, actual production-mode HTTP enrollment/pending approval/config sync/command/revocation flow, fixed initializer/runtime mount equality, ignored obsolete mount-variable inputs, and full integrated checks. Record any unavailable image/deployment verification.
- Implemented: shared startup-only HttpTransportPolicy on the Control Plane; Agent:AllowInsecureHttp on the Agent. compose.control.yaml maps STANZA_ALLOW_INSECURE_HTTP to Stanza__AllowInsecureHttp and compose.agent.yaml maps it to Agent__AllowInsecureHttp, both default false. HTTP must be enabled separately on both deployments; HTTPS remains supported with normal certificate verification. Enrollment and command clients reject disallowed origins before transmitting credentials; the Control Plane rejects disallowed cleartext enrollment before creating an Agent record.
- The original P25 fixed-host implementation is superseded by P26: initialize-agent now mounts ./agent_data:/agent-data; agent mounts ./agent_data:/var/lib/stanza-agent and uses Agent__PersistentDataRoot=/var/lib/stanza-agent. The privileged override inherits this storage. STANZA_AGENT_DATA_ROOT remains removed. No existing data has been moved or deleted; switching storage layouts must not silently discard an identity or workload files.
- Focused verification: 104 Agent options/enrollment/channel/browser-policy/Compose cases pass, including actual Production-mode HTTP enrollment into pending state, refusal before approval, explicit approval, full configuration synchronization, authorized dispatch and revocation-driven disconnect. This uses real HTTP/WebSockets with a test execution adapter, not real Docker workloads. Negative cases cover opt-in missing on either side and credential-bearing/unsafe URLs; fixed storage renders identically even if an old caller supplies a different data-root variable.
- P25 checkpoint integrated verification: **1,613 passed, zero failed, 8 skipped; 1,621 total (Release)**. The later P26 integrated run also passes these cases. All 33 JavaScript cases and actionlint pass. The new revocation-flow test waits longer than the server's five-second recheck interval but less than test cancellation; no authorization checks or failures were suppressed.
- Remains IN-PROGRESS for rebuilt-image and packaged-runtime verification. No image has been built/published/deployed by this agent for these changes; do not expect an existing GHCR image to recognize the new Agent option. Both updated images and updated Compose mappings must be deployed. No subagents or deployments are running in the background.

### P24 — Explicit opt-in HTTP browser sessions

- Add STANZA_ALLOW_INSECURE_HTTP to compose.control.yaml, default false, mapped to Stanza:AllowInsecureHttp. Read this pre-launch security prerequisite at startup; changing it requires recreating/restarting the Control Plane.
- Keep production browser session cookies Secure by default, including TLS reverse-proxy backends. Only explicit opt-in permits cookies over HTTP; HTTPS requests retain Secure cookies. Preserve HttpOnly, SameSite, CSRF, expiration, revocation and authorization.
- Permit HTTP public origins for browser same-origin WebSockets only when enabled, including consistent import validation. Reject malformed origins regardless of the flag.
- Display a prominent warning for HTTP-mode browser requests and show the read-only deployment flag/state in settings. Do not silently weaken Agent transport, Git/registry TLS or proxy trust.
- Tests: secure defaults/off, HTTP opt-in login/cookie persistence, HTTPS cookies, CSRF/revocation, strict URL/import validation, Compose default/override rendering, and real Chromium login/WebSocket flow using a non-localhost HTTP origin. Run integrated solution checks; record unavailable packaged-runtime verification.
- Implemented: startup-only HTTP policy, default-off Compose mapping, SameAsRequest cookies only on explicit opt-in (HTTPS remains Secure), HTTP public-origin editing/import validation, prominent HTTP warning and read-only deployment-state guidance in Settings. P25 subsequently renames BrowserHttpPolicy to HttpTransportPolicy and explicitly extends the user-approved opt-in to Agent enrollment/commands. Git/registry TLS and proxy trust remain unchanged.
- Focused verification: 21 HTTP/cookie/Compose/real-browser cases pass, including production Chromium login and authenticated live WebSockets on a deliberately non-localhost, non-secure HTTP origin. CSRF, revocation, strict default cookies and rejected import downgrades remain covered.
- Remains IN-PROGRESS for final packaged verification. The earlier HTTP-browser-only run reported 1,592 passed, 1 failed and 8 runtime cases skipped; its already tracked automation-browser schedule-preview timeout has not been fixed or suppressed. P25 and the latest P26 combined runs pass, but that does not conclusively resolve the intermittent browser issue. All 33 client cases and actionlint pass. No updated image was published or deployed by this agent, and the user's running instance was not changed.

### P21 — Agent startup and configuration-change synchronization

- After approval, synchronize the Control Plane Agent ID, runtime policy, Global/Agent environment, and assigned deployment files/environment/source definitions on every startup and reconnect.
- Detect saved changes at the UI-configured status interval and transfer only changed configuration; disconnected Agents receive a complete snapshot when reconnecting.
- Keep a private deployment-specific configuration cache separate from immutable execution workspaces. Synchronization does not need to restart workloads; explicit operations continue to apply workload changes.
- Remove unassigned/deleted deployment caches without touching running containers, execution workspaces, bind mounts or volumes. Keep source credential secrets operation-scoped rather than retaining unnecessary credentials in the cache.
- Expose acknowledged synchronization state in the Agent UI, with bounded authenticated transport, safe paths, atomic file replacement and secret-free diagnostics.
- Tests: initial/reconnect synchronization, change detection and unchanged suppression, assignment isolation/removal, files/directories/binary/environment round trips, runtime-policy changes, malformed input/path/symlink rejection, interrupted writes, and actual authenticated channel integration.
- Implemented 2026-10-23: versioned authenticated configuration messages; complete per-connection bootstrap/reconnect snapshots; change detection at the existing UI-configured StatusIntervalSeconds; per-deployment delta transfers; disk acknowledgements and Agent UI timestamps; private configuration-cache with staged deployment replacement and safe assignment cleanup. Cache updates never invoke Docker or alter operation workspaces. Source definitions/references synchronize, while credential secrets remain operation-scoped.
- Focused and full in-process verification passes, including actual outbound WebSocket startup/change/reconnect replication. Remains IN-PROGRESS pending rebuilt-container startup/reconnect verification. This host currently has no Docker daemon socket, so packaged deployment was not exercised.

#### Resumed implementation — 2026-10-23

- Work resumed at the user's request, prioritizing configuration sync and getting the packaged application running. No subagents are active.
- Authoritative latest complete solution run after full-stack examples/friendly Agent names: **1,658 passed, zero failed, 8 skipped; 1,666 total (Release)**. All four JavaScript suites pass (**33 cases**), actionlint passes, and default full-stack/Control Plane/ordinary Agent/privileged Agent Compose configurations render successfully without a daemon. Actual Compose checks cover component parity, template/name propagation, both Dockerfile workflows and mapped binds/configs/secrets. These verify current source, not the images deployed on the user's host.
- Earlier full runs intermittently timed out in the automation-browser schedule-preview case, including the P24 run with 1,592 passed, 1 failed and 8 skipped. Passing focused/full reruns do not conclusively resolve that timing issue; it remains tracked and is not suppressed. The 2026-10-22 pause snapshot and older per-module counts below are historical, not the latest integration status.
- The old image-pinning regression assertion now checks the temporary private override during execution and its cleanup afterward, rather than expecting a retained file inside the build context. Production private pin-file placement was retained.
- P10/P18: added authorized desired/observed runtime state, observation age/offline indicators, and saved/deployed revision information to deployment list/detail pages. Removed stale statements that dispatch/preview/export are unimplemented. Service/container detail and continuous runtime observation remain unfinished.
- P06/P16: Agent status now includes the Compose service label consumed by move health verification; the previous response shape prevented a real destination status from passing that verifier. Real multi-Agent move verification remains blocked on Docker availability.
- Current deployment contract: compose.yaml is the default complete stack; compose.control.yaml/compose.agent.yaml are separate-host alternatives; compose.agent.privileged.yaml is an explicit override. Copy example.env to .env and configure the bootstrap placeholders. Applications pull component images from ghcr.io/sas41/stanzaodm, default latest, with optional shared UTC date pins. STANZA_AGENT_NAME gives a friendly signed Agent label without changing Docker hostname or identity. Tests run only on main-targeted PRs; main pushes/merges build/publish without tests. HTTP requires STANZA_ALLOW_INSECURE_HTTP=true on each deployment. Each Agent uses local ./agent_data, fixed at /var/lib/stanza-agent only inside the Agent. Use a separate installation directory/project per instance; no existing data was moved or deleted.
- Packaging blocker: `/var/run/docker.sock` is absent and `docker info` cannot connect. No daemon was started or host privileges changed. Rebuilt images, fresh-stack startup, both Agent profiles, privileged host execution and remaining packaged release flows still need a Docker-enabled environment. P02/P04–P19 are not marked DONE.

#### Pause snapshot — 2026-10-22

- Historical pause snapshot; execution resumed on 2026-10-23. All previously delegated agents released ownership. The following results describe the paused baseline, not current verification.
- At the pause, completed milestones were P01, P03 and P20; the other 17 original tasks remained IN-PROGRESS without final integrated verification. Independent module reports were not treated as completion evidence. Later milestones/results are recorded above and under DONE.
- The last full-solution run before the pause reported **1,477 passed, 9 failed, 8 skipped; 1,494 total**. Fixes made before pausing had not then received a final combined run. This is historical evidence only; current source results are in the resumed-implementation snapshot, and release readiness still requires real-runtime verification.
- Those historical failures covered cross-project lifecycle/log scope checks, pinned-image override placement, automatic image-update branch/SHA metadata and a same-Agent concurrency expectation. Subsequent work fixes the source regressions and tests cross-Agent parallelism with intentional per-Agent serialization; current non-opt-in checks pass. This does not replace final Docker verification.
- Eight opt-in integration cases were skipped in that full run: real Docker lifecycle, PTY, continuous logs, authenticated registry/image changes, and PostgreSQL recovery cases. Some were successfully exercised in focused subagent runs, but that does not replace a final integrated run with all required test flags and database prerequisites enabled.
- Most remaining feature implementations now exist: authenticated Agent work transport, durable operations, Git/registry management, advanced deployment editing, operational policy and delegated authorization, actions/schedules/webhooks/image policies, real PTYs/log streams, clone/move/import/export, audit history, and encrypted instance recovery. Integration and release verification remain unfinished.
- Planned next work at the pause was to resolve regressions, exercise Docker/PostgreSQL/browser/runtime flows and rebuild both image/profile deployments. Execution has since resumed; the current outstanding work is recorded with each task and in the resumed snapshot.

### P09 — Registries and Git sources

- Manage public/private registry endpoints and credentials through the UI, with scoped credential use and reveal permissions and connection tests.
- Allow multiple Git sources per deployment, each with a URL, credential reference, branch/tag/commit selection, and isolated workspace destination usable by Compose build contexts.
- Provide a pre-filled searchable branch dropdown populated from the remote, with loading/error states, pagination or bounded retrieval for large repositories, refresh, and correct handling of deleted branches.
- Support cloning/fetching the selected source, building its image through Compose, and passing the resolved environment configuration. Record the exact commit used for every build/deployment.
- Keep Compose manually managed in Stanza for this release. Do not implement Git-backed Compose editing, browser modifications of checkouts, commits/pushes, or local Git overrides.
- Protect against malicious repository paths and credential leakage in clone URLs, process output, and error messages. Define submodule handling explicitly and validate any supported submodule destinations and credentials.
- Resolve sources into an operation-specific snapshot so branch movement cannot change an operation midway through execution.
- Tests: public/private repositories and registries, credential authorization, branch listing/search/refresh, tags/commits, checkout isolation, multiple repositories, deleted refs, failed fetches, and reproducible builds.
- Implemented: encrypted Git/registry credential CRUD, separate read/use/reveal grants, source bindings and collision checks, searchable/paginated HTMX branch selection, Agent-side tag discovery, pinned Git checkouts, private credential cleanup, and redirect refusal. Source credentials and definitions participate in execution snapshots and queued-request authorization.
- Historical focused verification reported 68 source tests and 7 client tests passing. The latest non-opt-in integrated suite passes. Real authenticated registry/image checks and authenticated Git redirect-refusal tests also exist; the required complete source-to-Agent-to-Docker-build release flow and opt-in runtime run remain pending.

### P11 — Configurable actions and execution targets

- Implement reusable actions as ordered, configurable steps with parameters, target selection, timeout, concurrency policy, cancellation behavior, and per-step results.
- Support built-in steps for Git refresh, build, pull, deployment lifecycle operations, and custom command/script execution.
- Allow each action to target the host or another container/deployment, including resources other than the deployment that triggered it. Resolve the correct Agent and authorize every target.
- For host execution, implement an explicit opt-in host-execution mechanism with deployment prerequisites shown in the UI. Running a command inside the Agent container is not host execution; do not mislabel it or silently enable broad privileges.
- For container execution, support explicit service/container selection, executable/arguments, working directory, user, and environment inputs where applicable. Define behavior for stopped or missing targets before execution.
- Do not interpolate untrusted parameters directly into shell commands. Separate structured argument passing from intentionally authored shell scripts and apply permissions appropriate to each.
- Persist automation ownership/execution identity and evaluate current access when an action runs. Disabling an owner or revoking its grants must not leave an ungoverned privileged action.
- Audit manual and automated triggers and all target resolutions. Restrict editing/executing host actions as a distinct privileged capability.
- Tests: all target types, cross-deployment authorization, host-vs-Agent isolation, argument safety, revoked owners, step failures, concurrency, timeouts, and cancellation.
- Implemented: encrypted ordered action plans, typed arguments/parameters, deployment/container/host targets, persisted run/step results, concurrency, cancellation, ownership checks, dispatch-time automation guards, and metadata-only auditing. Host execution uses real nsenter with explicit local opt-in and host namespace prerequisites, not execution inside the Agent container.
- Remaining verification: a positive privileged host-action flow through the rebuilt Agent deployment, profile/ownership transitions, interrupted side effects, and full action-to-operation-to-Agent integration. Refusal and argument-safety tests are not a substitute for positive host execution verification.

### P12 — Schedules and scheduled actions

- Support scheduled deployment operations and reusable actions, configured entirely through the UI.
- Persist schedule expression, timezone, enabled state, next runs, execution identity, target, overlap policy, and missed-run policy.
- Show human-readable schedules and upcoming occurrences. Handle invalid expressions and daylight-saving transitions explicitly.
- Use durable scheduling and execution claims to prevent duplicate dispatch from concurrent workers or restarts. Reuse the operation pipeline rather than executing work inside request handlers.
- Provide execution history, manual run, pause/resume, and clear results for skipped or denied executions.
- Tests: recurrence calculation, timezones/DST, restart recovery, duplicate claims, overlap rules, missed runs, revoked permissions, and disabled schedules.
- Implemented: cron/timezone configuration and previews, DST rules, durable occurrence claims, overlap/missed-run policies, manual runs, pause/resume, history, and background scheduling. Background scheduling/execution is paused during instance recovery.
- Focused automation tests cover recurrence, claims, and revocation; full application restart and runtime scheduling verification remains pending.

### P13 — Webhooks

- Provide scoped webhook endpoints for triggering deployment operations and actions, including Git-provider event handling where supported.
- Configure accepted events, branch/ref filters, target action, secret/signature verification, parameter mapping, and enabled state in the UI.
- Require authenticated delivery, enforce request size/rate limits, deduplicate provider delivery IDs, and prevent payload-driven command injection or target escalation.
- Rotate/revoke webhook secrets, show delivery history without leaking sensitive payloads, and correlate accepted deliveries with resulting operations.
- Tests: valid/invalid signatures, replay/duplicates, filtering, malformed/oversized requests, rate limits, mapping validation, target authorization, and secret rotation.
- Implemented: HMAC-SHA256 authenticated deliveries, bounded request bodies, persistent deduplication/rate limits, event/ref filters, whole-argument parameter mapping, encrypted secrets, rotation/revocation, and redacted delivery history.
- Focused HTTP/browser and automation tests were reported passing. A full signed-webhook-to-runtime-operation release flow remains pending.

### P14 — Automatic image updates

- Configure image checks and update policies per deployment/service, with frequency, notify-only versus automatic redeploy, and relevant concurrency/failure settings.
- Compare resolved image digests rather than tag strings, including authentication and architecture-aware resolution.
- Reuse normal deployment operations and health verification. Do not treat locally built services as registry-image update candidates unless explicitly configured with a registry source.
- Show available updates, last check, applied image identities, failures, and operation history. Coordinate with Git updates and other scheduled operations rather than racing them.
- Tests: unchanged/changed digests, private registries, unavailable registries, multi-architecture images, excluded services, policy enforcement, and failed redeployments.
- Implemented: policy UI, real Agent image probing, installed/remote config-digest comparison, platform selection, excluded builds, notify-only/auto-deploy modes, failure backoff, pinned deployment images, and resolved identity records. Applied commit/image metadata is preserved separately from pruned command output.
- Added enqueue/dispatch checks for pending manual configuration, placement, environment, sources, credentials, and applied-baseline changes; automatic Git inputs use recorded applied commits. Authored branch definitions are retained in encrypted operation metadata. A follow-up fix makes the next probe read those definitions rather than mistake a pinned SHA for a changed branch.
- Historical focused runtime/image tests included a real authenticated local registry. Current branch/SHA and applied-metadata retention regression cases pass in the combined non-opt-in suite. The final real-registry policy-to-redeploy flow against rebuilt images remains unverified.

### P16 — Clone and move workflows

- Clone a deployment into a new identity with selected managed files, source/credential references, environment configuration, and automation definitions.
- Preview destination-effective variables and conflicts involving published ports, explicit container names, networks, bind mounts, and existing volumes.
- Default cloned schedules, webhooks, and automatic updates to disabled. Require explicit confirmation before starting the clone.
- Move configuration and managed files through a guided destination validation, preparation, source-stop, destination-start, health-check, and placement-update workflow.
- Warn about host bind mounts and persistent volumes but allow authorized users to proceed. Never claim application data has been transferred or silently delete source data.
- Display a disabled `Transfer persistent data — not yet available` toggle. Volume/data transfer is future-only; persistent storage for workloads remains supported.
- Preserve recoverable placement/operation state after partial failures, and make source cleanup an explicit choice.
- Tests: clone identity isolation, automation defaults, destination permissions, variable changes, port/storage warnings, architecture mismatch, offline source/destination, partial move failures, and no unintended volume deletion.
- Implemented: atomic workspace clones, new identities, preserved text/binary/directory contents, destination previews, a durable move worker, destination preflight, source stop, destination deploy/status checks, placement commits under authorization, explicit source cleanup, and recovery inspection without command replay.
- Storage/conflict/architecture previews are deliberately conservative warnings, not a guarantee that a destination is compatible. Persistent data transfer remains unavailable; no workload volume transfer or automatic data deletion is implemented or claimed.
- Remaining: real multi-Agent move verification against the final operation worker and Docker runtime, including failure/recovery and source cleanup after placement changes.

### P17 — Plaintext configuration export and import

- Define a versioned portable bundle with plaintext configuration files and logical resource references, not database-specific identifiers.
- Support selected deployments/files/sources, all environment scopes, registry/Git configuration, action/schedule/webhook/update definitions, Agent configuration templates, groups/grants, and relevant instance settings.
- Include sensitive configuration values in the selected scope without export encryption. Before generating the export, show its contents/scope, prominently warn that credentials/tokens/environment values are readable, and require acknowledgement.
- Require export access to every included resource and sensitive value; do not make broad exports a permission bypass. Do not put bundle contents in audit logs or publicly accessible storage.
- Treat live session tokens, Agent private identities, and password hashes as security/runtime state rather than ordinary portable configuration. Do not reuse Agent identities through template imports.
- Validate import schema, paths, limits, references, credentials, permission grants, and target capabilities. Preview conflicts and map Agent/resource references before applying.
- Preserve privileged-account restrictions during imports. Keep imported workloads and automation inactive until explicitly enabled; report partial application clearly or apply configuration changes atomically where feasible.
- Distinguish configuration export from instance recovery and runtime volume data. Do not add legacy migrations or automatic volume transfer.
- Tests: plaintext round trips including secrets, warning acknowledgement, resource authorization, bundle traversal/limits, invalid references, conflict handling, privilege escalation, inactive automation defaults, and remapping to another instance.
- Implemented: acknowledged plaintext versioned bundles, encrypted expiring server-side previews, fingerprint/replay checks, staged import reports, logical resource mappings, atomic deployment workspace creation, and concrete participants for sources/credentials, all environment scopes, Agent templates, settings/policy, automation, and groups/grants.
- Credential export requires reveal access. Security-policy sections require System Admin. Existing accounts must be mapped explicitly; imports do not create/promote privileged users or reuse Agent private identities. Imported automation remains disabled.
- Historical focused portability verification reported 78 passing tests, including actual cross-instance SQLite round trips across configuration sections. Current non-opt-in integrated regressions pass, including HTTP public-origin import policy checks. Full live application/browser import/export release verification remains pending. Whole multi-section imports remain explicitly staged rather than falsely claimed to be universally atomic.

### P19 — Recovery, security, and initial-release verification

- Provide a coherent instance backup/restore procedure for PostgreSQL, managed files, and required encryption keys, separate from plaintext portable configuration exports. Do not claim it backs up workload volumes.
- Verify recovery after Control Plane/Agent restarts, storage exhaustion, unavailable registries/Git, interrupted builds, network failures, and expired/revoked credentials.
- Verify retention limits, operation/log buffering, connection cleanup, archive/file limits, execution privilege boundaries, and protection of Docker access.
- Run unit tests for every changed rule and component, integration tests against PostgreSQL and real Docker/Compose/Git, and browser tests against the containerized application. Use controlled clocks for scheduler tests.
- Release smoke test: fresh startup; first System Admin; ordinary user/group grants; Agent approval; mixed-source Compose with private images and Git builds; branch selection; three-level variables; host/container actions; scheduled operations; signed webhook; image update; terminal; clone; move with storage warnings; plaintext export/import.
- Confirm that fresh deployment works without manual database changes, hidden required settings, default credentials, unimplemented enabled controls, or reliance on host tools not supplied by the packaging.
- Implemented recovery: encrypted complete-instance archives using AES-256-GCM/PBKDF2, raw mapped database rows including password hashes, persisted encryption keys, strict schema/value/key validation, fresh-instance-only transactional restore, and additive key rollback protection. Workload data and Agent private keys are excluded.
- Coordinator integration adds recovery audit records, execution maintenance gating, browser-session stamp invalidation, restart-required HTTP blocking, and a one-time CSRF-protected restart action. Their current non-opt-in regressions pass; rebuilt-container restart/maintenance/recovery integration still requires final verification.
- Historical focused recovery verification reported 58 tests including a real PostgreSQL 18 round trip. The current combined run covers final session-invalidation/maintenance changes but skips the opt-in PostgreSQL round trip; the final PostgreSQL/container recovery run remains required.
- Release remains blocked on the unverified runtime items, opt-in Docker/PostgreSQL cases, rebuilt-image/browser deployment smoke tests, host-action prerequisites, lifecycle/recovery/failure scenarios, and final security review. P22 updates CI to discover all status/editor/branch/log client suites. Real Docker/PostgreSQL release gates remain separate unfinished work.

The earlier implementation pass was paused; work resumed on 2026-10-23 as recorded above. All previous subagents released ownership. Module reports alone are not proof that the final combined application is complete.

### P02 — Container packaging and first-run setup

- Implemented restart fix: Agent storage initialization can repeat when a prior initialization created only empty known storage folders but the Agent did not yet create identity.pk8. It still refuses unknown data, nonempty pre-identity stores, symlinks, invalid storage types and unexpected ownership before changing ownership/permissions. Twenty-nine initializer regression cases execute the actual inline shell script against isolated filesystems (chown/stat are substituted; no real ownership changes). Existing identity/workload bytes are preserved. The initializer milestone passed 117 repository/build cases; current expanded checks are included in the latest full run above. Real Alpine/container startup remains blocked on Docker availability. The requested latest image default and optional matching UTC date pins both render successfully.
- Supply container builds and a Compose-based Control Plane deployment with PostgreSQL, persistent application storage, health checks, and documented-in-configuration startup requirements.
- Supply a containerized Agent deployment with persistent identity and workspace storage and access to the selected Docker endpoint.
- Allow database connection configuration before startup. Limit other bootstrap inputs to what is necessary to reach the UI or enroll an Agent; manage ongoing application settings in the UI.
- Implement a protected first-run flow that creates the initial System Admin without shipped default credentials. Prevent simultaneous or repeated bootstrap claims.
- Provide a setup checklist for the public Control Plane URL, TLS requirements, first Agent enrollment, and first deployment. Do not require a registry or approved Agent for the application to start.
- Expose application settings in the UI with validation, explanations, access checks, and an indication when applying a change requires a restart. Identify host/container prerequisites that cannot be changed by an application setting.
- Persist required encryption keys across restarts and include them in the instance recovery design. Never rely on an ephemeral container filesystem for durable state.
- Tests: fresh-stack startup, first-run races, restart persistence, missing/invalid database settings, health readiness, and bootstrap lockout after initialization.
- Implemented: Control Plane/Agent Dockerfiles, Compose manifests, generated database credentials, atomic first-run System Admin creation, persisted key storage, settings UI, and real-browser setup smoke test. P20 additionally verifies both image builds and initial Compose startup with real PostgreSQL, health endpoints, setup page, and expected tables.
- Added in this pass: UI-backed session/login/execution/upload/retention/connection policies; per-Agent runtime settings; explicitly trusted reverse-proxy IP/CIDR configuration; encrypted instance recovery; and a restart-required recovery flow.
- Agent Compose now uses local ./agent_data beside each installation's Compose file, mounted at fixed /var/lib/stanza-agent inside the Agent and prepared by the initializer from that same local folder. P26 adds daemon-host path mapping for managed workload files and independent per-folder Compose projects; the privileged profile inherits this storage. No configurable data-root variable is required. An explicit host-action override and util-linux packaging were added. Updated images/manifests and host-profile/ownership behaviour still need rebuilt-image smoke verification. Bootstrap URL/token/socket-group, explicit insecure-HTTP opt-in when needed, and host kernel privileges remain documented pre-launch prerequisites.

### P04 — Account types, groups, and scoped permissions

- Implement System Admin, Admin, and User account types. System Admins have full access. Admins have full operational access to deployments, Agents, settings, and ordinary users but cannot manage Admin or System Admin accounts or grant/revoke administrative status.
- Enforce privileged-account protection for creation, promotion, demotion, deletion, disabling, password reset, session management, and indirect management paths. A protected account may still perform normal self-service operations such as changing its own password.
- Prevent loss of the final usable System Admin account.
- Give ordinary Users default-deny, additive action grants through direct permissions and groups. Scope grants to the relevant instance, Agent, deployment, action, or credential resource.
- Cover read access, file editing, deployment operations, environment access, credential use/reveal, terminals, host actions, automation management, import/export, and user/group management as distinct capabilities where appropriate.
- Prevent ordinary users from escalating access through group editing, delegated grants, action targets, credential references, imports, or account updates.
- Provide user/group management and an effective-permissions view explaining each grant.
- Enforce authorization in application operations and WebSocket subscriptions, not only navigation and buttons. Recheck access for queued automation and active sensitive sessions when permissions change.
- Tests: role/action/resource matrices, combined grants, cross-resource access, privileged-account protection, indirect escalation, final-System-Admin protection, and revoked live access.
- Implemented: account/group/membership/direct-grant management, exact permission catalog, effective-access explanations, privileged-account protection, final-System-Admin guard, serialized security mutations, and HTTP escalation/CSRF tests.
- Added in this pass: delegated Agent/settings/environment access, operation/credential/automation/portability/terminal/log authorization, queued and ongoing permission rechecks, source-credential change invalidation, and protected policy administration. Account type still restricts privileged-account management independently of operational grants.
- Security regression tests found stale tracked settings/Agent-policy version checks, a maintenance lease disposal issue, and blank proxy submissions; production fixes were added. Their expanded configuration cases pass in the current integrated suite. Final packaged authorization/security verification and review remain pending, including the newly opted-in cleartext transport boundary.

### P05 — Agent enrollment, identity, and connection lifecycle

- Let an administrator generate short-lived enrollment tokens and copy the minimal Agent startup configuration from the UI.
- Have the Agent generate a persistent identity and request registration through an outbound TLS connection by default. P25 adds the user-approved trusted-LAN HTTP exception only when both deployments explicitly enable STANZA_ALLOW_INSECURE_HTTP. An authorized administrator must approve the pending request before execution is allowed regardless of transport.
- Show pending identity details, runtime/host capabilities, approval/rejection, revocation, re-enrollment, connection state, and last-seen time in the UI.
- Exchange versioned structured messages over authenticated outbound connections. Do not expose an unauthenticated Agent command interface or require an inbound Agent management port.
- Support reconnect backoff, heartbeats, bounded buffering, message correlation, credential rotation, and explicit incompatible-version errors.
- Keep existing workloads running when the Agent or Control Plane disconnects. Reconcile observed state on reconnection without blindly repeating commands.
- Tests: approval enforcement, expired/reused tokens, identity persistence, impersonation/replay defenses, revocation, reconnect, incompatible contracts, and offline workloads.
- Implemented: persistent P-256 Agent identity, signed outbound enrollment/status requests, first-claim token binding, persistent replay protection, approval/rejection/revocation UI, bounded retries, encrypted Agent environment, and real in-process Agent-client/server tests.
- Added in this pass: authenticated outbound Agent WebSockets, correlated bounded work frames, single-session enforcement, heartbeat and token/approval revocation checks, capability publication, explicit credential rotation/reapproval, and a durable Agent request ledger preventing blind re-execution.
- Signed-channel, PTY/log forwarding, and real Docker cases were exercised in focused suites. Final rebuilt-container enrollment/reconnect/rotation and combined runtime verification remain pending.

### P06 — Docker adapter and capability model

- Implement Docker as the only initial runtime. Isolate Compose execution, image operations, container inspection, logs, exec sessions, networks, and storage inspection behind runtime interfaces.
- Detect Docker/Compose versions, host architecture, and supported operations. Present actionable capability errors before execution.
- Ensure local builds and Compose support are actually available in the packaged Agent; do not assume host CLI tools exist inside the Agent container.
- Keep adapter contracts flexible enough for future Podman support. Do not ship an apparently usable Podman selector or claim compatibility before implementing and testing it.
- Clearly disclose that Docker socket access grants substantial host control. Avoid granting the Agent unrelated host privileges by default.
- Tests: Docker adapter unit tests and real-runtime integration tests for validation, build, pull, lifecycle, inspect, logs, exec, and unsupported capabilities.
- Implemented: Docker CLI runtime abstraction, capability detection, structured Compose operations, clean inherited environment, workspace validation, bounded process output/cancellation, and safe-runner tests. Agent packaging supplies Docker CLI, Compose, Buildx, Git, and SSH client tools.
- Added in this pass: authenticated snapshot execution, mixed build/pull/deploy, health waiting, scoped existing-container controls, noninteractive exec, PTYs, continuous logs, Git/registry support, image identity inspection, and explicit host actions. Packaged execution is now enabled behind approval/authenticated dispatch and can be disabled through Agent runtime policy.
- Recent runtime fixes move internal interpolation secrets outside Docker build contexts, support managed empty directories/non-root bind reads, recheck discovered container labels, read all deployment containers' recent logs, and relocate generated image pinning files into temporary private storage.
- Current non-opt-in regression cases for these fixes pass together. Real lifecycle, scope isolation, network/storage cleanup expectations, and actual privileged host execution against rebuilt images still require the final Docker-enabled verification pass. Existing-container removal preserves volumes; current implementation is container-focused and must not be represented as complete general network/storage management.

### P07 — Deployment definition, text editing, and managed files

- Define each deployment as exactly one manually maintained Compose file, its environment files, supporting files/build contexts, source references, target Agent, and related automation configuration.
- Support mixed services within that file: public images, private images, locally managed builds, and builds from Git clones. Use Compose service names in technical views.
- Provide direct text editing for Compose, `.env`, Dockerfiles, and supporting text files, with syntax highlighting, save validation, revisions, diffs, and deployed-versus-saved revision indicators.
- Never silently rewrite, regenerate, or reformat user files. Validation must preserve the original text and provide useful line/context information where available.
- Support file/directory creation, upload, rename, download, and deletion, including binary build assets. Keep managed deployment files separate from arbitrary host files.
- Validate relative paths, symlinks, archive extraction, upload limits, and source destination collisions. Do not expose an unrestricted host file browser.
- Store immutable revision snapshots so in-flight operations do not change when a user edits files.
- Tests: mixed-source definitions, exact text round trips, revision isolation, invalid Compose, file operations, traversal/symlink/archive attacks, and size limits.
- Implemented: encrypted PostgreSQL-compatible draft/revision persistence, manual Compose/.env editors, supporting text/binary files, exact downloads, rename/delete/upload, history, optimistic concurrency, path/collision validation, and service/HTTP tests.
- Added in this pass: approved-Agent picker/backend validation, non-mutating syntax highlighting, bounded revision diffs, directory CRUD, binary replacement, atomic safe ZIP import, source destination reservations, policy-backed limits, runtime validation through the operation queue, and saved/applied revision indicators.
- Historical focused editor verification reported 203 tests plus 5 client tests, including Chromium. Current non-opt-in materialization/applied-revision regressions and editor/client cases pass in the combined run; full rebuilt-Agent materialization/application remains unverified. The configuration editor is no longer the only implemented deployment functionality.

### P08 — Global, per-Agent, and per-deployment environment configuration

- Provide text-based environment editors at all three scopes. Resolve matching keys top-down: Global, then Agent overrides, then Deployment overrides.
- Preserve original scope files and generate separate runtime material. Changing scope or target Agent must not rewrite the deployment's `.env` text.
- Use Compose-compatible parsing and interpolation semantics, including quoting, comments, empty values, escaping, and supported multiline values. Keep Compose interpolation distinct from container `environment` and service `env_file` behavior.
- Prevent unintended inheritance from the Agent process environment. Validate additional environment file paths and explicitly define how generated interpolation defaults are supplied to Compose.
- Show effective values, source scope, overridden keys, and affected deployments to authorized users. Restrict revealing sensitive inherited values separately from using them in an authorized deployment.
- Protect environment contents at rest, redact sensitive values from routine output, and do not automatically restart deployments after a global or Agent edit. Offer explicit operations to apply changes.
- Snapshot effective environment inputs for each operation. Recompute destination-specific inheritance during clone/move previews.
- Tests: precedence at every scope, parsing/interpolation parity, environment isolation, secret access/redaction, scope changes, Agent reassignment, and immutable execution inputs.
- Implemented: raw encrypted editors at Global, Agent, and Deployment scopes; immutable ordered environment inputs; literal-only provenance preview foundation; process-environment isolation; and daemon-free Compose config checks for precedence/interpolation.
- Added in this pass: persisted scopes captured into immutable operation requests, explicit ordered Agent env files, Compose-authoritative environment-preview operation, reveal checks at all three scopes, and guided-move inheritance previews. Internal interpolation files are outside the build context; the authored deployment .env remains available for service env_file use.
- Final environment-preview runtime checks, comprehensive provenance/affected-deployment UI, and all masking/permission combinations remain to be verified or completed. Compose interpolation and container environment injection remain distinct; no approximate parser replaces Compose.

### P10 — Durable operation pipeline and deployment lifecycle

- Persist operations with requester/trigger identity, target, configuration revision, effective inputs, timestamps, progress, step logs, and final results.
- Model queued, validating, preparing, building/pulling, applying, health-checking, succeeded, failed, and cancelled states with explicit allowed transitions.
- Support create, validate, build, pull, deploy/redeploy, start, stop, restart, and remove operations through the same authorized execution pipeline.
- Serialize conflicting operations on a deployment and coordinate shared target resources where needed. Apply timeouts, bounded logs, safe cancellation points, and retry policies appropriate to each step.
- Reconcile uncertain outcomes after restart/disconnection before retrying. Do not promise atomic rollback for arbitrary workloads or automatically remove persistent volumes.
- Report desired state separately from observed state and offline/unknown state. Show runtime drift or externally removed containers without silently destroying unmanaged resources.
- Tests: transition rules, concurrent requests, retries, cancellation, partial failures, health failures, Agent/Control Plane restarts, uncertain results, and external runtime changes.
- Added in this pass: encrypted durable requests/results, immutable revision/environment/source capture, queue claims and leases, per-deployment/per-Agent serialization, cross-Agent scheduling, cancellation, uncertain-outcome handling, fresh-status reconciliation, applied runtime metadata, source/permission rechecks, and audit records.
- Added live operation pages and progress frames for validation/preparation/build/pull/apply/check stages. Existing-container controls do not depend on changed draft files or revoked registry credentials. A positively unaccepted agent_busy result is deferred rather than treated as completed execution; uncertain sends are never blindly retried.
- Remaining: verify real queue-to-runtime recovery/cancellation/progress against rebuilt images, complete service/container observation and review freshness semantics. Combined non-opt-in regressions now pass and the concurrency case correctly uses distinct Agents; those old source-suite blockers are no longer pending. No transactional workload rollback is promised.
- 2026-10-23: integrated regressions now pass and the concurrency case uses distinct Agents. Authorized list/detail runtime observations now distinguish desired state, last observed state, offline connectivity and stale observations. Live service/container observations and real-runtime recovery/cancellation/progress remain incomplete.

### P15 — Live status, logs, and container terminals

- Stream Agent health, deployment status, logs, and operation progress over authenticated WebSockets with bounded buffering and reconnect behavior.
- Implement browser terminals through Control Plane-to-Agent forwarding and Docker exec, including input/output, terminal resizing, exit status, and explicit closure.
- Require separate terminal permissions and authorize the specific Agent/deployment/container. Close or deny sensitive sessions when their identity or permissions are revoked.
- Audit terminal opener, target, start/end, and outcome. Do not record terminal keystrokes or full contents by default.
- Keep interactive host terminals out of scope; host-targeted actions are a separate capability.
- Tests: subscription isolation, backpressure, reconnect, terminal input/output/resize, process exit, disconnect cleanup, access revocation, and sensitive-output handling.
- Added in this pass: actual Docker Unix-socket PTYs, scoped exec targets, input/output/resize/exit/cleanup, self-hosted xterm, continuous Docker stdout/stderr logs including stopped-container tails, bounded read-only log gateways, policy-aware connection limits, and metadata-only audits.
- Focused verification includes real Kestrel/WebSocket/browser and Docker PTY/log forwarding cases. Stream traffic does not enter the work ledger. Operation progress forwarding/UI was added afterward and still requires dedicated final integration verification. Host terminals remain out of scope.

### P18 — Operational UI and audit history

- Provide dashboard, Agents, deployments, registries/Git credentials, actions, schedules, webhook deliveries, users/groups, settings, import/export, and audit views.
- Deployment detail views must expose Compose/files, all environment scopes and provenance, sources/branch browser, storage warnings, saved/deployed revisions, services, logs, terminals, operations, and automation.
- Show actionable errors and clearly distinguish loading, offline, empty, denied, and failed states. Use HTMX for partial updates and WebSockets for real-time changes.
- Record configuration changes, privileged-account administration, Agent trust changes, credential access, exports/imports, action triggers, deployment operations, and terminal metadata with actor/resource/time/result.
- Expose operational retention and application limits as authorized UI settings. Redact secret values from audit records and routine diagnostic output.
- Tests: page/endpoint authorization, HTMX validation and errors, effective-permission explanations, audit completeness/redaction, and browser workflows for all major management screens.
- Added in this pass: source/credential, operation, action/schedule/webhook/image-policy, portability, recovery, policy, terminal, and live-log screens; resource-filtered audit history; and retention/settings controls. Mutation audits persist before response headers and never capture request bodies or terminal transcripts.
- Remaining: final browser/authorization/error-state consistency review, runtime progress tests, and release smoke checks. Some configuration edits have dedicated audit events while generic HTTP mutation metadata supplies the baseline; audit completeness must still be reviewed before release.

Verification constraints: Docker became available during P20. This pass added focused real Docker lifecycle/registry/PTY/log and PostgreSQL recovery tests, but the final integrated opt-in and rebuilt-image release checks are incomplete. Local CLI commands require `-p:AllowMissingPrunePackageData=true` because this host SDK lacks pruning metadata; that workaround is not an application build setting. Temporary diagnostic warning suppression used by a subagent is not accepted as final verification; no such suppression was added to project files.

Previously verified baseline before the current implementation pass (2026-10-22):

- `dotnet test Stanza.slnx --no-restore -p:AllowMissingPrunePackageData=true --verbosity minimal`: 621 passed, zero failed/skipped after P20. These baseline results are historical; the current expanded implementation requires a new integrated verification pass.
- `node --test tests/Stanza.Tests/Live/status.test.cjs`: 9 passed, zero failed/skipped.
- Both application projects publish successfully in Release configuration with the same local SDK workaround. Publish outputs used `/tmp/opencode/stanza-publish/`, not committed deployment artifacts.
- Both Docker image builds and fresh Control Plane/PostgreSQL startup were verified under P20. Full Agent workload dispatch, container lifecycle, automation, and recovery verification belong to the current implementation pass and are not covered by that earlier smoke test.
- Current startup guidance: run `docker compose -f compose.control.yaml up -d` on a Docker-enabled host and visit `http://localhost:8080/setup`. Images default to latest; optionally set the same published UTC STANZA_VERSION on both applications. The listener binds only to loopback unless changed. Prefer trusted HTTPS termination; trusted-LAN HTTP requires STANZA_ALLOW_INSECURE_HTTP=true on each participating deployment and an appropriate public origin. Retain all Control Plane volumes/encryption keys and each Agent's local ./agent_data directory. For multiple Agents, use separate installation directories/projects and do not clone/share identities. Local source builds use the Dockerfiles, not deployment manifests. These instructions require updated images; current source changes were not published/deployed by this agent.
- This baseline predates the expanded feature code. The resumed-implementation snapshot and P27 verification above are the authoritative current source status; the 2026-10-22 pause snapshot is retained only as history. No source-suite result establishes that current rebuilt images or all required runtime flows have been verified.

## DONE

### P23 — PR-only test gate and main-branch image builds

- Run the stable Test application check only for pull requests targeting main, including opening, reopening and new commits; do not skip files or suppress test failures.
- On pushes/merges to main, build/publish both matching-version images without running or depending on the PR test job. Preserve existing date-tag publishing and manual build-only behaviour without tests.
- Do not run branch-push workflows for feature branches, or build/publish images from PR workflows. Retain read-only PR permissions and guarded registry credentials.
- Explain how to require Test application and PR-based changes through a main-branch GitHub ruleset, including up-to-date checks and bypass restrictions. Do not modify remote repository rules without authorization.
- Tests: workflow trigger/job/dependency routing for PR/main/tag/manual cases, stable required-check identity, absence of failure suppression, publishing-policy regressions, workflow lint and integrated solution checks.
- Verified 2026-10-23: explicit PR-only verify guard, main-only branch push trigger, PR open/update/reopen/ready/retarget events, and independent non-PR release/image jobs. Images depend only on release, so a skipped PR test job cannot skip main/tag/manual builds. Six routing regression cases and actionlint pass; integrated Release solution: 1,578 passed, zero failed, 8 runtime cases skipped; all 33 client cases pass.
- External boundary: no GitHub workflow was dispatched, no image was pushed, and no repository protection/ruleset was changed. Enable an active main ruleset requiring pull requests and the GitHub Actions Test application check, preferably up-to-date branches and no bypass actors. The check must appear from a PR run before selecting it in repository settings. This source change cannot itself enforce remote merge rules.

### P22 — Explicit Compose entrypoints and publishing workflow review

- Supply exactly three root manifests: compose.control.yaml for Control Plane/PostgreSQL, compose.agent.yaml for the ordinary Agent, and compose.agent.privileged.yaml as the explicit high-privilege Agent override. Preserve existing opt-in security boundaries. Both application services pull from ghcr.io/sas41/stanzaodm with component-specific suffixes and use the same STANZA_VERSION variable; following the user's later request, unset/blank values default to latest and exact matching UTC release pins remain optional. No deployment-time source build.
- Subsequent user change P27 adds a fourth root entrypoint, compose.yaml, for the complete example stack. The three component/profile manifests remain; the original exact-three constraint above is historical.
- Update startup commands, manifest references and CI validation for the renamed files; validate the ordinary and combined privileged Agent configurations.
- Use UTC date versions in YYYY-MM-DD-HHMMSS format (for example 2026-10-23-115011), chosen once per workflow run and shared by Agent/Control Plane image tags and OCI version labels. Validate real calendar/time values. Date-tag releases use their exact Git tag; do not generate semantic versions.
- Ensure the workflow verifies the solution and every client suite before building both images. Publish/login only on default-branch pushes and valid date-version tag pushes; retain lowercase repository-derived image names, pinned actions, scoped token permissions and separate build caches.
- Subsequent user change P23 supersedes the test/build routing above: only main-targeted PRs run tests; main pushes/merges and date tags build/publish independently, and manual runs build without tests or publishing.
- Tests: manifest naming/service/security contracts, actual Compose configuration rendering, all client-suite discovery, executable publish-policy cases including malformed versions and nonpublishing events, workflow lint and integrated solution regressions. Record local Docker/GitHub execution limitations without claiming a registry push occurred.
- Verified 2026-10-23: 87 repository/build-policy cases pass, including daemon-free rendering of all three deployment configurations, valid/leap/invalid date versions, matching component versions and nonpublishing PR/manual/feature-branch policies. actionlint 1.7.7 passes; all 33 client cases pass; integrated Release solution: 1,542 passed, zero failed, 8 runtime cases skipped.
- External boundary: this agent did not dispatch a GitHub workflow or push an image. This host still has no Docker daemon socket, so current image builds/startup were not exercised locally. The user reports deploying GHCR images, but their provenance and inclusion of the latest source changes were not verified by this agent. These limits do not imply that P19 release verification is complete.

### P01 — Solution structure and runtime boundaries

- Build on the current .NET LTS with two deployable components: Stanza Control Plane and Stanza Agent. Use a modular application, not independently deployed microservices.
- Use three production projects: Stanza.ControlPlane, Stanza.Agent, and Stanza.Core. Core contains shared protocol contracts and runtime-independent rules; Control Plane owns persistence/services and Agent owns runtime adapters. Each application references only Core as an internal project dependency.
- Use ASP.NET Core server-rendered views, HTMX for dynamic interactions, and WebSockets for live data. If using SignalR, require WebSocket transport for live connections.
- Use PostgreSQL for Control Plane state. Develop for fresh installations; do not introduce legacy compatibility or database migrations.
- Maintain a runnable containerized application at each increment. Incomplete features must be clearly unavailable rather than exposed as functioning controls.
- Tests: architecture/dependency boundaries, configuration validation, application startup, and component health endpoints.
- Verified under P20: three-project architecture, BCL-only Core, two application entrypoints, server-rendered HTMX/WebSocket UI, PostgreSQL persistence, health/startup tests, both image builds, and fresh Control Plane/PostgreSQL startup. Remaining runtime features are tracked separately rather than keeping the foundational architecture task open.

### P20 — Consolidate production projects and add image publishing

- Replace Domain and Contracts projects with one dependency-light Stanza.Core library containing shared rules and protocol contracts.
- Move Control Plane-specific Infrastructure services and persistence into Stanza.ControlPlane; keep Agent runtime implementations in Stanza.Agent. Both applications reference only Core as an internal project dependency.
- Update namespaces, Razor views, solution references, tests, package ownership, and assembly-boundary checks without changing persisted schema or protocol behavior.
- Expand the existing .gitignore to exclude build outputs, local credentials, environment overrides, runtime data, test artifacts, and editor state while preserving safe example files.
- Add a GitHub Actions workflow that verifies the application and builds both images, publishing to GHCR only for default-branch pushes and version tags. Pull requests must never publish or log in to a registry.
- Use repository-derived lowercase image names, scoped GITHUB_TOKEN permissions, pinned action revisions, component-specific build caches, and explicit image tags.
- Tests: full .NET and JavaScript regression suites, new architecture/build-policy checks, ignore-rule behavior, workflow syntax/security validation, Release publishing, and local Docker image builds where available.
- Verified: 621 .NET tests in Debug/Release and 9 JavaScript tests pass, including real browser flows, new three-project dependency checks, executable publishing-policy cases, and Git ignore behavior in isolated temporary repositories. GitHub workflow passes actionlint, which is also included in CI.
- Verified: both Docker images build with the official .NET SDK; published dependency manifests contain only the application and Core as internal projects, Agent contains no EF/Npgsql dependencies, and both images run as non-root UID 1654. Packaged Compose and Buildx clients execute successfully.
- Verified: a uniquely named temporary Compose stack initializes real PostgreSQL, creates expected tables, and serves `/health/live`, `/health/ready`, and `/setup` successfully; all of its containers, network, and volumes were removed afterward.
- Workflow: `.github/workflows/docker-images.yml` and tested `.github/scripts/image-metadata.sh`; images are `ghcr.io/<owner>/<repository>-control-plane` and `ghcr.io/<owner>/<repository>-agent` in lowercase, initially Linux amd64. P22 replaces the original semantic-version policy with one shared UTC date version for both images. P23 limits branch pushes to main and tests to main-targeted PRs. Main pushes publish date/latest/SHA tags; valid date-tag pushes publish that exact date and SHA tags without tests. PRs run tests without image builds/publishing; feature branch pushes do not trigger the workflow; manual runs build without tests/publishing. Uses GITHUB_TOKEN; no extra registry secret is required.
- External boundary: this agent did not initialize a repository, make a commit, dispatch a workflow or push an image. The configured remote is sas41/StanzaODM and the user reports deploying GHCR images; this does not establish that those images include the current source changes. Actual publishing/package permissions and rebuilt-image provenance remain outside locally observed verification.

### P03 — Local authentication and session security

- Implement local user accounts, secure password hashing, login/logout, password changes, session expiration, and administrator-managed resets for eligible accounts.
- Apply CSRF protection to browser mutations, secure cookie settings, login throttling, and session invalidation after relevant account/security changes.
- Never log passwords, session tokens, enrollment secrets, registry credentials, or Git credentials.
- Tests: authentication success/failure, throttling, CSRF, password changes/resets, disabled accounts, expiration, and session revocation.
- Verified: account-service integration tests and actual HTTP tests cover authentication, bounded passwords, dummy verification, resets/changes, revocation, controlled-clock expiration without sliding renewal, throttling, CSRF, open-redirect rejection, privileged-account boundaries, and HTML secret leakage. Production session cookies are Secure by default, including behind HTTP reverse-proxy backends; P24 adds the user-approved default-off bootstrap exception for HTTP browser access. Real Chromium flows cover default loopback startup and explicitly opted-in non-localhost HTTP login/WebSockets. Broader UI-configurable policy settings remain tracked under P02, and full container verification under P19.
