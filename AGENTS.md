Do not create markdown files unless asked to.
Create Unit tests for everything, account for unit tests for all changes.
Do not rush to implement, stop and ask for clarification.
Preserve existing data and support upgrades of existing installations. Include versioned schema and persisted-data migrations whenever changes require them. Test both fresh installation and upgrade paths. Never require resetting or recreating data to deploy an update. Preserve existing customized settings unless explicitly instructed otherwise.
The software should always be in a state, that would work out of the box.
Expose every setting via the UI, all configuration of the software should be doable via the UI, database connection should be configurable pre-launch however.
The application is inteded to run fully dockerized.
The application and it's deployments and agents should all be easy to deploy and manage.

The tech stack is .Net LTS, dynamic elements use HTMX, real time things use WebSockets for real time data.
Keep exactly three production projects: Stanza.ControlPlane, Stanza.Agent, and Stanza.Core. Both applications reference only Core as an internal project dependency. Core contains shared contracts and runtime-independent rules, with no PostgreSQL, Entity Framework, ASP.NET, or application dependencies. Control Plane owns its persistence/services; Agent owns local runtime execution.
