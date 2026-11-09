> Active 2026-11-08 implementation: see [context](gap-implementation-context.md), [execution plan](gap-execution-plan.md) and [audit contracts](upstream-audit-2026-11-08/contracts.md). Historical material below is preserved.

# Durable application contracts

Planning snapshot, 2026-10-20. **Existing** means present in the worktree, including the uncommitted milestone. **Proposed** means a design for review, not an implemented/public API. F1 freezes exact signatures, DTO definitions, route placement and migration ownership before downstream implementation. Discovery tasks cannot skip unknown cancellation, confidence or fetch-transport semantics.

## Existing boundaries

| Caller path | API/service function path | Endpoint / call | Method and parameters/defaults | Result | Auth/errors |
| --- | --- | --- | --- | --- | --- |
| Web `Views/Automation/Schedules.cshtml` / `wwwroot/js/automation.js` through hub proxy | Client `AutomationClient.cs`; Server `DurableDeviceApi.Automation.cs` | `/v1/schedules` | GET; POST `{sessionId,agentId:1,jobId,everyMinutes,input?:{}}` | Array; created schedule with ID/revision/timing | Admin; invalid 400, absent session/job 404 |
| Same | Same | `/v1/schedules/{id}`; `/{id}/tick` | PATCH `{enabled?,everyMinutes?}`; DELETE; POST tick | Updated schedule; `{ok:true}`; tick status/task/slot | Admin management; admin or schedule-bound trigger on tick only |
| Same | Server `DurableDeviceApi.Automation.cs`; raw Client `SendAsync` | `/v1/trigger-credentials`; `/{id}` | GET metadata; POST `{label,scheduleId?,expiresInHours:720}`; DELETE | Issue returns `{credential,token}` once; no-store | Admin only; 401 invalid/expired/revoked, 403 recognized trigger on other routes |
| Web `Views/Automation/Executions.cshtml` | Client `ExecutionsAsync` / `ExecutionAsync` | `/v1/sessions/{s}/executions`; `/{taskId}` | GET `kind?,status?,q?,limit=20,cursor?`; limit 1–100, q max 200 | Authoritative page with opaque continuation; execution detail | Admin only; invalid 400/not found 404 |
| Job handler | Hosting `Apps/Jobs.cs` / catalog AppState | `JobContext.SaveStateAsync(path,value)` | JSON Pointer and `JsonValue`; phase/iteration/path receipt | Saved value, including replayed original | Local host; small AppState limits apply |
| Job handler | Hosting `Apps/Jobs.cs` | `JobContext.CheckAsync(id,passed,detail?)` | ID max 100, detail max 4000 | Persisted run audit check | Required missing/failed check prevents completion |
| Durable tool/host | Durable `Harness/Models.cs`; `Models/NativeDurableModels.cs` | `IDurableModels.ClassifyAsync` | Model/context JSON and cancellation | Classifier-result JSON, including error results | Host provider resolution; must inspect result status, not only thrown exceptions |

Existing namespaces/signatures (use `PiSharp.Durable.Chord.JsonObjectValue` / `JsonValue` for host JSON):

```csharp
// PiSharp.Durable.Hosting; JobSchedules.cs
IReadOnlyList<JobSchedule> DurableDeviceHost.Schedules();
Task<JobSchedule> DurableDeviceHost.CreateScheduleAsync(string sessionId, long agentId,
    string jobId, int everyMinutes, JsonObjectValue? input = null,
    CancellationToken cancellationToken = default);
// ExecutionHistory.cs
Task<JsonObjectValue> HostedSession.ExecutionsAsync(string? kind = null,
    string? status = null, string? query = null, int limit = 20,
    JsonObjectValue? cursor = null, CancellationToken cancellationToken = default);
Task<JsonObjectValue> HostedSession.ExecutionAsync(long taskId,
    CancellationToken cancellationToken = default);
// Apps/Jobs.cs
Task<string> JobContext.AskAgentAsync(string question);
Task CheckAsync(string id, bool passed, string? detail = null); // JobContext member
// PiSharp.Durable.IDurableModels in Harness/Models.cs
Task<JsonObjectValue> ClassifyAsync(JsonObjectValue model, JsonObjectValue context,
    CancellationToken cancellationToken);
```

Classifier DTOs already exist in `PiSharp.Contracts.ModelOperations` (`ModelOperationContracts.cs`): choice/score/bool questions and answers, `ClassifierContext`, `ClassifierResult`, usage and stop reason. Model registry implementations live in `PiSharp.AI.ModelOperations`; `NativeDurableModels` bridges the wire forms. Bool probability is not automatically the same quantity as a provider's choice/score confidence. F1 defines the supported mapping per policy/provider.

## Proposed common wire conventions

- All routes below are additive v1 capabilities, administrative-auth initially. Keep the original capability string list and add detailed registry metadata. Trigger-only tokens receive 403 for every new route. A future feedback/read credential requires a separately reviewed scope; existing tokens never gain permissions.
- Create/execute/feedback mutations require `Idempotency-Key` (bounded stable operation ID); same key + same canonical payload returns the original result, different payload returns 409 conflict. Update/activation require `expectedRevision`; stale values return 409. Durable approval decisions are idempotent and reject conflicting late decisions.
- Lists: `limit=20`, range 1–100; opaque cursor; `{items:[],next:null|cursor}`. Items are bounded summaries. Detail fetches do not inline full source corpora. Resource IDs are opaque strings; existing numeric task IDs remain compatible with current JS-safe bounds.
- Accepted operations return HTTP 202 with `{sessionId,taskId,executionUrl}`. Completed resource creation returns 201 with its envelope. Continue the existing `{error:{code,message}}` convention verified in F1; add structured field errors additively if supported. Use 400 validation, 401 auth, 403 scope, 404 absent, 409 stale/idempotency conflict, 413 oversized, 429 local admission limit and 503 temporary provider/device unavailable. Background failures appear in execution records, not as invented successful HTTP responses.
- Envelope times use UTC Unix milliseconds consistent with existing host records; versions/revisions are positive integers. Redact authorization headers, credential values, sensitive inputs and private documents from errors/logs. Distinguish reported usage, reserved estimate and unknown/uncertain cost.

## Proposed UI / API map

The proposed partial files `DurableDeviceApi.{Policies,Artifacts,Sources,Research,Feedback,Budgets}.cs` live in Server; matching `*Client.cs` partials live in Client. Proposed Razor pages live under `Web/Views/Automation/`; server-rendered card partials under `Views/Artifacts/`. Add page routes through the existing controllers and use the authenticated hub proxy. No browser direct device-token access.

| Caller / UI route | Client function (proposed) | Device endpoint / method | Parameters / payload / defaults | Response shape | Errors / constraints |
| --- | --- | --- | --- | --- | --- |
| `/policies` | `PoliciesAsync`, `PolicyAsync` | GET `/v1/policies`, `/v1/policies/{id}/versions/{v}` | page; ID/version | Page summaries; immutable `PolicyVersion` | Unsupported version/absent policy 404 |
| `/policies` editor | `PublishPolicyAsync`, `ActivatePolicyAsync` | POST `/v1/policies/{id}/versions`; PUT `/v1/policies/{id}/active` | policy spec; `{version,expectedRevision}` | Version; active pointer/revision | Schema/weights/threshold validation; stale activation 409 |
| Job / policy preview | `EvaluatePolicyAsync` | POST `/v1/sessions/{s}/judgments` | `{agentId:1,policyId,version,input,evidenceIds:[],budget}` | Accepted execution | Provider unsupported/invalid input rejected before admission |
| Execution detail | `JudgmentAsync`, `DecideJudgmentAsync` | GET `/v1/sessions/{s}/judgments/{id}`; POST `/{id}/decision` | `{choice:approve|reject,reason?}` | `JudgmentRecord`; durable human decision | Admin; task approval gate; conflicting decision 409 |
| Execution/card detail | `ArtifactsAsync`, `ArtifactAsync` | GET `/v1/sessions/{s}/executions/{t}/artifacts`; GET `/v1/sessions/{s}/artifacts/{id}` | page; artifact ID | Metadata page; versioned `ArtifactEnvelope` | Validate evidence ownership; encoded unknown-version fallback |
| Allowlisted card action | `InvokeArtifactActionAsync` | POST `/v1/sessions/{s}/artifacts/{id}/actions/{actionId}` | validated action input/confirmation | Accepted execution or completed receipt | Server-owned operation, auth, confirmation and idempotency; never arbitrary commands |
| `/sources` | `SourcesAsync`, `SaveSourceAsync`, `DeleteSourceAsync` | GET/POST `/v1/sources`; PATCH/DELETE `/{id}` | source spec; patch has expectedRevision | Page/source revision; deletion receipt | Secret reference only; deny unsafe destinations |
| `/sources` manual check | `CheckSourceAsync` | POST `/v1/sessions/{s}/source-checks` | `{sourceIds,agentId:1,budget}` | Accepted execution | Bounded IDs, budget reserved before requests |
| `/research` and execution detail | `StartResearchAsync` | POST `/v1/sessions/{s}/research` | `{agentId:1,question,angles?,sourceIds:[],models,concurrency:2,budget}` | Accepted execution | 1–5 angles; host concurrency ceiling; unsupported settings 400 |
| Research cancel | Existing task cancellation client/route after F1 verification | Reuse existing authenticated task stop route | session/task ID | Settled/accepted cancellation | Do not introduce a conflicting cancellation protocol |
| Artifact feedback controls | `RecordFeedbackAsync` | POST `/v1/sessions/{s}/artifacts/{id}/feedback` | `{profileId:default,action,labels?:[],note?,undoEventId?}` | `FeedbackEvent`, resulting profile revision | Actor supplied by host identity, never trusted input |
| `/preferences` | `PreferenceAsync`, `UpdatePreferenceAsync` | GET/PATCH `/v1/preferences/{id}`; POST `/{id}/reset` | revisioned changes; reset has expectedRevision | `PreferenceSnapshot` | Bounded weights/examples; replay and undo semantics |
| `/budgets` / execution detail | `BudgetAsync`, `ReservationsAsync` | GET `/v1/budgets`; GET `/v1/sessions/{s}/executions/{t}/usage` | UTC period/run; page | Limits, reservations, settled usage and uncertain outcomes | Admin; no secrets; do not conflate provider credits/currencies |
| Feature discovery / all clients | `DeviceAsync` plus registry metadata accessor | Existing GET `/v1/device`, additive metadata or linked manifest | none | Old strings plus feature versions/coverage | Older devices supported without metadata |

Policy/artifact internal authoring is through the host service and registered job/tool adapters. Do not expose an unrestricted artifact-write HTTP route simply for agent convenience. Exact reset/export/delete routes, audit-event actor identity in single-user device mode, and manifest location are F1 outputs.

## Proposed C# service boundaries

All new host types below use namespace `PiSharp.Durable.Hosting`, except Client helpers in `PiSharp.Durable.Client`. Names are proposals. DTOs are immutable records, input collections bounded; `CancellationToken` defaults to `default` at public methods. Optional text/object values use explicit nullable types. Avoid adding a required member to existing interfaces without a compatibility/default strategy.

| Proposed type / file | Signature sketch | DTOs and implementation responsibility |
| --- | --- | --- |
| `JobDefinition` / existing `Apps/Jobs.cs` | additive `InputSchema?`, `InputSchemaVersion?`; preserve `Inputs` | Bounded schema subset; normalized input snapshot in existing run record |
| `JudgmentService` / `Judgment/JudgmentService.cs` | `Task<JudgmentRecord> EvaluateAsync(JudgmentRequest request, CancellationToken cancellationToken = default)` | Request contains stable operation ID, policy version, input, evidence, run scope and budget; service uses existing classifiers or structured-generation adapter |
| `PolicyStore` / `Judgment/PolicyStore.cs` | `Task<PolicyVersion> PublishAsync(string id, PolicySpec spec, string operationId, CancellationToken cancellationToken = default)` | Immutable policy version plus separately revisioned active pointer; store/host integration decided F1 |
| `ArtifactStore` / `Artifacts/ArtifactStore.cs` | `Task<ArtifactEnvelope> SaveAsync(ArtifactDraft draft, string operationId, CancellationToken cancellationToken = default)` | Validate kind/schema/evidence, record conversation receipt and retained envelope; no raw HTML |
| `IBudgetLedger` / `Budgets/IBudgetLedger.cs` | `Task<BudgetReservation> ReserveAsync(BudgetRequest request, CancellationToken cancellationToken = default)`; `Task<UsageReceipt> SettleAsync(string operationId, MeteredUsage usage, CancellationToken cancellationToken = default)` | SQLite transaction checks run/day caps; reserve/settle/uncertain/reconcile state machine; terminal result is idempotent |
| `IRetrievalProvider` / `Sources/IRetrievalProvider.cs` | `Task<RetrievalResult> FetchAsync(RetrievalRequest request, CancellationToken cancellationToken = default)` | Request includes validated source, conditional headers, byte/time bounds, credential reference and reserved operation ID; response status/document/evidence/cache/usage; policy-aware transport enforces redirects/DNS |
| `ResearchCoordinator` / `Research/ResearchCoordinator.cs` | `Task<ResearchHandle> StartAsync(ResearchRequest request, CancellationToken cancellationToken = default)` | Stable parent/angle/attempt IDs, child handles, frozen model/thinking/budgets, bounded durable join; exact runtime cancellation API is discovery gate |
| `FeedbackStore` / `Feedback/FeedbackStore.cs` | `Task<FeedbackResult> AppendAsync(FeedbackDraft draft, string operationId, CancellationToken cancellationToken = default)` | Server-bound actor/target; append event + profile revision atomic in chosen store; reversible derived snapshot |
| Client partials / `*Client.cs` | e.g. `Task<JsonNode?> StartResearchAsync(string session, JsonObject request, string operationId, CancellationToken cancellationToken = default)` | Uses escaped paths and existing SendAsync idempotency; stable DTO helpers may wrap raw JSON; no Hosting reference |

## Data ownership and calls

| Caller | Proposed service/repository | Query / key and parameters | Result / invariants |
| --- | --- | --- | --- |
| Policy UI, job judgment phase | PolicyStore / catalog-owned `policy_versions`, active pointer | `(policyId,version)`; activation expected revision | Immutable spec/hash; atomic activation; no editing existing versions |
| Job judgment/artifact writers | Conversation document families | `(conversationId,operationId)` / `(runTaskId,artifactId)` | Authoritative decision/artifact receipt retained after task settlement; bounded record |
| Source coordinator | Catalog-owned source/document store | source revision; canonical identity; `(hash,parserVersion,authScope)` | Metadata and immutable blob reference; conditional headers and provenance |
| Retrieval/judgment/research dispatch | BudgetLedger / catalog-owned reservation tables | operation ID; run + UTC device-day budget/meter | Atomic admission across both caps; settlement/uncertainty receipts |
| Feedback UI/CLI | FeedbackStore / catalog-owned profile/event tables | profile ID, event ID, target reference, expected revision where applicable | Append-only event and deterministic profile projection; explicit undo/reset/delete |
| Execution API | Existing authoritative history plus bounded lookups | session/task ID, page limit/cursor | Linked artifact/decision/evidence/usage summaries; full content fetched separately |
| GC/backup/doctor | Shared manifest/reference enumerator | dry-run by default; cutoff, active refs, operation ID | Never remove active/pinned/unresolved/replay-required data; recoverable bounded operations |

No exact SQL/schema layout is frozen here. F1 supplies migrations, indexes, referential deletion/archive behavior, store locking and cross-store reconciliation state machines. Archive/delete must not strand live reservations, feedback targets, evidence or schedule references. A decision explaining which record is authoritative and how to rebuild each projection is required before F2 writes data.

## Agent samples 33–62 contracts (2026-11-01)

Each `samples/agents/NN-Name` project uses namespace `PiSharp.Samples.Name`. Implement `public static WorkflowSpec Workflow.Create(SampleContext c)` and `public static Task<int> SelfTests.RunAsync(string fixture)`; Program/csproj are scaffolded. WorkflowSpec is (Id, Title, Instructions, IReadOnlyList<ToolSpec>, IReadOnlyList<PlannedCall>). ToolSpec is (Name, Description, JsonObject Parameters, Func<JsonObject,CancellationToken,Task<JsonNode?>>, bool RequiresApproval=false). Use SampleJson.Schema(propertiesJson, requiredNames) with exact IDs and explicit argument types; host rejects extra fields and wrong types. Context.Input is supplied JSON, Context.State is per-run state, Context.Approved is host approval, and ResolvePath/WriteJsonAsync constrain artifact paths to the workspace. Native provider selection and credentials remain in Shared, unchanged.

Each workflow needs meaningful domain inspection, computed decisions and persisted results, normally 3 tools with concrete demo calls. Use deterministic fixture dates/cents and actual algorithms/local artifact operations, not canned responses. Live fixture.task must state one concrete, successful objective and all requested IDs. Instructions should make the tool sequence clear; live validation expects all demo tool stages to execute. Never use hidden default IDs, unkeyed stale caches, invented external effects or execution claims. Mutation tools require approval and must guard direct callback invocation too. Reports may be written without approval; simulated bookings/deletions/access changes must be explicitly documented as workspace-only. Do not call remote APIs from fixtures.

SelfTests must cover nominal output, relevant missing/ambiguous/invalid data, two distinct IDs or repeated calls (no stale state), and approval/idempotency for mutations. Use unique temp workspaces, no fixed shared test dirs. No keys, HTTP, live services, global config, commits or pushes in workers. All .cs begin `// PiSharp-only (no upstream Pi source): ...`. Preserve public upstream catalogs and runtime APIs. Workers may add helper classes inside their own projects, not Shared. Existing project references may be built only after main confirms Shared build, using `-p:BuildProjectReferences=false`; do not compile all projects concurrently. Read appropriate existing sample for structure; do not clone its domain behavior mechanically.
