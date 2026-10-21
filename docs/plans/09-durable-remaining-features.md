# PiSharp Durable: remaining feature plan

Status: implemented on `feature/durable-network`, not released. The contracts are in [decision 0010](../decisions/0010-durable-application-contracts.md) and the results of the qualification run in [release/durable-m6-qualification.md](../release/durable-m6-qualification.md). The rest of this document is the plan as proposed on 2026-10-20; where the decision differs, the decision is what was built.

## Baseline and scope

Build on `feature/durable-network`, including the uncommitted recurring-job milestone described in [decision 0009](../decisions/0009-recurring-jobs-and-execution-history.md). That milestone supplies persisted interval schedules, scoped trigger credentials, execution history, and completion audits. It has 219 passing hosting/server/web/browser/CLI cases from the preceding implementation pass. Preserve those changes and establish a reviewed baseline before beginning this roadmap.

The reference application is [pi-durable-news-agent at 273b79b](https://github.com/disler/pi-durable-news-agent/tree/273b79bb8d3d8907cae9fbd6abd3da163ee2a343). Borrow reusable application/hosting capabilities. PiSharp already follows pi-durable 1.2.0; the reference application's 1.1.0 dependency is not an engine upgrade target. Jev, Firecrawl, news-specific scores, and a particular remote hosting vendor are optional adapters or sample policy choices.

| Remaining item | Deliverable | Milestone |
| --- | --- | --- |
| Reusable judgment policies | Immutable versioned policies, normalized scoring, decision cache, evidence, confidence routing and approval | M2 |
| Structured result cards | Validated artifacts, allowlisted Razor components, execution links, safe actions and portable text | M2 |
| Source monitoring/retrieval | RSS/Atom, GitHub releases, bounded page extraction, caching, hashes, health, fallbacks, request/credit limits | M3 |
| Supervised research | Persisted angles, bounded parallel children, independent model/thinking, incremental results, synthesis and cancellation | M4 |
| Feedback/preferences | Explicit feedback events, reversible preferences, bounded few-shot context and versioned ranking | M5 |
| Client capability parity | Registry, generated/checkable route coverage, browser/CLI/TUI controls and older-device behavior | Contract starts M2; completed M6 |
| Reference application | Dependency/release watcher using the generic features end to end | M5 |
| Operational completion | Doctor, remote attach, service templates, backup/restore, retention and release evidence | M6 |

Cron/time-zone scheduling, distributed multi-writer clustering, arbitrary model-generated HTML, provider training, automatic publishing, and a broad plugin marketplace are outside this roadmap. Interval scheduling remains the current contract. Add these later only through separate requirements.

## Architectural rules

1. Keep the durable engine portable. Put application coordination in `PiSharp.Durable.Hosting`, HTTP in Server, transport DTOs/client calls in Client, rendering/proxying in Web, and terminal commands in Cli. Hosting must not depend on ASP.NET or Cli; Client remains BCL-only. Extract a new application library only if actual dependencies justify it.
2. Reuse `IDurableModels.ClassifyAsync`, native classifier adapters, task checkpoints, memo APIs, ownership, approval waits, and completion audits. Do not introduce a second scheduler or duplicate the classifier stack.
3. Retained decisions/artifacts belong in conversation document families or an explicitly owned application store. Task-owned documents retire at settlement. Device App State is capped at depth 24 and 100,000 serialized characters; use it for small pointers/settings, not corpora or ledgers.
4. Session commits and the catalog are separate transaction domains. Use stable operation IDs, a session-side receipt, idempotent catalog acknowledgement, and startup reconciliation. Never assume a cross-database transaction. Durable checkpoints do not guarantee exactly-once remote charges or mutations.
5. Never call snapshot/session APIs inside a session mutation callback. Commit subscribers may enqueue invalidations only; they must not block or reenter the session. Any future searchable projection needs a durable replay watermark, idempotent upserts and rebuild support. Authoritative records stay usable if projection is unavailable.
6. Freeze a run's definition/policy/input/source hashes, model configuration, preference revision and budget at admission. Explicit resume uses that snapshot. A changed policy/version starts a new evaluation; it never silently changes a waiting run.
7. Feature additions advertise capabilities on v1 and preserve old routes/JSON fields. Trigger credentials keep their existing tick-only permission. New read or feedback scopes, if added, are separately defined; never widen a previously issued credential.
8. Tests use fake models, fake clocks, fixture HTTP and failure injection by default. Paid providers and live credentials are optional qualification runs, recorded separately. Retrieved documents and model outputs are untrusted data.

## Milestone sequence and gates

### M2 — shared execution contracts, judgment and artifacts

**Outcome:** a job can evaluate an item under a versioned policy, wait for a person when required, and retain a validated result with evidence and costs.

**A. Contract and storage foundation (F1–F3)**

- Add versioned object inputs while retaining existing `JobInput` numeric definitions and existing schedules. Use an additive `inputSchema`/`inputSchemaVersion` contract. Freeze the effective normalized input in the run record. Start with a bounded schema subset: object, string, finite number, boolean, enum, bounded arrays, required fields and nested objects; reject unknown fields by default. Do not claim full JSON Schema support. Validation returns field paths; passwords/tokens are secret references, never job inputs. Keep existing request body limits as an upper bound and set explicit per-definition limits.
- Store immutable, size-limited result and decision envelopes in conversation families keyed by run/operation ID. Put large fetched bodies in a catalog-owned document/blob store under the device data root, addressed by content hash. Persist metadata/indexes in SQLite; immutable blob writes use a temporary file and atomic rename. Reconcile orphans after a grace period; never GC a referenced blob. Keep source URLs, retrieval times, hashes, parser versions and allowed content type with every evidence reference.
- Decide schema migrations only after discovering the actual next catalog version. The current catalog is v4; use the next unused version and exercise upgrades from v1–v4. Back up before migration; no in-place downgrade guarantee. Do not edit a published migration to add later features.
- Introduce a reservation/usage ledger before parallel or paid work. One logical operation has a stable ID, run ID, provider, meter, requested maximum, status, timestamp and receipt. Reserve atomically across per-run and UTC-day device limits before dispatch; count active reservations against caps. Actual usage settles the reservation once; known unused quota is released; uncertain remote outcomes remain reserved until provider reconciliation or explicit resolution.
- Count requests, retrieval credits, model tokens and monetary units separately. Use integer tokens/credits and integer microcurrency plus a currency code; never add incompatible currencies or provider credits. Unknown prices display as unknown, not zero. Require a conservative bound before accepting work under a hard money cap; enforce token/output limits where supported. A local cap cannot undo a provider's already accepted call.
- Add retention/export/GC contracts and capability entries now so each subsequent feature registers its routes and clients. All durable writes and HTTP idempotency keys must detect same-key/different-payload conflicts.

**B. Judgment policies (J1–J3)**

- Store immutable policy versions and a separately revisioned active-version pointer. A policy defines input schema, provider/model or classifier reference, prompt/template, output schema, labels, score ranges, weights, thresholds, confidence routing, and required evidence. Validate unique labels, finite values, legal ranges, weight normalization and threshold ordering at publication.
- Prefer the existing classifier operation for categorical policies. Use a bounded structured generation adapter for policies needing explanations/evidence, with explicit schema validation. Keep both behind one provider-neutral judgment service; optional Jev import must map into the same contract and remain a later adapter.
- Default confidence routing: `auto >= 0.90`, `confirm >= 0.50`, otherwise `human`; values are configurable per immutable policy. Confidence is the provider's reported value, not a calibrated guarantee. Missing/invalid confidence routes to human; invalid output fails validation and cannot auto-approve. If the provider has no confidence semantics, require a policy-specific supported path rather than inventing a score.
- Persist raw normalized result, scores, rationale, evidence IDs, model and policy versions, cache status, usage and decision state. Cache key includes canonical input hash, evidence/content hashes, policy version, prompt/schema version, model/settings and preference revision if used. Scope caches to the device/authorization boundary. Read receipts on replay before making another remote call. Cross-run TTL defaults to 24 hours and is configurable; durable replay receipts outlive cache TTL. Policy changes and source changes miss the cache.
- `confirm` and `human` open durable approval interactions with evidence and the proposed result. Human choice is a separate append-only decision with actor/time/reason; it does not overwrite the model judgment. Timeout defaults to no automatic approval. Resumption, rejection, cancellation and late decisions are idempotent and audited.
- Expose policy list/detail/publication/activation, evaluation and decision lookup. Initial management uses administrative device auth. Integrate required decision/check IDs into completion audits. Policies cannot authorize external writes by themselves.

**C. Structured artifacts (A1–A2)**

- Start with `finding`, `comparison`, `evidence-list`, `timeline`, and `action-request`. Envelope: ID, schema version, kind, title, run/task IDs, created time, revision, bounded typed props and evidence IDs. Persist an immutable version and a receipt before announcing availability.
- Server validates kind, required fields, array/text limits, finite scores, evidence ownership, supported schema version, and action identifiers. Reject invalid output with field-level repair guidance and a bounded repair count (default one retry); keep the validation failure in execution history. Required-artifact audit fails if no valid artifact is saved.
- Razor renders a fixed partial per kind. HTML-encode text, allow only safe HTTP(S) evidence links, and treat model markdown through the existing safe rendering boundary or plain text. No scripts, event-handler props, arbitrary HTML or executable commands. An action references a server-owned allowlisted operation with validated input, authorization, confirmation and idempotency checks; rendering never executes it.
- Show artifacts on the job card and execution detail with evidence, policy/approval provenance, accessible text fallback and download of redacted structured JSON. Unknown versions get a bounded encoded fallback. Preserve current 30-card transcript limit; full artifacts are fetched separately with paging.

**M2 acceptance:** old numeric jobs/schedules pass unchanged; invalid schemas/policies cannot publish; concurrent duplicate judgments retain one local logical receipt; human gates survive restart; invalid/hostile cards never execute content; capped reservations reject excess parallel work; unknown billing stays visible; artifacts/decisions survive terminal-task retirement and history-window eviction. Add API/client fixtures and browser desktop/phone coverage.

### M3 — source monitoring and resilient retrieval

**Outcome:** a scheduled source workflow checks changes cheaply, retains evidence and continues through recoverable failures without exceeding its budget.

- Define `SourceDefinition` with ID, kind, URL/repository, enabled state, revision, parsing options and optional credential reference. Add CRUD, optimistic revisions, status/health and a manual check operation. A source is polled by a normal scheduled job, using existing schedule credentials; no competing background scheduler.
- Implement RSS/Atom and GitHub releases first, then bounded HTTP page extraction. Use conditional GET with ETag/Last-Modified; normalize stable item IDs and canonical URLs without dropping identity-bearing query parameters. Document source-specific normalization. Hash normalized content and parser version, not timestamps alone. Unchanged sources skip repeated judging and publication; an explicit new policy version may request reevaluation of cached content.
- Store fetch receipts, source/item versions, raw bounded content, normalized documents and links to consuming runs. Default maximum response is 2 MiB, extracted text 256 KiB, 5 redirects and 30 seconds per fetch; make these host-configurable hard ceilings. Decompression also counts toward the byte limit. Treat duplicate/late/out-of-order feed entries deterministically; retain tombstones or source IDs long enough to prevent accidental republishing after GC.
- HTTP fetch policy validates every redirect and resolved target, prevents loopback/link-local/private-network and cloud metadata access by default, and defends against DNS rebinding through the chosen transport. Private intranet sources require an explicit host allowlist. Never forward authorization to a different origin. Disable external XML entities/DTD resolution; do not execute JavaScript. Source text cannot change system/tool instructions. Bound prompt inclusion and cite evidence IDs.
- Add `IRetrievalProvider` adapters for direct HTTP and optional search/extraction services. Fallbacks operate only on eligible failure classes and consume the same reservations. Cache entries vary by URL, content hash, parser and authorization identity; never share authenticated content with anonymous requests. Optional providers are disabled until configured and cannot silently enable paid fallback.
- Persist circuit health per provider/origin: honor Retry-After, exponential cooldown starting 2 minutes and capped at 30 minutes with jitter; no immediate retry for 403/429. Bound attempts (default three per logical operation across fallback chain) and use durable sleeps for delayed retries. Do not permanently disable a source after a few transient errors; show degraded state and allow manual retry/disable.
- The web Sources page shows freshness, changes, last failure, next eligible retry, budget and retained evidence. Manual checks return an accepted execution reference. CLI gets equivalent list/check/enable/disable commands through the registry; terminal detail can use plain text first.

**M3 acceptance:** fixture feeds include namespaces, malformed XML, missing IDs/dates, duplicates, deletions and pagination; fixtures prove conditional 304 behavior, unchanged-content suppression, auth-cache isolation, redirects, oversized/decompression responses, Retry-After and cooldown across restart. Burst tests prove parallel reservations stay within both run/day caps. Crash after fetch but before acknowledgement reuses a saved response where available; unknown external charges do not disappear.

### M4 — supervised research jobs

**Outcome:** a person or schedule can start bounded multi-angle research, see partial results, cancel it, and recover after restart.

- Add a `research` job using the typed input contract: question, 1–5 explicit angles (or a bounded planning phase that produces them), source references, research model/thinking, synthesis model/thinking, concurrency and budget. Default concurrency 2, host ceiling 5 children; cap attempts, context and output. Validate supported model/thinking combinations before admission. Snapshot all settings.
- Persist a research plan with stable angle IDs and one child identity per angle before launching children. Extend/reuse `JobContext` through an explicit bounded fan-out API; do not call its current sequential single-child `AskAgentAsync` concurrently. Preserve existing jobs' replay keys and behavior. Each child has its own read/retrieval tool allowlist, operation IDs, deadline and budget share, enforced under the same parent cap.
- Use scheduler/task ownership and checkpointed durable waits. A process restart rejoins existing children; it does not invent new angle IDs or retry completed work. Failed attempts get explicit attempt identities and a bounded retry policy. Parent cancellation stops queued/running children and waits for settlement; shutdown suspends recoverable work and is distinct from user cancellation.
- Publish each valid partial answer/evidence artifact as it completes. Maintain states `planned`, `running`, `completed`, `failed`, `cancelled` per angle plus attempt metadata; parent completion remains the normal job status. Default synthesis requires at least one successful angle and labels missing/failed angles. Require an all-angles policy when the caller needs completeness. No successful angles means failure.
- Synthesis reads a frozen, stable-angle-ordered set of accepted answers/evidence and records its input hash. It cannot mutate child results. Failed synthesis resumes/retries without rerunning completed research. Optional judgment gates apply before publication; required evidence/answer/synthesis checks appear in the completion audit.
- Research UI provides start form, progress per angle, partial evidence, recorded costs, approval and cancel/resume links. Reuse execution detail/artifact rendering and event streams. Event reconnect reconciles from authoritative state rather than replaying UI actions. Initial incremental rendering may poll using bounded intervals; it must continue to work when streams fail.

**M4 acceptance:** deterministic fake children demonstrate bounded concurrency, budgets and distinct configurations; terminate/restart at plan admission, child creation, partial publication and synthesis commit. Assert one logical result per angle and no lost completed answer. Test cancellation during queueing/tool call/approval, one/all child failures, deadline expiry, invalid planning output, out-of-order completion and reconnect. Clearly record uncertain provider calls; do not claim exactly-once provider billing.

### M5 — feedback, preferences and a complete reference watcher

**Outcome:** explicit user choices improve ranking and a release watcher demonstrates the whole workflow.

- Feedback is append-only and request-idempotent: artifact/item ID, action (`more`, `less`, `label`, `dismiss`, `undo`), optional bounded labels/note, actor, time and request ID. The target and actor are server resolved; model content cannot impersonate a person. Authenticated UI/CLI actions only; trigger tokens remain forbidden.
- Start with an explicit **device-local named profile**, default `default`; do not describe this as multi-user personalization. A profile has a revision, bounded topic/label weights and selected example references. More/less nudges default to +0.10/-0.15, clamped to [-1,1]; a label contributes +0.10. Make these policy settings rather than universal relevance claims. Deduplicate repeated feedback and bound recent examples (default 20). Undo appends a reversal and rebuilds deterministically. Reset/export/delete are first-class.
- Ranking consumes a frozen profile revision and exposes why a weight affected a score. Few-shot examples are selected explicitly, sanitized, size-limited and treated as examples, never instructions. No automatic training, implicit LocalBrain memory writes, or cross-device sharing. Source-content relevance and policy confidence stay separate.
- Add feedback controls to structured artifacts, with clear saved/retry/revert states and parity CLI commands. Expose profile revision and preferences on a small Settings page. Apply changes to future runs; reranking an existing result is an explicit new operation with its own receipt.
- Build `samples/durable/ReleaseWatcher` (proposed path): configured repositories/feeds and a named judgment policy; collect changed releases, dedupe, evaluate relevance, optionally research risky changes, publish comparison/finding artifacts, accept feedback and retain audit evidence. A read-only dependency manifest input may be supported with explicit file access; no automatic dependency edits, messages, publishing or installs. Include offline recorded fixtures and sample configuration with secret references only.
- Reference demo: create a scheduled watcher, issue its scoped trigger token, run a changed fixture release, route a medium-confidence decision to approval, inspect evidence/costs, give feedback, restart and prove the next unchanged tick emits no duplicate artifact. Document each public service/API used so the sample does not become a privileged code path.

**M5 acceptance:** replay/restart/undo produce identical profile state; isolation tests prevent wrong-profile/target writes; stale revisions conflict; deletion removes selected examples and derived preference state according to retention rules; hostile notes do not alter tool authority. A full offline watcher scenario passes through schedules, sources, judgment, research, artifacts, feedback and completion audits.

### M6 — client parity, operations and release qualification

**A. Capability registry and clients (C1–C3)**

- Extend the existing string capability list with a checked-in manifest recording feature ID/version, API methods/routes, required scopes, client method/CLI command/TUI control/browser page, response fixtures and explicit unsupported-client reason. Keep the existing string list for old clients; expose detailed metadata additively. CI verifies actual registered endpoints and supported clients against the manifest; declarations alone do not establish coverage.
- Add BCL-only Client convenience APIs and DTOs where stable; preserve raw JSON escape hatches. Commands provide text and `--json`, stable exit codes, opaque cursors, cancellation, idempotency and redacted diagnostics. Add commands for existing schedules, credentials and executions as well as new policies, sources, research, artifacts, feedback and budgets. Secrets appear only on explicit issue/export flows, never ordinary list/detail output.
- Implement `pisharp durable attach` through `DurableDeviceClient` and a remote presentation adapter, not a local harness reading remote files. Cover initial snapshot, event reconnect/dedup, submissions, approvals, task cancellation, model/settings, history and artifacts. Preserve a failed pending write's request ID across retry; never blindly resubmit on reconnect. Terminal should show meaningful text fallbacks for structured cards and clearly disable unsupported features.
- Test older devices missing each advertised capability. Return an actionable unsupported-feature result; do not show controls that will always fail. Scope and permission failures must remain distinguishable from offline/retryable errors.

**B. Doctor and host operation (O1–O2)**

- `pisharp durable doctor [--json]` checks supported runtime, data directory permissions/locking, available disk, catalog/session schema compatibility, pending migration/recovery, provider configuration **presence** (no secrets), listener/base URL, device auth, active reservations, orphan receipts, source health, schedules and service configuration. Default is read-only; network probes and repair are explicit flags/commands. Repair produces a concrete preview, backup and idempotent operation receipt. Probe output is redacted and bounded.
- Provide Windows Service, systemd and launchd templates for the same `durable serve` process with working/data directories, restricted service identity, restart/backoff, graceful shutdown, log rotation and secret-file permissions. Keep installation explicit; generating a template does not install a service. Windows paths/spaces and Unix ownership have dedicated fixtures. No dependency on Node for native operation.
- Default listener stays loopback. Document trusted reverse proxy/TLS and SSH-tunnel configurations for remote access, correct forwarded-header handling, origins and token custody. Do not expose an admin endpoint publicly as a demo convenience. Attach accepts a stored credential reference or protected prompt, not a bearer token in a command-line URL.
- Add supported backup/export/restore with SQLite-aware snapshots or a verified quiesced copy; catalog and session/blob manifests must describe a consistent recovery point. Verify a restore in a clean data root. Retention defaults: execution/decision/artifact metadata 90 days, source bodies 30 days, feedback until explicit deletion; retain active runs, pending approvals, unresolved reservations, referenced evidence and replay receipts. Offer dry-run size estimates; purge in bounded batches. Explain that purging evidence can make an old result no longer reproducible.
- Improve history without replacing its authority: execution detail links source/decision/artifact/budget records; add explicit user-turn grouping as an additive view over `pi.generation` records and a read-only archive reader if required for the retention workflow. Keep current open-session history operational. Add indexed projections only after a measured scan bottleneck, with backfill/rebuild and correctness tests.

**C. Qualification (Q1)**

- Run hosting, server, client/CLI, web and browser suites after each vertical milestone. At release, run full durable engine/SQLite/tools suites and affected classifier suites; broaden to whole-repo compatibility only when shared contracts change. Existing tests and compatibility fixtures must not be weakened to fit the new implementation.
- Add crash/restart chaos fixtures around every remote side effect and every session/catalog acknowledgement boundary; use load fixtures for history paging, source volume, bounded fan-out and budget contention. Record latency/storage benchmarks before selecting projection/index complexity.
- Produce a release matrix for native Windows/Linux/macOS, offline/no-Node runtime, migration/backup restore, browser desktop/phone, old-client/new-device/new-client/old-device, and optional TypeScript peer interop. Test against pinned reference artifacts where a compatibility claim is made; label PiSharp-only features separately.
- Optional real-model smoke runs validate classifier, structured output, child model/thinking and reported usage for configured providers. Record provider/model/date, evidence and skips. Missing credentials do not block offline development, but untested live providers cannot be advertised as verified.
- Keep Hosting/Server/Client/Web experimental and unpacked until the owner chooses a release. Publishing packages/deployment is a separate explicit action after qualification.

**M6 acceptance:** manifest coverage catches a deliberately removed route/client binding; remote attach survives disconnect/restart without duplicate input; doctor is safe on damaged/old stores; restore reproduces a watcher run and evidence; service start/stop/recovery works on target OSes; release evidence lists actual passes, failures and skips.

## Delivery order, ownership and estimates

Use [AI_DOCS/workflow-plan.md](../../AI_DOCS/workflow-plan.md) for dependency-marked work packages and [contracts.md](../../AI_DOCS/contracts.md) for current versus proposed boundaries. A future executor must resolve the blocking contract-discovery package before editing shared APIs. Parallelism is safe only after those contracts are frozen: for example judgment and artifact rendering, or CLI and browser consumers of the same accepted API. Schema migrations and shared route registration need one integrator at a time.

| Milestone | Prerequisite | Planning effort, engineer-days | Review checkpoint |
| --- | --- | --- | --- |
| M2 | Existing milestone baseline; F1 contract freeze | 12–18 | First audited policy-to-card vertical slice |
| M3 | F2 storage, F3 budgets and M2 evidence contracts | 8–12 | Changed-source fixture with no duplicate publication |
| M4 | M2 plus M3 retrieval and budgets | 10–15 | Crash-recoverable parallel research demonstration |
| M5 | Judgment, artifacts, sources, research | 6–10 | End-to-end release watcher with reversible feedback |
| M6 | Capability contract begins M2; final gate after M5 | 12–18 | Remote/service/restore and release evidence |

Total planning range: **48–73 engineer-days** for one experienced engineer, not a delivery promise or autonomous-agent runtime estimate. Includes tests/docs/integration, excludes external provider onboarding, hosting procurement and release approval. Re-estimate after F1 and each checkpoint; parallel work can reduce elapsed time but does not remove integration effort. Operations diagnostics/templates can begin after F1; final qualification waits for all features.

## Decisions and follow-ups

Use the defaults above to avoid blocking implementation on routine preferences. F1 must confirm source-fetch transport enforcement, durable child cancellation behavior, and available classifier confidence semantics before freezing signatures. If these require engine changes, split and review those changes independently. Add a decision record per accepted cross-layer contract rather than silently treating this proposal as already shipped API.

Before implementation: review the existing uncommitted milestone, establish its baseline, freeze F1 contracts, then begin F2/F3. No code changes, deployment, service installation, paid calls or commits are authorized by this planning request alone.
