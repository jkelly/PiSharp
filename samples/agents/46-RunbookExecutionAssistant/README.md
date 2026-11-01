# Runbook Execution Assistant

Executes a small allowlisted local runbook through native PiSharp tools. The supported actions are `write_text`, `check_exists`, `check_contains`, and `sha256`; there is no shell or arbitrary command execution.

```powershell
dotnet run --project samples/agents/46-RunbookExecutionAssistant -- --demo --approve
dotnet run --project samples/agents/46-RunbookExecutionAssistant -- --self-test
```

`inspect_runbook` validates 1–20 steps, unique IDs, action arguments, safe paths and an acyclic dependency graph. Each action stays below `operations/<runbookId>/` in the selected workspace; traversal and linked paths are rejected. `execute_runbook` requires host approval and an unchanged inspected plan. Dependencies execute first; failed prerequisites cause dependent steps to be skipped. Repeated successful writes preserve the same contents and return the same evidence. `write_execution_report` saves actual outcomes in `runbook-execution.json`, bound to a SHA-256 fingerprint of the executed plan. Reinspection invalidates prior execution state; changed plans must execute again before a new report can be written.

The recovery fixture writes a local status file, checks its existence and contents, then hashes its bytes. The audit fixture demonstrates a missing file and a skipped dependent hash. These are test workspace actions and make no production changes. Twelve focused cases cover actual execution, repeated execution, distinct runbooks, failed/skipped steps, approval, missing data, unsupported commands, traversal, cycles, plan changes, stale-report rejection and reinspection invalidation. No credentials or network access are required.
