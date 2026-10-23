# Stanza Open Deployment Manager

A Dockerized Control Plane and outbound-connecting Agents for managing Compose deployments.

## Start the example stack

Requires a Linux Docker host, Docker Compose v2+, and access to the GHCR images. The default `compose.yaml` runs PostgreSQL, the Control Plane, and one Agent. `compose.control.yaml` and `compose.agent.yaml` are alternatives for separate hosts—not additional stacks to start against the same data.
For an existing installation, retain its Compose project name and data mounts when changing entrypoints; a different project name selects different database volumes.

1. Copy the configuration template:
   ```bash
   cp example.env .env
   ```
2. Edit `.env`: replace every `CHANGE_ME`, set a friendly `STANZA_AGENT_NAME`, and obtain `STANZA_DOCKER_GID` on the Agent host:
   ```bash
   stat -c '%g' /var/run/docker.sock
   ```
   **Leave the enrollment-token placeholder until step 5.** Set `STANZA_CONTROL_PLANE_URL` to an origin reachable from the Agent container; its `localhost` is not the Docker host. Use valid HTTPS through your reverse proxy by default. For a trusted-LAN HTTP test, explicitly set:
   ```dotenv
   STANZA_BIND_ADDRESS=0.0.0.0
   STANZA_HTTP_PORT=8080
   STANZA_ALLOW_INSECURE_HTTP=true
   STANZA_CONTROL_PLANE_URL=http://192.168.1.10:8080
   ```
   Replace that example IP with your server's address. HTTP exposes passwords, tokens, deployment secrets and commands; opt in on **both** deployments when using separate hosts. The secure defaults remain loopback binding and HTTP mode off.
3. Start only the Control Plane and its database:
   ```bash
   docker compose up -d control-plane
   ```
   Open `http://localhost:8080/setup` locally, or your configured HTTPS/LAN address, and create the first System Admin. There are no default application credentials.
4. In **Settings**, set the public URL to the address used by your browser. For TLS termination, configure the actual trusted proxy IP/CIDR in **Operational policy**; do not trust arbitrary forwarded headers.
5. In **Agents**, generate an enrollment token and put it in `.env` as `STANZA_ENROLLMENT_TOKEN`. Then start the complete stack:
   ```bash
   docker compose up -d
   ```
6. Approve the expected friendly Agent name/fingerprint in **Agents**. The Agent creates its own identity; do not generate or delete identity files manually. Execution starts only after approval.

Images default to `latest`; set the same UTC `STANZA_VERSION` (for example `2026-10-23-115011`) on CP and Agents to pin an exact matching release. Updates to environment variables require container recreation, not just `restart`:

```bash
docker compose up -d --force-recreate control-plane agent
docker compose logs --tail=100 initialize-agent agent
```

Keep the database/Control Plane named volumes (including encryption keys and database credentials) and `./agent_data`. Do not use `down -v` as a troubleshooting step. Docker socket access grants substantial host control.

### Additional Agents

Copy `compose.agent.yaml` and `example.env` into a **separate installation directory** per Agent, configure its own name/token and the CP URL/socket GID, then run `docker compose -f compose.agent.yaml up -d` there. Each gets its own `./agent_data`; the internal path stays `/var/lib/stanza-agent`. Use unique folder/project names (`-p` can select a project name), keep Docker's default container hostname, and never copy/share a running Agent's identity. Friendly names are labels, not replacements for cryptographic IDs.

For opt-in host actions, combine the base file with `compose.agent.privileged.yaml` and enable host actions in the UI. Ordinary Agents remain non-root; the privileged profile can execute as host root. Agents sharing a Docker daemon are **not** a security isolation boundary and must avoid conflicting ports, explicit container/volume names and local image tags.

## Test a deployment through your Agent

1. Open **Deployments → Create deployment draft**, enter `Environment test`, and select your approved Agent.
2. Paste this into **Main Compose file** (keep the `${...}` and `$${...}` exactly as shown):

```yaml
services:
  test-web:
    image: nginx:alpine
    ports:
      - "33333:80"
    environment:
      TEST_MESSAGE: ${TEST_MESSAGE}
      TEST_ENV: ${TEST_ENV}
      TEST_VALUE: ${TEST_VALUE}
    command:
      - /bin/sh
      - -c
      - |
        cat > /usr/share/nginx/html/index.html <<EOF
        <!DOCTYPE html>
        <html>
        <head>
          <title>Docker Environment Test</title>
        </head>
        <body>
          <h1>Docker is working!</h1>
          <h2>Environment variable test</h2>
          <ul>
            <li>TEST_MESSAGE: $${TEST_MESSAGE}</li>
            <li>TEST_ENV: $${TEST_ENV}</li>
            <li>TEST_VALUE: $${TEST_VALUE}</li>
          </ul>
        </body>
        </html>
        EOF
        exec nginx -g 'daemon off;'
```

3. Paste this into **Deployment environment (.env)**:

```dotenv
TEST_MESSAGE=Environment variables work!
TEST_ENV=development
TEST_VALUE=12345
```

4. Save, choose **Validate saved Compose on Agent**, and wait for success in Operations. Then select **Deploy / redeploy** under **Runtime operations** and queue it.
5. Open `http://AGENT_HOST:33333` using the actual Agent host's address—not necessarily the CP host. Allow that port through its firewall. You should see “Docker is working!” and all three values. Use dummy values: this example publishes them in the page.

`${...}` is resolved by Compose; `$${...}` defers expansion to the container's shell. Saving/synchronizing configuration does not deploy it. Environment precedence is **Global → Agent → Deployment**; runtime container variables still need explicit `environment:` mappings or a service `env_file:`.
The stack's installation `.env` configures Stanza itself; the deployment `.env` above belongs in Stanza's editor, not in the installation file.

## Deployment recipes

### 1. Compose images and `.env` only

1. Create a draft, select an approved Agent, paste your Compose and deployment `.env`, then save.
2. Reference values with `${KEY}`. To inject every value from the authored deployment file, use `env_file: ./.env`; this is not the merged Global/Agent environment. Add supporting `app.env` files through the file editor if needed.
3. For private images, create registry credentials under **Credentials & registries**, test them, and bind them on the deployment's **Git and registry sources** page. Public images need no credential binding.
4. Validate, deploy, and inspect **Operations**, **Live logs**, or **Container terminal**. Configuration edits require another explicit deployment to take effect.

### 2. Build application code from Git

Compose is maintained in Stanza; Git supplies isolated application build inputs, not a Git-managed Compose file. HTTPS repositories are supported; SSH URLs and submodules are not.

1. Create the draft and select its Agent. For a private repository, first create a Git credential in **Credentials & registries**.
2. Open the deployment's **Git and registry sources**, add the repository URL/ref/credential, and use destination `sources/app`. Save, then browse/search branches or tags; an exact commit can also be selected.
3. If the repository has its own root `Dockerfile`, use:
   ```yaml
   services:
     app:
       image: stanza-git-demo:local
       build:
         context: ./sources/app
       ports:
         - "33334:3000"
   ```
   Adapt the port to your application, which must listen on `0.0.0.0` inside its container.
4. **If the repository has no Dockerfile**, create a supporting text file named `Dockerfile.app` in Stanza. For example, for a Node application with a lockfile and `npm start`:
   ```dockerfile
   FROM node:24-alpine
   WORKDIR /app
   COPY package*.json ./
   RUN npm ci --omit=dev
   COPY . .
   CMD ["npm", "start"]
   ```
   Set `dockerfile: ../../Dockerfile.app` under `build:` above. That path is relative to `sources/app`, while `COPY` still reads the repository build context. Use a Dockerfile suitable for your application's language/dependencies.
5. Put runtime values in the deployment environment and map them in `environment:`; build-time values need explicit `build.args`. Validate and deploy. Deployment performs source checkout, build/pull, apply and health waiting; do not run Docker commands manually inside the Agent.

### 3. Mix pulled images, Git builds and managed local builds

1. Configure `sources/app` as above. In the file editor, create `worker/Dockerfile` and any files its build needs. For a minimal worker, use `FROM alpine:3.22` and `CMD ["sleep", "3600"]` on separate lines.
2. Use one Compose file for all services:
   ```yaml
   services:
     web:
       image: nginx:alpine
       ports: ["33335:80"]
     app:
       image: stanza-mixed-app:local
       build:
         context: ./sources/app
     worker:
       image: stanza-mixed-worker:local
       build:
         context: ./worker
     cache:
       image: redis:7-alpine
   ```
3. Add environment mappings/credentials as needed, save, validate and deploy. Stanza builds build-backed services and pulls image-only services. Use deployment-specific image tags when Agents share a daemon. Add health checks and connect services for your actual application; the snippet only demonstrates source types.

### 4. Create and mount managed files

1. Open the draft's supporting files. Create directories/text files or upload binary assets; for example `site/index.html` containing `<h1>Managed file works</h1>`.
2. Mount relative managed paths in Compose:
   ```yaml
   services:
     web:
       image: nginx:alpine
       ports: ["33336:80"]
       volumes:
         - ./site:/usr/share/nginx/html:ro
   ```
3. To mount only the existing HTML file, replace the directory mount with `./site/index.html:/usr/share/nginx/html/index.html:ro`. Other config files work the same way, for example `./config/site.conf:/etc/nginx/conf.d/default.conf:ro`; create a valid nginx server configuration at that path first. Add separate environment files with `env_file: ./app.env` if needed.
4. Save, validate and redeploy. The Agent resolves managed mounts to its actual host storage; arbitrary host files are not editable through Stanza. Explicit host bind mounts and named volumes refer to the selected Agent's host and are not portable application data. Do not combine the read-only site mount with the smoke test's command that writes `index.html`.

### 5. Move between Agents

1. Enroll/approve the destination Agent. Check architecture, available ports, credentials and destination Global/Agent environment values.
2. Prepare destination bind mounts/volumes and make a workload-consistent backup/data-transfer plan yourself. **Stanza moves configuration/placement, not persistent workload data.** The data-transfer toggle is deliberately disabled.
3. Open **Clone, move & import/export → Move deployment placement**, select the deployment/destination, preview variables/storage warnings, and acknowledge the source-stop/destination-start operation.
4. Queue the guided move and monitor its operations: destination validation, source stop, destination deploy/health check, then placement update. Previews are warnings, not proof of host-wide conflict freedom.
5. On success verify the destination application, then explicitly request source-container cleanup if desired. On failure inspect both Agents and the recovery record before repeating commands; do not assume rollback or delete source volumes. Use **Clone** instead to create a separate inactive deployment identity.
