# Samples 38–42 implementation report

Status: implemented and locally verified on 2026-11-01. Batch `batch-b`; assigned complexity hard, requested model `gpt-6.1-sol`, reasoning high, requested service tier ultrafast. Effective tier is unverified because the delegation interface provides no tier parameter.

Owned directories only:

- `samples/agents/38-SubscriptionRenewalAssistant`
- `samples/agents/39-RfpResponseBuilder`
- `samples/agents/40-PurchaseRequestCoordinator`
- `samples/agents/41-ShipmentExceptionAgent`
- `samples/agents/42-FieldServiceDispatcher`
- This report.

Each directory contains the supplied Program/csproj scaffold plus authored `Workflow.cs`, `SelfTests.cs`, `Data.cs`, `fixture.json`, and `README.md`. Restore generated each project's `packages.lock.json`. Shared, solution, catalog, public runtime APIs, and unrelated files were not intentionally edited. All authored C# files start with the required PiSharp-only provenance comment.

## Implemented behavior

| Sample | Inspection / computation / persistence | Fixture result |
|---|---|---|
| 38 Subscription | Read exact subscription; compute 60-day renewal and notice windows, utilization and annual price alternatives; save approved local review plan | SUB-CRM renews in 44 days, 60% utilization, 158400-cent proposed annual cost, 105600-cent saving against quote |
| 39 RFP | Read exact RFP; match requirement capability keys to approved, unexpired evidence; persist source-cited draft with gaps and unsupported claims | RFP-ORBIT supports 2 requirements, has 1 mandatory evidence gap, and flags expired/overstated EU-residency claim |
| 40 Purchase | Read request and budget; compute positive line quantities/prices, available budget and configured approver threshold; approved local pending submission | PR-100 total 75000 cents; available 150000 cents; department manager and finance controller route |
| 41 Shipment | Read/sort exact shipment events; calculate overdue/stall hours at fixture time; persist unsent grounded notifications | SHIP-501 is 18 hours overdue and 52 hours since latest scan; evidence EV-501/EV-502 |
| 42 Dispatch | Read job and technicians; check all skills, shift, overlap, inbound/outbound travel; persist approved top-ranked local assignment | TECH-ADA can serve JOB-71 with 10 minutes inbound and 10 minutes outbound travel |

All have three concrete tools and three deterministic demo calls. Fixture tasks request those exact IDs, tool stages, and mutation parameters. Dates are supplied by fixtures; tool outputs use workspace-relative artifact paths, not random identifiers or wall-clock timestamps. Monetary tool arguments use integer cents. No remote domain APIs are called.

Guarded tools declare `RequiresApproval` and independently reject direct callbacks without `c.Approved`. Purchase and dispatch use caller-supplied operation IDs, source-bound payload comparison, persistent receipts, and `FileMode.CreateNew`. The same operation replays in a fresh context, changed payloads fail, and a new operation cannot duplicate an already-submitted request or assigned job. Existing local receipts reduce purchase availability and add dispatch appointments. An in-process mutation gate serializes direct concurrent mutation admission; cross-process transactionality is explicitly excluded in READMEs.

The subscription write is an approved reviewed-plan artifact, never a cancellation. RFP and shipment writes are reports and require no approval. All READMEs distinguish fixture/model demonstration from actual service integration. No external message, booking, cancellation, purchase, or dispatch occurred.

## Verification

Using the required SDK, each owned project was built sequentially:

```powershell
& 'E:\AI\PiSharp\PiSharp\artifacts\sample-tools\dotnet\dotnet.exe' build 'samples/agents/<directory>/<directory>.csproj' -p:BuildProjectReferences=false --nologo
& 'E:\AI\PiSharp\PiSharp\artifacts\sample-tools\dotnet\dotnet.exe' run --project 'samples/agents/<directory>/<directory>.csproj' --no-build -- --self-test
& 'E:\AI\PiSharp\PiSharp\artifacts\sample-tools\dotnet\dotnet.exe' run --project 'samples/agents/<directory>/<directory>.csproj' --no-build -- --demo --approve
```

All five final builds succeeded with zero warnings and zero errors. All five self-test processes exited 0. All five complete fixture agent loops reported `success:true`, executed all three expected tool stages, and returned zero tool errors.

| Sample | Explicit assertions | Expected rejection checks | Result |
|---|---:|---:|---|
| 38 | 10 | 3 | 13 pass |
| 39 | 11 | 1 | 12 pass |
| 40 | 10 | 5 | 15 pass |
| 41 | 9 | 2 | 11 pass |
| 42 | 17 | 4 | 21 pass |
| Total | 57 | 15 | 72 pass |

Counts above come from the executed assertions/rejection expectations. Three console-only count labels were corrected in source after these successful runs; the main integrator's final build will incorporate those text-only corrections. Tests use unique temporary directories and clean them in `finally`.

Coverage includes nominal arithmetic and selection, missing and duplicate IDs, independent multi-ID/repeated calls, stale totals, expired/unapproved/misquoted evidence, rejected direct unapproved writes, identical artifact rewrites, fresh-context receipt replay, conflicting idempotency payloads, duplicate new operations, persisted budget/schedule effects, future/conflicting tracking events, missing tracking, delivered shipments, inbound and outbound travel, appointment overlap, shift containment, and current position versus historical visits.

No live model/API execution was performed by this worker; the main integrator owns live verification and repository-wide validation. Source/build edits were frozen before handing these projects to that live runner. No blockers remain for integration. No commits or pushes were performed.
