# D1: runtime discovery

| Field | Result |
| --- | --- |
| Status | complete |
| Model / effort | gpt-6-sol / medium |
| Changed files | none; read-only explorer |
| Commands | Targeted `rg` and `Get-Content` on contracts, models, jobs, history and tests |
| Blockers | none for planning |

Existing `PiSharp.Contracts.ModelOperations` supports choice/score/bool questions, provider confidence/probability and `ClassifierResult.Usage`, including billed failures. `PiSharp.AI.ModelOperations` routes provider-neutral adapters; use these instead of a mandatory Jev dependency. `PiRequestBudget` is a transport-memory limit, not a spend ledger.

`Hosting/Apps/Jobs.cs` provides checkpointed phase data, receipt-backed `SaveStateAsync`, conversation-scoped full records and a 30-card ring. `AskAgentAsync` reuses one child and call-index request keys; concurrent research needs stable branch/attempt identities, bounded children, durable join/cancel and frozen synthesis input. Task docs retire at settlement; durable receipts and historical artifacts need retained ownership.

No durable source retrieval domain currently exists. Add host-side adapters with bounded transport, provenance, caching, health and budget admission. Recorded generation usage in `ExecutionHistory.cs` is retrospective; direct classifier/retrieval usage must be captured separately, with atomic reservations before parallel dispatch and explicit uncertain outcomes.

Relevant verification: `Hosting.Tests/JobReplayCases.cs`, `JobCases.cs`, `AutomationCases.cs`; `Classifiers.Tests/RegistryCases.cs`, `DecisionsCases.cs`, `SystemOneCases.cs`; durable task recovery/retention suites. Follow-up: freeze policy/result/evidence/budget contracts, cancellation semantics and replay rules before implementation. Incorporated into roadmap F1–F3, judgment, retrieval and research gates.
