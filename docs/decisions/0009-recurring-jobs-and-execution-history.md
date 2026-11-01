# Recurring jobs, scoped triggers, execution history and audits

Status: implementation requested by the owner on 2026-10-20, on `feature/durable-network`.

The reference is disler/pi-durable-news-agent at `273b79bb8d3d8907cae9fbd6abd3da163ee2a343`. This milestone adds reusable hosting features; it does not add the news application or a dependency on jev.

- Schedules target an existing session, agent and job, with numeric inputs and an interval in minutes. The first tick is due after one interval. The host ticks automatically; an external scheduler may tick the same schedule through the API. Paused schedules do not start work. After downtime, missed intervals coalesce to the current slot. An active previous run causes that slot to be skipped. Starts and skips have durable receipts in the target conversation, making simultaneous ticks and a crash between admission and catalog acknowledgement safe. Editing an interval starts a new scheduling revision and clock. Deleting a schedule revokes credentials restricted to it; archiving its session removes its schedules.
- Trigger credentials are random bearer secrets stored only as SHA-256 hashes in the device catalog. They expire, can be revoked, and may be restricted to one schedule. They authorize only the tick endpoint, cannot bypass due/paused/overlap checks, and cannot read, chat, create schedules or issue credentials. The existing device token remains the administrative credential. Secrets are returned only when issued.
- Execution history reads authoritative persisted task, entry and job-record documents from an open session. It includes historical and live chat generation tasks and jobs, paged newest first, with task/entry details, descendant subagents, errors, timing and recorded usage. It survives restarts and is independent of the 30-card transcript window. Archived sessions retain their files but are not queried by this UI. No secondary telemetry projection can lose an acknowledged commit. Record scans are consistent session transactions; the associated run document is read through the snapshot API, outside the mutation callback.
- Every job has a full run record in a conversation document family keyed by task ID. Task-scoped documents retire on terminal settlement, so they cannot hold historical audits. Completion audits require all declared phases and optional named checks. Handlers record checks through `JobContext.CheckAsync`; a missing or failed required check makes completion fail. Failures, declines and aborts retain an incomplete audit. Old runs without audits display as unaudited, not as audited success.
- The web UI adds Schedules and Executions pages, accessible from navigation. Schedule management, token issue/revoke, history filters, pagination and execution details use the authenticated hub proxy. Tokens never appear in GET responses. The APIs are optional advertised capabilities, preserving existing v1 routes and clients.

Validation covers migration from catalog versions 1–3, tick duplication and recovery, pause/resume, overlap, failures, credential scope/expiry/revocation, execution history after restart and beyond 30 jobs, failed audits, and browser flows.

## Using the milestone

Start the device with the existing `pisharp durable serve` command and open PiSharp Durable Web. **Schedules** creates, pauses, resumes, edits and deletes recurring jobs and issues/revokes automation tokens. **Executions** filters runs by session, kind, status or text; it shows phase steps, audits, descendant tasks and recorded usage. Refresh updates these pages; they do not hold a live stream open.

The new routes are additions to v1:

| Route | Device token | Trigger token |
|---|---|---|
| `GET /v1/schedules` | List | Forbidden |
| `POST /v1/schedules` | Create `{sessionId, agentId: 1, jobId, everyMinutes, input?: {}}` | Forbidden |
| `PATCH /v1/schedules/{id}` | Change `{enabled?, everyMinutes?}` | Forbidden |
| `DELETE /v1/schedules/{id}` | Delete | Forbidden |
| `POST /v1/schedules/{id}/tick` | Tick | Tick only the authorized schedule(s) |
| `GET /v1/trigger-credentials` | Metadata only | Forbidden |
| `POST /v1/trigger-credentials` | Issue `{label, scheduleId?, expiresInHours?: 720}`; returns `{credential, token}` once | Forbidden |
| `DELETE /v1/trigger-credentials/{id}` | Revoke | Forbidden |
| `GET /v1/sessions/{s}/executions` | History with `kind`, `status`, `q`, `limit` and opaque JSON `cursor` | Forbidden |
| `GET /v1/sessions/{s}/executions/{taskId}` | Execution detail | Forbidden |

An external scheduler can call `POST /v1/schedules/<id>/tick` with `Authorization: Bearer <trigger-token>` and an empty JSON object. The endpoint returns `started`, `skipped`, `paused` or `not_due`; it never overrides the schedule. Requests using a recognized trigger token receive 403 on any other route; expired, revoked or invalid credentials receive 401. `DurableDeviceClient` has scheduling, tick and execution-history convenience methods. Embedding hosts can set `DurableDeviceOptions.RunSchedules = false` to supply ticks themselves.

The interval is 1–525600 minutes, and token lifetime is 1–8760 hours. Admission failures are retried on later polls without acknowledging the slot; failed jobs settle that slot and do not block future runs. Receipts live in the target session database, and immutable task input identifies the schedule across interval revisions, including recovery before catalog acknowledgement.

For a custom job, set `JobDefinition.RequiredChecks = ["report-saved"]` and have a handler call `await context.CheckAsync("report-saved", matches, "Read the persisted report back.")`. Checks and phase progress commit with the run record. Workspace snapshot and Release check require a saved-result check. Audit status is `passed`, `failed`, or `incomplete`; the expected, done and missing requirements remain visible in history.
