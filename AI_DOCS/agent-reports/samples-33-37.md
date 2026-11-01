# Batch A: agent samples 33–37

Status: complete, 2026-11-01. Contracts/scaffolding dependencies were satisfied. Implementation stayed within the five assigned sample directories and this report; Shared, the sample catalog, solution and existing runtime APIs were not edited.

Assignment: hard complexity, requested `gpt-6.1-sol` / high reasoning / ultrafast service tier. Effective service tier is **unverified** because the delegation API exposes no service-tier control. No claim is made about changing the parent model.

## Delivered

Each directory contains readable `Workflow.cs`, `SelfTests.cs`, `fixture.json`, `README.md` and the generated `packages.lock.json`, alongside the main agent's scaffolded Program/project file. All authored C# files retain the PiSharp-only header. Three deterministic native tool stages inspect source data, compute decisions, and persist a workspace-relative review artifact. Mutation samples have a fourth stage only when the host grants approval; callback implementations also check approval independently.

| Sample | Behavior | Artifact / local state | Final focused checks |
| --- | --- | --- | ---: |
| 33 CalendarSchedulingCoordinator | Explicit timezone conversion, DST rejection, shared interval coverage, busy/local-booking overlap detection, earliest-slot selection | `proposal-{requestId}.json`; approved `booking-{requestId}.json` | 38 |
| 34 EmployeeOnboardingCoordinator | Shared plus role-specific prerequisite graph, cycle/missing-dependency validation, readiness, manager/team assignments, start-relative deadlines | `onboarding-{employeeId}.json` | 13 |
| 35 EmployeeOffboardingCoordinator | Ordered access/equipment/handoff work, pending obligation dates, local revocation state without pretending manual work is complete | `offboarding-{employeeId}.json`; approved `revocations-{employeeId}.json` | 21 |
| 36 AccessReviewAssistant | Role allowlists, exclusive expiration dates, approved/unexpired exceptions, source-backed removal decisions and guarded local removals | `access-review-{employeeId}.json`; approved `access-removals-{employeeId}.json` | 27 |
| 37 ContractObligationTracker | Source/clause validation, fixed and event-based calendar deadlines, explicit as-of dates, lead windows, unresolved triggers and status counts | `obligations-{contractId}.json` | 18 |

Every fixture contains a concrete `task` and demo-matching ID. No current timestamps, generated IDs, provider calls or absolute workspace paths appear in domain tool results. Bookings/removals/revocations are explicitly workspace-only simulations. Mutation receipts fingerprint the reviewed input and reject changed-input replays. Plans recompute by requested ID and inspect persistent local state; there are no unkeyed caches.

## Verification

SDK: `E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe` (10.0.401).

All five projects built individually with `-p:BuildProjectReferences=false`; all finished with **zero warnings and zero errors**. The initial no-restore build of 33 found no assets file, so the first build of each new project performed the normal repository-configured offline restore with explicit `-p:RestorePackagesPath=C:/Users/jksof/.nuget/packages`. No package feeds are configured in repository NuGet.Config. No shared/runtime source or lockfile diff resulted.

Commands used for each owned project:

```powershell
& 'E:\AI\PiSharp\PiSharp\artifacts\sample-tools\dotnet\dotnet.exe' build samples/agents/NN-Name/NN-Name.csproj -p:BuildProjectReferences=false -p:RestorePackagesPath=C:\Users\jksof\.nuget\packages -v:minimal
& 'E:\AI\PiSharp\PiSharp\artifacts\sample-tools\dotnet\dotnet.exe' run --project samples/agents/NN-Name/NN-Name.csproj --no-build -- --self-test
```

Final total after cross-review fixes: **117 passing focused checks across five self-test runs**. Tests use unique temporary workspaces and include actual native `SampleHost.ExecuteAsync` fixture runs, persisted artifact assertions, missing/ambiguous source records, independent second IDs, repeated runs and domain boundary cases. Samples 33/35/36 additionally test direct-callback approval denial, approved native four-tool execution, idempotent cross-context replay and conflicting input. Calendar explicitly rejects offsetless UTC windows and ambiguous DST local times.

Cross-review identified that Calendar's `OfType<JsonObject>` silently ignored null or scalar availability/busy records. All such source-record filtering in 33–37 now uses strict object validation. Calendar tests cover null and scalar entries in requests, people, availability and busy arrays, asserting both plan/booking rejection and absence of any booking file. Related regressions cover malformed onboarding records, offboarding access/employee records, access-review exceptions/policies/employees and contract events/sources/contracts. All five affected projects were rebuilt individually with `-p:BuildProjectReferences=false --no-restore`; each had zero warnings/errors and its focused tests passed. Fixture success outputs are unchanged.

No live provider/API calls, credentials, global configuration changes, commits or pushes were performed.

## Follow-up / limits

No implementation blocker remains. Parent integration owns all-project builds, catalog/solution integration, live provider validation, review and publication. These samples consume local structured records, execute sequentially and are not production multi-process calendar/IAM/HR systems. Contract dates use calendar days and supplied clauses; arbitrary contract extraction and business-day calendars are outside this sample.
