Do not create markdown files unless asked to.
Create Unit tests for everything, account for unit tests for all changes.
Do not rush to implement, stop and ask for clarification.
Do not try to account for legacy data or systems, unless specifically told to, do not write migrations unless asked to, always develop as if it'll be a fresh deployment.
The software should always be in a state, that would work out of the box.
Expose every setting via the UI, all configuration of the software should be doable via the UI, database connection should be configurable pre-launch however.
The application is inteded to run fully dockerized.
The application and it's deployments and agents should all be easy to deploy and manage.

The tech stack is .Net LTS, dynamic elements use HTMX, real time things use WebSockets for real time data.
Keep exactly three production projects: Stanza.ControlPlane, Stanza.Agent, and Stanza.Core. Both applications reference only Core as an internal project dependency. Core contains shared contracts and runtime-independent rules, with no PostgreSQL, Entity Framework, ASP.NET, or application dependencies. Control Plane owns its persistence/services; Agent owns local runtime execution.

PLAN.md is the implementation source of truth. Read it before starting work and keep it current as implementation progresses.
All planned implementation tasks start under TODO. Move the complete task, including its stable identifier, acceptance criteria, and test requirements, to IN-PROGRESS when work begins, then to DONE only after implementation and verification are complete.
Do not mark partially implemented or unverified work DONE. Record any remaining work or blockers with the task, and keep unrelated tasks in their existing sections.
Every implementation change must include appropriate unit tests and account for integration or browser tests where needed. A documentation-only update does not by itself complete an implementation task.
Do not expand the agreed scope or make unresolved product/security decisions silently; ask for clarification before implementing those changes.
When delegating parallel work, assign exclusive file/directory write ownership and explicit contracts before work begins. Subagents must not edit shared project files, entrypoints, packaging, or PLAN.md unless ownership is explicitly assigned to them.
The coordinating agent owns integration and plan updates. Release ownership before another agent edits the same files, coordinate builds/tests that share output directories, and run the integrated solution tests before reporting completion.
