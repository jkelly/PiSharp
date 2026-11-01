# Flaky test investigator

Uses `analyze_runs` → `draft_repair` → `write_investigation`, with explicit `testId` (`cache-isolation` in the fixture). Input tests contain uniquely identified historical runs, `passed`/`failed` outcomes, and optional seed, order and worker metadata.

Fewer than three observations remain insufficient. All failing runs are classified as consistently broken; all passing runs are stable in the observed evidence. Mixed outcomes are flaky. A dimension is correlated only when at least two runs in one group failed and at least two outside it passed, with complete metadata. Correlation suggests investigation; it does not establish cause.

The proposal limits follow-up validation to 20 runs and persists as `investigation-<testId>.json`. It never claims to have executed those runs or applied a repair. Evidence changes require reanalysis.

```powershell
dotnet run --project samples/agents/49-FlakyTestInvestigator -- --demo
dotnet run --project samples/agents/49-FlakyTestInvestigator -- --self-test
```

Six tests cover correlated failures, consistently broken tests, insufficient/malformed evidence, independent test IDs and repeats, stale evidence, duplicate run IDs and stage guards.
