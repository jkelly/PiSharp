# Employee Onboarding Coordinator

Build a role-specific onboarding checklist from employee records, shared tasks, role tasks and owner records. For `emp-101`, paperwork is complete, account provisioning is ready, and training/repository access wait on their prerequisites. Repository access is assigned to the employee's actual engineering manager.

```powershell
dotnet run --project samples/agents/34-EmployeeOnboardingCoordinator -- --demo
dotnet run --project samples/agents/34-EmployeeOnboardingCoordinator -- --self-test
```

Run these commands from the repository root. `inspect_employee` resolves the unique employee and role, `build_checklist` topologically sorts prerequisites, and `save_onboarding_plan` writes `onboarding-emp-101.json` under the workspace. Every checklist row includes owner, due date relative to the employee's start date, status and incomplete prerequisites. Plans distinguish completed, ready and blocked work; creating a plan does not complete a prerequisite.

Assignments are proposed local records. No HR, directory, ticketing or account system is changed, and approval is unnecessary for this report. Use `--input FILE --workspace DIR` to supply another employee dataset. `--live --model MODEL` lets a native provider call the same tools. Required source arrays are `employees`, `roles`, `owners` and `commonTasks`; role task IDs must be unique across shared and role-specific tasks.

Self-tests cover native tool execution and the persisted artifact, role-specific owners/deadlines, readiness, missing/duplicate employees, missing/cyclic prerequisites, independent second employee evaluation and deterministic repeats.
