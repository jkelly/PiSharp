# Employee Offboarding Coordinator

Create an ordered, auditable departure plan for `emp-201`: revoke access, recover equipment, complete handoffs, and retain unresolved obligations such as expense settlement. The fixture deliberately leaves a laptop return, handoff acceptance and financial obligation pending.

```powershell
dotnet run --project samples/agents/35-EmployeeOffboardingCoordinator -- --demo
dotnet run --project samples/agents/35-EmployeeOffboardingCoordinator -- --demo --approve
dotnet run --project samples/agents/35-EmployeeOffboardingCoordinator -- --self-test
```

From the repository root, run `inspect_departure`, `plan_offboarding`, then `save_offboarding_plan` via the sample. The artifact `offboarding-emp-201.json` includes phased steps, responsible owners and pending obligations with days relative to departure. Equipment recovery remains blocked while access revocation needs approval. Handoffs require actual recipient acceptance, and the plan is incomplete until every action and obligation is resolved.

`--approve` additionally invokes `revoke_local_access`, writing `revocations-emp-201.json` in the workspace. It affects **only a local simulation ledger**. It never disables remote accounts or marks equipment, handoffs or financial work complete. Host and callback enforce approval. Exact repeats return the same receipt; changed source records under the same employee ID fail rather than reusing stale approval. Recalculated plans read the ledger, while the saved pre-approval plan remains a review snapshot. Use a dedicated workspace for this sequential sample.

Input requires `employeeId` and uniquely identified `employees`, each with departure date and explicit `access`, `equipment`, `handoffs`, `obligations` arrays. Use `--input FILE --workspace DIR` or native orchestration with `--live --model MODEL`.

Self-tests cover native three/four-stage execution, phase ordering, pending obligations, missing/duplicate IDs, a second completed departure, direct-callback approval, local state updates, unchanged manual obligations, idempotent replay and changed-input conflict.
