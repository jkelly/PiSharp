# Deployment readiness reviewer

Reviews structured release evidence through `inspect_release` → `evaluate_gates` → `write_readiness_report`. Every tool takes an explicit `releaseId`; the fixture requests `release-42`.

The input declares required tests and observed results, an explicit migration inventory (including an empty array when none exist), required configuration values, and rollback rehearsal/artifact evidence. Missing, failed, skipped, malformed or ambiguous evidence blocks readiness. A source snapshot binds each evaluated/report stage to the inspected evidence. The output is `readiness-<releaseId>.json`, with every gate and its decision.

This sample reviews supplied evidence. It does not run tests, inspect a live environment or deploy anything. `ready` means the supplied evidence satisfied the configured gates.

```powershell
dotnet run --project samples/agents/48-DeploymentReadinessReviewer -- --demo
dotnet run --project samples/agents/48-DeploymentReadinessReviewer -- --self-test
```

Seven isolated tests cover success and persistence, missing/failed checks, multiple releases, stale evidence, malformed migration rows and stage/ID guards. No provider credentials are required in demo or self-test mode.
