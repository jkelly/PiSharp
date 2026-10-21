# Durable application contracts (F1)

Status: accepted for implementation by the owner's request of 2026-10-20 ("implement the plan"), on `feature/durable-network`. This record
freezes the contracts the roadmap in [plans/09](../plans/09-durable-remaining-features.md) left to F1. It supersedes the proposals in
`AI_DOCS/contracts.md` wherever they differ. Everything here is PiSharp-only and experimental (`PISHARP_DURABLE001`); nothing is packed or
published.

## B0 baseline

Starting revision: `cd65d88` plus the uncommitted decision 0009 milestone, preserved unchanged in the working tree. Before any new code the
Hosting (72), Server (16) and Web (4) suites were rebuilt with the local SDK 10.0.400 MSBuild and passed (`artifacts/b0-*.log`).

## Discovery results

- **Cancellation.** `HostedSession.AbortTaskAsync` aborts a task after its owned work: owned tasks and the work of conversations the task
  owns. Research children are conversations owned by the research job task, so the existing `POST /v1/sessions/{s}/tasks/{id}/abort` is
  the research cancellation protocol; no new route. Host shutdown closes the Harness and cancels invocations without settling tasks; they
  resume when the session reopens. User cancellation settles the job `aborted`.
- **Waiting inside a phase.** A phase may await durable work it created (a submission, an owned task) as `AskAgentAsync` already does; a
  rerun of the phase must rediscover that work by stable identity. Bounded fan-out uses this, with `Task.WhenAny` over the running branch
  waits, instead of `NextTaskState.Waiting`, whose join is all-settled only.
- **Classifier confidence.** `choice` and `score` answers carry the provider's `confidence`; `bool` answers carry only `probability`.
  A policy maps only these reported values. A bool question may route on confidence only when the policy says
  `"boolConfidence": "margin"` (confidence = max(p, 1 − p)); otherwise it routes to a person. Structured generation uses the model's
  self-reported `confidence`. Neither is a calibrated guarantee.
- **Fetch transport.** `SocketsHttpHandler.ConnectCallback` resolves the name itself, rejects non-public addresses (loopback, private,
  link-local including cloud metadata, CGNAT, multicast, unspecified, IPv6 unique-local/site-local, IPv4-mapped forms of those) unless the
  host is explicitly allowed, and connects to the address it checked, which closes DNS rebinding between check and use. Redirects are
  followed manually so every hop is checked; authorization never crosses origins. Decompression is manual so expanded bytes count against
  the limit. XML parsing prohibits DTDs and has no resolver.

## Storage domains and authority

| Record | Store | Key | Authority / rebuild |
| --- | --- | --- | --- |
| Job run record, artifact list, judgment links | Session `pisharp.job-record` (existing) | task ID | Authoritative; retained after settlement |
| Judgment record | Session family `pisharp.judgment` | judgment ID | Authoritative decision, provenance, human decisions (append-only) |
| Artifact envelope | Session family `pisharp.artifact` | artifact ID | Authoritative, immutable revision 1 |
| Evidence reference | Session family `pisharp.evidence` | evidence ID | Authoritative pointer: URL, times, hashes, parser, content type, excerpt, blob hash |
| Execution start receipt | Session family `pisharp.operation` | `Idempotency-Key` | Same commit as the task it starts |
| Operation receipts (catalog mutations) | Catalog `operations` | key | Same transaction as the mutation |
| Policies, versions, active pointer | Catalog `policies`, `policy_versions` | ID / (ID, version) | Authoritative; versions immutable |
| Judgment cache | Catalog `judgment_cache` | cache key | Disposable; never authoritative |
| Reservations and usage | Catalog `budget_reservations`, `budget_limits` | operation ID | Authoritative ledger |
| Blobs | `blobs/sha256/xx/<hash>` + catalog `blobs`, `blob_refs` | content hash | Content-addressed, immutable; refs drive GC |
| Sources, items, fetch receipts, health | Catalog v6 tables | source ID / receipt operation ID | Authoritative; items keep tombstones |
| Feedback events, preference profiles | Catalog v7 tables | event ID / profile ID | Events authoritative; profile is a deterministic projection |

A session commit and a catalog transaction never share a transaction. Every cross-domain step has a stable operation ID: the catalog side
is written first and is idempotent by that ID; the session side records the ID; a replay rereads the catalog receipt before any remote
call. A crash between a remote call and its receipt leaves the reservation `uncertain` (still counted) and the next attempt gets a new
attempt identity. One local logical result is guaranteed; exactly-once remote charges are not.

Catalog migrations: **v5** (M2) operations, blobs, budgets, policies, judgment cache; **v6** (M3) sources; **v7** (M5) feedback. Each is
additive, applied in one transaction after the existing v1–v4 steps, and backed up first (`catalog.v<old>.bak.sqlite` beside the catalog). A newer catalog is
refused; there is no downgrade.

## Wire conventions (additive to v1)

- New routes are administrative (device token). A recognized trigger token gets 403 on all of them. Existing credentials never gain
  permissions.
- Creation and execution mutations take `Idempotency-Key` (1–200 characters). Same key and same canonical body returns the first result
  with `Idempotent-Replay: true`; a different body is 409 `conflict`. Updates and activations take `expectedRevision`; stale is 409.
- New error codes: `limited` (429, a local budget or admission limit) and `too_large` (413). Validation errors may add
  `error.fields: [{path, message}]`. Old clients ignore both.
- Lists: `limit` 1–100 (default 20), opaque string `cursor`, `{items, next}`.
- Accepted executions return 202 `{sessionId, taskId, executionUrl}`; created resources 201.
- Times are UTC Unix milliseconds. Money is integer micro-units with a currency code. Unknown cost is `null` with `costKnown: false`.
- `GET /v1/device` keeps `capabilities` (strings) and adds `features: [{id, version}]`; `GET /v1/capabilities` returns the manifest.

## Typed job inputs

`JobDefinition.InputSchema` (JSON, optional) and `InputSchemaVersion` (default 1) are additive; definitions without a schema keep the
numeric `Inputs` and their clamping. The supported schema subset is: `object` (`properties`, `required`, `additionalProperties: false`
only), `string` (`maxLength` required, ≤ 100 000; `minLength`, `enum`, `pattern`, `format: "uri" | "secret-ref"`), `number`/`integer`
(finite; `minimum`, `maximum`), `boolean`, `array` (`items`, `maxItems` required, ≤ 1000; `minItems`), `default` on any property, nesting
depth ≤ 8. Unknown keywords make the definition invalid when the host opens. Unknown input fields are rejected. Errors carry JSON Pointer
paths. `secret-ref` strings are `secret:<name>` references; values are never inputs. `MaxInputBytes` defaults to 64 KiB. The run
record keeps the normalized input, `inputSchemaVersion` and `inputHash` (SHA-256 of canonical JSON: ordinal key order, Chord number
formatting).

## Budgets

Meters: `requests`, `tokens`, `credits:<provider>`, `money:<CURRENCY>` (micro-units). Requests and tokens add across providers; credits
and currencies never mix. Device limits are per UTC day; run limits are frozen into the run record at admission. `Reserve` checks, in one
catalog transaction, the run and the day: active (`reserved`, `uncertain`) reservations count their requested amounts, settled ones their
actual amounts. Exceeding either rejects with `limited`. A money limit requires a money amount in the request; an unknown price under a
money limit is rejected. States: `reserved` → `settled` | `released` | `uncertain`; `uncertain` → `settled` | `released` only by explicit
reconciliation. Settle is idempotent and conflicts on different values. `GET /v1/budgets` and `PATCH /v1/budgets` (revisioned limits),
`GET /v1/sessions/{s}/executions/{t}/usage`, `POST /v1/budgets/reservations/{id}/resolve`.

## Judgment

Policy spec (immutable per version; SHA-256 of its canonical JSON is the policy hash): `title`, `description`, `kind`
(`classifier` | `structured`), `model {provider, id}`, `instructions` (≤ 8000), `inputSchema`, `labels [{id, description}]` (1–20, unique,
`^[a-z0-9][a-z0-9_-]{0,39}$`), `scores [{id, instructions, levels[2..10], weight > 0}]` (≤ 8, weights normalized at publication),
`thresholds {auto: 0.90, confirm: 0.50}` (0 ≤ confirm ≤ auto ≤ 1), `boolConfidence` (`none` | `margin`, unused by choice policies),
`requiredEvidence` (0–20), `maxOutputTokens` (≤ 4000), `cacheTtlSeconds` (0–604800, default 86400), `reviewTimeoutHours` (null or 1–720),
`preferenceProfile` (null or a profile ID), `flags [{id, instructions, true, false}]` (≤ 8 yes/no questions; they route on confidence only
with `boolConfidence: "margin"`). Unknown fields are rejected with field errors. Policy management: `GET /v1/policies` (`{items, next}`),
`GET /v1/policies/{id}` (summary, versions, active spec), `GET /v1/policies/{id}/versions/{v}`, `POST /v1/policies/{id}/versions` (the body
is the spec; publishing a spec identical to the latest version returns it; a policy's first version becomes active at revision 1), `PUT
/v1/policies/{id}/active {version, expectedRevision}`.

Evaluation is the application job `pisharp-judge` (phases `evaluate`, conditional approval `review`, `record`) or `JobContext.JudgeAsync`
inside any job. `POST /v1/sessions/{s}/judgments {agentId, policyId, version?, input, evidenceIds, budget?}` returns 202. The service
order is: receipt → input and evidence validation → cache → reservation → `calling` receipt → provider → settle → result receipt and cache.
Routing: `auto` when confidence ≥ auto, `confirm` when ≥ confirm, otherwise `human`; missing, non-finite or out-of-range confidence routes
`human`; invalid output fails and never auto-approves. Cache key: policy ID, version and hash, input hash, sorted evidence content hashes,
model and settings, preference revision; TTL per policy; durable receipts outlive it. `confirm` and `human` open a durable approval with the
proposed result; a person's choice is appended as `{choice, actor, at, reason}` and never overwrites the model's result; repeating the
same choice is a no-op, the opposite one is 409, and a review timeout resolves `expired`, never approved.
`POST /v1/sessions/{s}/judgments {agentId?, policyId, version?, title?, input, evidenceIds?, budget?}` answers 202 with `judgmentId` too
(the record exists once the evaluate phase starts); `GET /v1/sessions/{s}/judgments` (`{items, next}`, newest first), `GET
/v1/sessions/{s}/judgments/{id}`, `POST /v1/sessions/{s}/judgments/{id}/decision {choice: approve|reject, reason?}`. The decision's actor is
the credential (`device-admin`), never a body field. A policy version is frozen into the run record at admission, so activating another
version never changes a run already started.

## Artifacts

Kinds (schema version 1): `finding`, `comparison`, `evidence-list`, `timeline`, `action-request`. Envelope: `id`, `schemaVersion`, `kind`,
`title` (≤ 200), `runTaskId`, `conversationId`, `createdAt`, `revision` (1), `props`, `evidenceIds` (≤ 100, owned by the session),
`provenance` (policy, version, judgment, decision), `operationId`. Props are bounded per kind (`ArtifactCatalog`). Text renders encoded;
links only HTTP(S); markdown only through the existing sanitizer. Actions reference host-registered operations (`reevaluate`,
`start-research`, plus host additions) with an input schema; invocation needs `confirm: true` and `Idempotency-Key` and starts an
execution; rendering never runs anything. A saved artifact records the check `artifact:<kind>`, so `RequiredChecks` can demand one. Invalid
output gets field errors, one repair attempt by default, and a failed step in the run record. Routes: `GET
/v1/sessions/{s}/executions/{t}/artifacts`, `GET /v1/sessions/{s}/artifacts` (`{items, next}`), `GET /v1/sessions/{s}/artifacts/{id}`, `GET
.../{id}/export` (`?format=text` for the plain-text rendering), `POST .../{id}/actions/{action}`. Evidence: `GET /v1/sessions/{s}/evidence`,
`POST /v1/sessions/{s}/evidence {title, url?, text, contentType?}` (the same URL and text is one record), `GET .../evidence/{id}`, `GET
.../evidence/{id}/text`. The hub renders cards server-side (Razor partials per kind, `GET /ui/{device}/{session}/executions/{t}/artifacts`);
unknown kinds or schema versions fall back to bounded JSON.

## Sources and retrieval

A source spec is `{id, kind: feed | github-releases | page, url (feed, page) | repository (github-releases, owner/name), title (default
the ID), enabled (default true), maxItems 1–500 (default 100), maxPages 1–20 (default 1, at most the device limit), includePrereleases,
credentialRef?, allowFallback (default false)}`. Unknown fields are field errors; `credentialRef` must name a credential configured on the
device (`RetrievalOptions.Credentials`), whose value never leaves the host. Create is revision 1 and takes `Idempotency-Key`; `PATCH`
merges (a `null` removes a field; `id` and `kind` are fixed) and `DELETE ?expectedRevision=` tombstones the definition; both need the
current revision. Recreating a deleted ID continues its revision count and keeps its items. A source's `state` is `{status: new | ok |
degraded, lastCheckedAt, lastChangedAt, lastSuccessAt, lastError, lastErrorAt, failures, nextEligibleAt, lastRunKey, items}`.

Retrieval goes through `IRetrievalProvider` (`direct` is built in; fallbacks are host-configured, ordered, and used only for a source with
`allowFallback` and no credential). The direct provider resolves DNS itself and connects only to public addresses: loopback, private,
link-local, CGNAT, documentation, benchmark, multicast, unique-local and IPv4-embedding IPv6 forms (mapped, compatible, NAT64, 6to4,
Teredo) are refused unless the host is in `AllowedPrivateHosts`, and cloud metadata addresses are refused always. Redirects are followed by
hand and each hop is checked; an `Authorization` header goes only to the original origin. Limits (hard host ceilings): 2 MiB response, counted
again after gzip/deflate/Brotli decoding, 256 KiB extracted text, 5 redirects, 30 s, 3 attempts per logical fetch across the chain, 5
pages. Conditional GET uses the cached ETag/Last-Modified of the first page, keyed by provider, URL and credential identity, so an
authenticated response is never reused without the credential. Feeds parse Atom, RSS 2.0 and RSS 1.0 with DTDs prohibited; pages keep
the title and visible text (script, style, template and similar elements dropped); GitHub releases skip drafts, and prereleases unless
included. Canonical URLs lower-case scheme and host and drop default ports, user info, fragments and `utm_*`, `fbclid`, `gclid`, `mc_cid`,
`mc_eid` and `igshid` parameters. Items are keyed by feed ID (`id:`), else canonical link (`url:`), else a hash of title and date (`hash:`);
the content hash covers parser and version, title, URL and normalized text. Changed items are new keys or new hashes; unchanged items are not
judged or published again; items missing from a complete fetch get `absentAt` and stay, so a reappearance is recognized.

Health is kept per provider and origin: a transient failure (network, timeout, 403, 429, 5xx) starts a cooldown of 2 min doubling to 30 min
with deterministic ±10 % jitter, or the response's `Retry-After` (capped at 24 h) when that is longer; `Retry-After` survives restarts and
`force` skips only the cooldown, never `Retry-After`. Every call writes a fetch receipt (`fetch:{run}:{source}:p{page}:a{attempt}`) before
the provider runs; a restart replays receipts in attempt order, re-calls a free provider under the same operation, and marks an interrupted
paid call's reservation `uncertain` rather than calling again. Each source's result is committed once per run
(`source-check:{run}:{source}`), with its items and state, in one catalog transaction.

The application job `pisharp-source-check {sourceIds?, policyId?, version?, reevaluate?, force?, maxJudgments?, budget?}` freezes the
source specs (all enabled sources when none are named) and the policy version at admission. Phases: `fetch` (repeats until no source is
waiting on a cooldown), `judge` (when a policy is given: the changed items, or the current ones with `reevaluate`, projected onto the
policy's input schema), `record` (an `evidence-list` artifact with provenance `{sources, policyId, policyVersion}`, check `sources`). A run
fails only when no source succeeded and one failed hard. Routes: `GET/POST /v1/sources`, `GET/PATCH/DELETE /v1/sources/{id}` (the detail
adds `origin`, that origin's `health` and recent `receipts`), `GET /v1/sources/{id}/items` (`{items, next}`, most recently changed first),
`GET /v1/retrieval/health`, `POST /v1/sessions/{s}/source-checks` (202). The hub's `/sources` page lists sources with their freshness,
failures and next retry, edits them, starts checks and shows retrieval health.

## Research

The application job `pisharp-research` takes `{question (≤ 4000), angles?[1..5] {id?, title, prompt?}, sourceIds? (≤ 20), evidenceIds?
(≤ 50), research {model?, thinking?}, synthesis {model?, thinking?}, concurrency 1–5 (default 2), requireAll (default false), maxAttempts 1–3
(default 2), angleDeadlineSeconds 30–3600 (default 600), retrieval {maxFetches 0–10 (default 3), origins? (≤ 20)}, policyId?, version?,
budget?}` (128 KiB at most). Admission refuses duplicate angle IDs, unknown sources, a model the device does not have, a thinking level other
than `off` on a model that does not reason, an unknown policy, a version without a policy, and an origin that is not `http(s)://host[:port]`
with no path, each as a field error. It freezes each stage's model and thinking level (the device default when
none is given), the sources' revisions and the policy version.

`JobContext.FanOutAsync(branches, {concurrency ≤ 5, maxAttempts ≤ 3, deadline ≤ 1 h}, completed?)` is the public fan-out: up to 10
branches, each a child conversation owned by the job task with its own model, thinking level, instructions and tools from the read-only
allowlist (`read`, `search_history`), plus `fetch_page` for a branch given a retrieval allowance (below). The children and their rows in the run record's `branches` are written in one commit before any
branch starts, so a restart finds the same children. A row is `{key, id, phase, iteration, title, conversationId, model, thinking, status:
planned | running | completed | failed | cancelled, attempts[], answerEntryId, answerChars, error, published, startedAt, endedAt}`; an
attempt is `{attempt, requestId: job:<task>:<phase>:<iteration>:branch:<id>:<n>, status: running | completed | failed | expired | limited |
cancelled, startedAt, deadlineAt, mark, endedAt, error, resumed, usage}`. Each attempt reserves budget under the run's limits (one request,
the prompt and the model's largest answer, and its price when the catalog has one); a refused reservation fails the branch as `limited`. The
attempt submits to the child under its request ID and races a durable sleep to its deadline; at the deadline the child is aborted and the
attempt `expired`. When it ends, the reservation settles at what the child recorded after its mark (released when it made no request; cost
unknown when the attempt was resumed). A restart rejoins a running attempt rather than starting another; a completed branch keeps its
answer. `completed` runs once per completed branch, including after a restart, and its fields (an artifact ID) are kept on the row.
Aborting the run cascades to the children; their rows and running attempts become `cancelled` and the open reservations `uncertain`, since
an interrupted call may have been billed. Shutdown leaves them running for the restart.

A branch's `retrieval {urls, origins, maxFetches 1–10 (default 3), maxChars 500–20 000 (default 8000)}` is its allowance for `fetch_page
{url}`: the exact URLs it may fetch, compared in canonical form (at most 500), origins any page of which it may fetch (at most 20, so the
model chooses the path there: name only origins the run may send requests to), how many pages, and how much of a page's text one fetch
returns. The `branch-retrieval` extension that provides the tool is installed on every session but selected only for such a branch: the
child's extensions and the allowance document (`{allowance: {runKey, runTaskId, budget, urls, origins, maxFetches, maxChars}, fetches}` on
the child, which a fork does not copy) are written in the same commit as the child, so any other conversation is refused. A fetch is
admitted and counted in one commit, keyed by its tool call; a URL outside the allowance, a fetch past the count and an origin cooling down
are refused with a message the model can act on. Admitted fetches are direct HTTP under the device's address policy, with no credential, no
fallback provider and the source checks' size and redirect limits; each has an operation ID (`retrieve:{run}:c{child}:t{call}`), a receipt
(`@fetch_page`) and a reservation of one request against the run's limits, and failures feed the origin's health. The page is kept as
session evidence (parser `page`), and the tool returns its evidence ID, URL and title, the fetches left, and its text bounded to `maxChars`,
marked as untrusted data. A rerun of the same call after a restart reuses the recorded page; one interrupted before its receipt finished
calls again under the same operation. When an attempt ends or the run is aborted, a fetch still in flight becomes `uncertain` and its
reservation settles at the one request.

Phases: `plan` (the given angles, or a planner child's JSON array with one repair before the run fails; the material, numbered `[n]` source
items and `[eN]` evidence of at most 12 000 characters quoted as untrusted data, is kept in the checkpoint so a restart asks the same
questions; the material's item and evidence URLs are kept with it), `research` (one branch per angle; unless `retrieval.maxFetches` is 0
or there is nothing to fetch, an angle may fetch `maxFetches` pages, 6000 characters each, from the material's URLs and the run's
`origins`, and the run record's `research.fetchesPerAngle` says how many; each answer is saved at once as a `finding` keyed `angle:<id>` with
provenance `{angleId}` that cites the run's evidence and the pages its angle fetched, so there is one per angle whatever the order or restarts; check `research:answers` needs one answer, or all with `requireAll`), `synthesize` (a
separate phase, so a retry does not research again: the accepted answers in plan order, hashed with `CanonicalJson.Hash` and told which
angles were not answered; check `research:synthesis`), `gate` (with a policy: the synthesis as `{title, summary}` projected onto the policy's
input schema, judged at the frozen version), `review` (an approval when the route is `confirm` or `human`), `publish` (the synthesis as a
`finding` with provenance `{synthesis hash, judgmentId, policyId, policyVersion, decision}`; result `{artifactId, angles, missing,
synthesisHash}`). Required checks: `research:answers`, `research:synthesis`, `artifact:finding`. Route: `POST /v1/sessions/{s}/research`
(202, `Idempotency-Key`). The hub's `/research` page starts runs (with how many pages each angle may fetch), follows each angle's status and
attempts, shows the findings kept so far and the run's spend, and offers cancel and the review decision.

## Feedback and preferences

Feedback is an append-only catalog event `{id, profileId, requestId, target {sessionId, artifactId}, title, action: more | less | label |
dismiss | undo, labels [≤ 10, a-z 0-9 _ - . : up to 40 characters], note ≤ 1000, example, actor, at, reverses, revision}`. The route
resolves the target from the session's own artifact, and the actor is the authenticated credential (`device-admin`), never a body field.
The request ID (the body's `requestId` or the `Idempotency-Key`) makes it idempotent per profile: the same request returns the first event
with `Idempotent-Replay: true`, a different one conflicts. A `more` or `less` without labels is about the artifact's judged label (its
provenance judgment's result) and its `props.tags`. An undo names the event it reverses (`reverses`), or reverses the newest effective one
on the same artifact; reversing an undone event, an event on another artifact or one from before a reset conflicts, and an event of another
profile is not found. A `dismiss` cannot be an example; an undo carries no labels, note or example.

A profile is device-local, `default` unless named (`^[a-z0-9][a-z0-9_-]{0,39}$`), at most 10 000 events. Its revision counts every event.
The projection is a fold of the events after the last `reset`, identical on replay: each artifact keeps its latest effective `more`,
`less` or `dismiss` (so repeating a vote counts once) and the union of its `label` events; undone events drop out. Weights: the vote adds
`settings.more` (0.10) or subtracts `settings.less` (0.15) for each of the vote's labels, each label adds `settings.label` (0.10), and every
weight is clamped to [−1, 1]. Examples are the artifacts whose latest feedback chose one, newest first, at most `settings.maxExamples`
(20), with notes cut to 300 characters. `PATCH` changes settings (an event) and forgets examples (a `forget` event per example, which also
removes the chosen event's note); `reset` starts weights and examples over and keeps the history; `export` returns the profile and every
event; `DELETE` removes the profile and all its events. Every change takes `expectedRevision` and conflicts when it is stale.

A judgment policy with `preferenceProfile` freezes `{profileId, revision, weights, examples}` when the judgment is created (its
`preferenceRevision` is `profile@revision`, part of the cache key). The examples reach the classifier or the structured prompt only as
`state.examples` data, with a note that they are examples and not instructions. The result gains `preference: {profileId, revision, base,
adjustment, rank, reasons [{label, weight}]}`: the base is the policy's weighted score (0.5 without scores), the adjustment adds the weights of
the judged label and the item's own labels, and the rank is clamped to [0, 1]. Confidence and the route never change. Feedback applies to
future judgments; reranking a result is a new judgment, with its own receipt, at the new revision.

Routes: `POST /v1/sessions/{s}/artifacts/{id}/feedback` (201, `{event, profile}`), `GET /v1/sessions/{s}/artifacts/{id}/feedback?profileId=`
(`{profileId, revision, target, vote, labels, example, undo}`, the event an undo would reverse), `GET /v1/preferences` (`{items, next}`),
`GET/PATCH /v1/preferences/{id}`, `POST /v1/preferences/{id}/reset`, `GET /v1/preferences/{id}/export` (an attachment), `DELETE
/v1/preferences/{id}?expectedRevision=`. Artifact cards in the hub offer more, less, dismiss, label (with optional labels), an example box
and undo; a failed write keeps its request ID so Retry cannot record it twice, and the card shows the saved or reverted state. The
`/preferences` page shows a profile's revision, weights and examples, edits its settings, forgets examples, and resets, exports or deletes
it.

## Reference release watcher

`samples/Durable.ReleaseWatcher` (the plan proposed `samples/durable/ReleaseWatcher`; the folder follows the other samples) is a client of
the public routes only, through `DurableDeviceClient`; its README lists every method and route it uses. Setup finds or makes the sources,
the policy (published under the hash of its spec and activated), the session (an Idempotency-Key from its name) and the
`pisharp-source-check` schedule, and can issue a trigger credential restricted to that schedule. A pass reads the completed checks'
evidence lists, starts `pisharp-judge` with each release's evidence ID as the Idempotency-Key, and starts `pisharp-research` for an
approved judgment whose label is configured for it, keyed by the judgment ID. Content-addressed evidence makes an unchanged release replay
its judgment and a changed one start another. The offline demo runs the reference scenario against a hosted device and a recorded feed on
loopback with a manual clock and a faux classifier; `release-watcher.offline-scenario` in the Server tests checks its report: the
trigger-credential ticks, a changed release judged again, a 0.70 judgment routed to review and approved, known usage, passed completion
audits, feedback weights after an undo and a replayed retry, a repeated setup, and no new artifact or judgment after a restart and an
unchanged tick.

## Capability manifest and clients

`contracts/durable-capabilities.json` lists every feature with its version, routes, scopes, client methods, CLI commands, browser pages and
explicit unsupported reasons. `application-api.manifest-matches-routes-client-and-device-features` in the Server tests compares it with the mapped
endpoints and the client's public methods, and requires every binding to be present or to say why not;
`remote.manifest-cli-bindings-match-the-command-table` in the DurableCli tests compares the `cli` bindings with the command table both ways,
and checks that removing a command or a manifest binding is caught; `hub.manifest-web-bindings-match-the-hub-pages` in the Web tests
requires each `web` page to be served with the automation view it names and every automation page the hub links to be bound (`/schedules`
belongs to decision 0009 and is exempt), and checks that a dropped page, an unknown page and a feature with neither pages nor a reason are
caught. Clients check `features` before showing a control and report an
unsupported feature distinctly from authorization and offline errors. Retention is CLI and client only: the manifest says the hub does
not offer it.

## CLI parity (M6)

`pisharp durable <group> <command>` (`src/PiSharp.Cli/Durable/DurableRemote.cs`, `DurableCommands.cs`) drives a device's v1 API through
`DurableDeviceClient`, as the hub does; no remote command reads the device's files. Without `--device` a command uses the local device's
`device.json` and token file; with `--device <url>` the token comes from `PISHARP_DEVICE_TOKEN` or `--token-file`, and a token in an
argument or in the URL is refused. Every command takes `--json` (the device's JSON instead of text). Exit codes are stable
(`DurableExit`): 0 ok, 1 failed, 2 usage, 3 not found, 4 conflict, 5 unauthorized, 6 unsupported, 7 offline (retryable), 8 invalid,
9 limited, 130 cancelled. A write takes `--request <id>` as its Idempotency-Key or request ID; without one the command makes one and
prints it only when the device did not confirm the write, so repeating the command with it cannot act twice. A command of an application
feature checks `features` first and exits 6 with what the device lacks, which an offline device (7) never produces. Output never contains
the device token or a trigger credential's secret, except in the two flows that exist to show one: `pisharp durable token` and issuing a
trigger credential.

## Attach (M6)

`pisharp durable attach [--session] [--agent]` (`DurableAttach.cs`) follows an agent's stream with `FollowAsync` and reduces it with the
client's `AgentModel`. The first snapshot prints the last 20 items and says how many came before; each item, tool call and pending card
prints once by its key, so a reconnect (`[reconnected]`) or a device restart replays nothing. A line is a message (a follow-up when the
agent is busy); `/steer`, `/abort`, `/cancel`, `/model`, `/thinking`, `/history`, `/pending`, `/status` and `/artifacts` (when the device
offers artifacts) mirror the composer. Cards have text fallbacks: question forms list numbered options and `/answer` takes numbers or
free text per question; plan reviews and job approvals take `/plan <task> approve|reject [feedback]` and `/approve`/`/deny`. A send the
device did not confirm keeps its text and request ID; `/retry` sends it again with the same ID, and nothing is resent on its own. On a
device that does not advertise a feature, its command prints "<device> does not offer <feature>." and stays attached; an unreachable
device prints `offline: ...` instead.

## Doctor (M6)

`DeviceDoctor.Check` (`src/PiSharp.Durable.Hosting/Storage/DeviceDoctor.cs`) reads a data directory without writing to it: SQLite files
open read-only, as `immutable` when no WAL or shared-memory file exists, so a check leaves no `-wal`/`-shm` behind; a test compares the
directory byte for byte before and after. Checks: the directory, the device lock (running or not), free disk, the catalog version
(missing, older, newer than this build) and `quick_check`, each session store, schedules, trigger credentials, application records (skipped
on catalogs older than version 5), uncertain or stuck budget reservations, blobs (missing, size, and with `--verify-blobs` hash; orphan
files), retention, failing sources and leftover migration copies. A damaged, old or newer store is reported, never fatal. The CLI adds
the listener (`warn` beyond loopback, pointing at [durable-remote-access.md](../durable-remote-access.md)), the token (`fail` when weak,
`warn` when others can read it; never shown) and, with `--probe`, a call to the running device. `--repair` needs the device stopped:
it migrates the catalog (keeping its backup copy), collects unreferenced blobs and makes the token owner-only.

## Service definitions (M6)

`pisharp durable service systemd|launchd|windows` prints, or writes with `--out`, a definition that runs `durable serve --data-dir` as
the user, with the install, stop and log commands. It installs nothing, refuses to overwrite and holds no token. `serve` links SIGTERM
(and SIGHUP off Windows) to its cancellation, so `systemctl stop` and `launchctl bootout` stop the device as Ctrl+C does. **Deviation:**
Windows gets a per-user scheduled task at logon with restart on failure, not a Windows service: the console binary does not implement
the service control protocol, and the device needs the user's profile and model credentials. `Stop-ScheduledTask` ends the process; the
next start resumes as after a crash. Loopback stays the default; reverse proxy with TLS and SSH tunnels are documented in
[durable-remote-access.md](../durable-remote-access.md).

## Backup and restore (M6)

`DeviceBackup` (`src/PiSharp.Durable.Hosting/Storage/DeviceBackup.cs`) and `pisharp durable backup create|verify|restore`. **Deviation:**
backups are offline only: `create` takes the device lock, so a running device gets exit 4. Each `.sqlite` file is copied with SQLite's
backup API and left in rollback-journal mode; other files are copied; lock files, WAL and journal files, `blobs/tmp`, temporary files and
migration copies are skipped. `pisharp-backup.json`, written last, records the format and version, the time, the device's ID, the catalog
version, the sessions, whether secrets are included, and every file's size and SHA-256. The token is left out unless `--include-secrets`.
`verify` checks the manifest, paths, sizes, hashes, SQLite integrity and unlisted files (exit 8 when damaged); `restore` needs an empty
target, verifies first and restores the token owner-only. `release-watcher.a-restored-backup-replays-the-run-and-its-evidence` restores the
reference scenario into a clean root and runs the watcher there: nothing is judged again, every judgment replays, and the evidence texts
equal the original's.

## Retention (M6)

`DeviceRetention` (`src/PiSharp.Durable.Hosting/Storage/Retention.cs`), `GET /v1/retention` (dry run), `POST /v1/retention {batch?,
dryRun?}`, `RetentionAsync`/`PurgeRetentionAsync`, and `pisharp durable retention show|purge [--batch <n>] [--all]`. Defaults: 90 days for
metadata (settled or released budget reservations, judgment cache entries), 30 days for source bodies (fetch receipts and their blob
references, source item bodies, the retrieval cache), and a one-day grace before unreferenced blobs are collected. A purge removes at most
`batch` records per category (500 by default, 5000 at most) and reports `more`; `--all` repeats until nothing is left. The report's
`alwaysKept` lists what is never purged: reserved or uncertain reservations, fetch receipts of runs that have not ended, blobs that any
record references, operation, App State and execution-start receipts, feedback events (until deleted) and preference profiles, the
definitions (policies, sources, schedules, trigger credentials), and session records. **Deviation:** retention covers the catalog domain
only. Session records (judgments, artifacts, evidence, executions, transcripts, job runs) are not purged, because the engine has no
document deletion; archiving the session (`DELETE /v1/sessions/{s}`) is the way to set them aside.

## Qualification (M6)

[release/durable-m6-qualification.md](../release/durable-m6-qualification.md) records the suites run on this branch with their actual
passes, failures and skips, and what was not run.

## Not in scope

Cron and time-zone schedules, multi-writer clustering, model-generated HTML, provider training, automatic publishing and a plugin
marketplace stay out, as the roadmap says.
