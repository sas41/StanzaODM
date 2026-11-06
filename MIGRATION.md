# Manual migration runbook

For the remote operator/agent upgrading the existing Stanza installation. Follow the actual deployed configuration, not assumptions from the repository examples. No deployment, pruning, journal repair or lock release has been performed by the source-code agent.

## Scope and compatibility

This source build uses Agent channel protocol **7**, enrollment protocol **1**, and database schema **4**. Both application images must contain the matching changes. Merely pulling the previously published `latest` images does not install this work.

Implemented changes:

- Script actions for deployment containers and for Docker administration on a selected Agent, including maintenance with no deployment.
- One temporary output spool per new execution; completed output/result archived in the CP database before Agent output cleanup is acknowledged.
- Atomic journal writes, terminal-result persistence retries and storage-aware Agent readiness.
- Read-only container logs; interactive terminal endpoints, PTYs, keyboard forwarding, resizing and xterm.js removed.
- Unused alternate Docker executor removed. Docker CLI, Compose, Buildx, Bash and OpenSSH retained.

**Do not perform the still-pending redesign as part of this migration.** This batch does not yet convert operation workspaces to the proposed permanent deployment folders, replace the authorization model with two roles/group-only access, replace the general portability framework, or collapse all legacy automation records. Existing definitions, permissions, schedules and storage must not be rewritten in anticipation of those changes.

The source suite passes, but the opt-in Docker/PostgreSQL cases were not exercised in this environment. Packaged-runtime checks below remain necessary.

## 1. Inventory before changing anything

Record, without publishing secrets:

- Existing Compose installation directories, project names, Compose files/overrides and image tags/digests on each host.
- CP and Agent image revision labels. The incident report identifies revision `ec42fd35` for both published applications; verify the running containers rather than assuming this is still the case.
- PostgreSQL version, database volume, CP data/key volume, database-secret volume and actual Agent data bind mount.
- Agent ID/fingerprint/name, public CP URL, Docker endpoint/socket group, HTTP/TLS/proxy configuration and any privileged override currently in use.
- Current deployment container IDs, image IDs, mounts and endpoints, plus queued/running/uncertain operations and action/schedule states.

Useful read-only discovery commands:

```bash
docker ps --format '{{.ID}}\t{{.Names}}\t{{.Image}}'
docker inspect --format '{{json .Config.Labels}}' <control-plane-container>
docker inspect --format '{{json .Mounts}}' <agent-container>
docker system df -v
df -h
df -i
```

Replace placeholders with discovered names. Do not dump container environments, `.env`, private keys or unredacted job output into the migration report.

Keep the existing Compose project name and mount sources. Changing the project name or switching from separate-host manifests to the combined example can select different volumes. Do not replace the existing `.env` with `example.env`.

## 2. Establish headroom without deleting application data

Pause new Stanza triggers and record which actions/schedules/webhooks/image-update policies were enabled. Let accepted work finish, or request cancellation and verify its actual outcome. Do not repeatedly queue the incident's locked action.

If the Agent has an in-memory terminal result awaiting persistence, restore writable storage before restarting it. Restarting destroys that memory; it does not manufacture completion evidence.

Write backups to another filesystem or off-host, not to the nearly full root filesystem. Check both bytes and inodes on the filesystems holding Agent data and Docker data. The Agent's health probe measures its data filesystem, not every possible Docker/build-cache filesystem.

After explicit approval of the affected Docker daemon, reclaim unused build cache and dangling images. Inspect builders and candidates first:

```bash
docker buildx ls
docker buildx du
docker image ls --filter dangling=true
```

Possible cleanup commands, only after reviewing their scope, are `docker builder prune --filter 'until=168h'` for the relevant builder and `docker image prune` for dangling images. They are daemon/builder-wide, not Stanza-deployment-scoped. Add unattended confirmation flags only after approval. Do not assume the reported totals are all reclaimable.

Do not run `down -v`, volume pruning, `system prune --volumes`, broad recursive deletes or blanket ownership changes. Do not automatically delete operation workspaces by age.

Before removing any old workspace, inspect mounts of **all existing containers, including stopped and maintenance containers**, and running build/action processes. Protect paths referenced directly or through parent/child bind mounts. Also inspect for persistent application data even if no container currently mounts it. Keep the incident's workspace and diagnostic evidence until recovery is complete. Produce a candidate report and stop if ownership or data purpose is uncertain.

## 3. Back up the quiesced installation

Preserve together:

- PostgreSQL database contents, including configuration, users/groups/grants, actions, schedules and operation history.
- CP encryption/data-protection keys and its existing data/secret volumes.
- The Agent data directory: identity, request ledger, job journals, output chunks/spools, configuration, workspaces and any workload data it contains.
- Compose files/overrides, the existing `.env`, image digests and custom settings.

Use a private backup location and restrictive permissions. A PostgreSQL logical backup can be taken from the existing database container. With the repository's standard database service/variables, for example:

```bash
umask 077
docker compose exec -T database sh -ec 'export PGPASSWORD="$(cat "$POSTGRES_PASSWORD_FILE")"; exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > /separate-filesystem/stanza-backup/database.dump
```

Use the installation's existing project/file flags and actual credentials mechanism if different. Check the command's exit status and validate the dump with a matching `pg_restore --list`; a created file alone does not prove a successful backup. Do not restore it over a live database as a test.

Copy keys and Agent state consistently after quiescing writes. Preserve original bytes, ownership and permissions. Never regenerate/delete `identity.pk8`, clear the request ledger, or remove encryption keys to make an upgrade start.

## 4. Install matching application images

1. Publish or obtain matching CP and Agent images built from the approved changes, including newly added source files. Record their immutable image digests and revision labels.
2. Pin the same approved `STANZA_VERSION` in the existing configuration on both hosts. Preserve other customized values, mount sources, PostgreSQL version and Compose project identity.
3. Keep automatic triggers paused during the version mismatch. Upgrade the CP, then the Agent promptly. Protocol mismatch is expected to fail closed, not execute work.
4. Recreate only the application services, using the existing Compose file/project/override arguments. For an already initialized standard combined installation:

```bash
docker compose pull control-plane agent
docker compose up -d --no-deps control-plane
docker compose up -d --no-deps agent
```

For separate hosts, run only the relevant service commands in each original installation. Do not start a second stack against the same state. Do not recreate PostgreSQL or initialize empty replacement volumes.

5. Verify the applications use the original keys, database and Agent identity. Existing users/deployments/settings must remain present. Check logs for protocol, permission, storage and migration errors without publishing secrets.

The schema remains version 4 for this batch; there is no new SQL transformation requiring a reset. Do not alter the schema-version marker manually. Existing installations older than schema 4 still require the application's supported versioned upgrades and their original encryption keys.

New jobs use journal format 2 and a `<operation-id>.output` spool under `jobs-v1`; format-1 jobs/chunks remain readable. Preserve both formats. No bulk journal conversion is required.

## 5. Convert actions explicitly

Do not automatically translate arbitrary multi-step plans into shell scripts. Inventory each action, its access assignments, confirmation text, owner, schedules and effects first.

For an existing Docker-maintenance action:

1. Pause the action and its automatic triggers; record their previous states.
2. Open the existing action so its ID/access assignments can be retained. Inspect the displayed legacy definition.
3. Choose **Host Docker**, leave Deployment empty, and select the intended Agent.
4. Supply the reviewed Bash script and an appropriate confirmation message. `docker system prune --force` is supported, but affects the whole daemon. Do not run it merely to test migration.
5. Explicitly confirm replacement of the legacy steps/inputs. Review removed parameters and any dependent triggers. Saving changes execution ownership to the editor; verify that account remains appropriate and authorized.
6. Validate first with a separate non-destructive action such as `docker version` or `docker info`. Restore intended schedules/access only after review.

Deployment actions select their deployment instead. Container scripts select its Compose service; the container must already be running and contain `/bin/bash`. Sending a complete script over stdin is supported; interactive keystrokes are not.

Button metadata and existing concurrency settings are preserved when editing. Imports remain inactive until explicitly enabled. Historical execution records are not rewritten.

Host Docker actions use the ordinary Agent's Docker socket and need no privileged host-shell profile. Older `host-exec` definitions/profile support remains for compatibility until explicitly replaced. Do not remove an existing privileged override as an incidental upgrade step: first convert its actions, drain work, back up state and inspect ownership. A former root-run Agent can have root-owned private metadata. Any ownership repair must target verified Agent-owned metadata only, preserve modes/identity, reject symlinks, and never recursively change ownership of workload data or live bind mounts.

OpenSSH remains installed. Preserve existing SSH keys, host-key trust and authored Git scripts; do not disable host-key verification. The managed Git-source UI currently accepts HTTPS only, so do not claim that a private-key/known-hosts editor has been added. The branch dropdown and shallow selected-reference fetching remain available for managed sources.

## 6. Handle the reported uncertain execution separately

An application upgrade is **not** proof that the old job stopped. HTTP 200 from the previous containers, `ApplyStarted=false`, a late user cancellation, or absence of a disconnect record are not individually sufficient to fabricate a result.

Preserve the incident's original CP records, request hash, executor ledger, job journal, process/daemon diagnostics, valid output prefix and the empty chunk files. Disk exhaustion is strongly supported by the report, not conclusively logged. Do not relabel the later `CancellationReason=user` as the original cause.

Recovery must establish the specific execution's stopped-before-apply outcome using trustworthy evidence and the actual published execution/checkpoint code. Check for remaining CLI children and daemon-side work; stopping the Agent alone is insufficient. Verify the deployment's original container/image identities and correlate all evidence to the exact operation/request hash.

- If a valid existing terminal receipt is recovered, the upgraded Agent can import its stopped evidence without re-executing the request. Then use **Inspect and reconcile** in the CP to refresh container status and resolve the operation and associated automation locks together.
- If no trustworthy terminal receipt exists, stop and report that the run remains blocked. A separately reviewed, explicitly approved incident repair is required to record independently verified completion evidence. It must preserve the original request hash/ID, existing evidence and incomplete-output status, use atomic durable writes, and never assert success or no-apply solely to release a lock.
- Do not force-clear `ActiveRunId`, edit CP states directly, delete/reuse the request ID, or set `Stopped=true` simply because a normal retry is skipped.

After durable recovery and CP output archival, any corrupt legacy chunks beyond the journal's valid sequence remain a separate evidence/cleanup decision. The new cleanup deliberately refuses unexpected legacy entries rather than deleting them. Keep copies and do not remove disputed files before verification and approval.

## 7. Acceptance checks

Before enabling automatic work, verify:

- Both images are the approved matching build and the original Agent identity reconnects without replacement.
- Existing users, permissions, deployments, customized settings and workload data are intact.
- CP readiness and Agent `/health/ready` are checked separately. Low space, pending terminal persistence or unresolved jobs produce Agent readiness failure. Do not hide the warning by lowering the threshold just to turn health green.
- A non-destructive Agent-targeted Docker script runs without a deployment or privileged shell profile.
- A container script runs against the intended service, with failure/cancellation results reported correctly.
- Branch selection still works; a selected source fetch is shallow and does not recursively fetch submodules.
- Live container logs work for running/stopped containers. `/terminals/<id>`, `/live/terminal/<id>` and xterm assets are absent.
- A viewer joining mid-operation sees earlier output and catches up. During the run, new output uses one temporary spool rather than thousands of chunk files.
- After completion, output and result are in PostgreSQL before the Agent spool disappears. History remains available with the Agent offline. Small replay receipts remain; they must not be removed.
- On a controlled test, a CP/network interruption retains the Agent spool and does not re-execute accepted work. Reconnection allows archival and cleanup. Do not simulate disk exhaustion on the production filesystem.
- Existing application endpoints and persistent data remain correct after the smoke deployment.

Only then restore the explicitly intended automatic triggers. Record restored states and any deliberately paused/blocked items.

## Stop/rollback boundaries and final report

If any check fails, keep triggers paused and preserve evidence. Do not restore an older backup over newly accepted execution receipts: that could permit duplicate side effects. An old Agent that only understands format-1 journals is not automatically safe against a data directory containing format-2 jobs. Any downgrade needs a separate compatibility/data-preservation review, even though the CP schema number is unchanged.

Report image revisions/digests, backup verification/location, preserved mount/project/identity information, reclaimed headroom and exact approved cleanup commands, changed action IDs and owners, runtime checks, output archival/cleanup verification, and outstanding blockers. Redact secrets and distinguish observed results from assumptions. Explicitly report the uncertain incident as unresolved if its completion evidence cannot be established.
