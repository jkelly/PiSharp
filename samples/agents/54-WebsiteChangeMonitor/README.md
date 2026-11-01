# Website Change Monitor

Compare recorded website responses with persistent baselines and create deduplicated local alert records. The fixture detects a Standard plan price change from 20 to 25 dollars while ignoring whitespace, markup and script changes on the status page.

```powershell
dotnet run --project samples/agents/54-WebsiteChangeMonitor -- --demo --approve
dotnet run --project samples/agents/54-WebsiteChangeMonitor -- --self-test
dotnet run --project samples/agents/54-WebsiteChangeMonitor -- --input path/to/fixture.json --workspace path/to/output --approve
```

The agent calls `inspect_recorded_pages`, `compare_page_content`, then `commit_monitor_results` with explicit `pageIds`. Input `pages` have `id`, `html`, recorded HTTP `status`, `requiredText`, and `minimumChangedWords`; `baselines` maps page IDs to recorded earlier HTML. Normalization removes scripts, style and comments, strips tags, decodes entities and collapses whitespace. Diff evidence reports added and removed distinct, case-normalized words. A changed normalized page alerts only when the current text contains the configured phrase and meets the changed-word threshold. Empty `requiredText` matches any content.

Only recorded status 200 pages advance a baseline; failed responses preserve it. A new page creates an initial baseline without alerting. Source and selected-baseline fingerprints reject stale plans; a committed-plan receipt permits exact replay. Baselines, local alerts and commit receipts persist together in `monitor-state.json`; restarting with the same workspace suppresses duplicate alerts. Conditions are evaluated against persisted state, not a stale process cache.

`monitor-comparison.json` contains reviewable before/after text and word differences. `monitor-report.json` reports counts and the relative state path. The fixture produces **one local alert**, then zero new alerts on repeat. Both host and callback require approval to commit baseline/alert changes. Eight offline regression cases cover conditions, failures, missing IDs, initial baselines, stale sources/baselines, approval and persistent replay.

This sample performs no HTTP requests and sends no notifications. Text extraction is intentionally bounded and is not a browser renderer: hidden nodes, embedded malformed tags and script-generated content need a real DOM adapter for production monitoring.
