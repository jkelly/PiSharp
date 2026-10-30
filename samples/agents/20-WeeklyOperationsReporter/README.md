# Weekly Operations Reporter

Fetch independent sales, support, and fulfillment data concurrently and calculate weekly rates.

```powershell
dotnet run --project samples/agents/20-WeeklyOperationsReporter
dotnet run --project samples/agents/20-WeeklyOperationsReporter -- --self-test
dotnet run --project samples/agents/20-WeeklyOperationsReporter -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `fetch_week_metrics` then `calculate_week_report` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `calculate_week_report` derives a result from the fixture after `fetch_week_metrics` examines source data. Replace the fixture using `--input` to test other records.

For week `2026-W43`, the fixture reports **$515 revenue**, **90.62% ticket resolution**, and **95.06% on-time fulfillment**. Sales, support, and fulfillment totals are calculated concurrently inside `fetch_week_metrics`; `calculate_week_report` derives rates from those totals. These are fixture records, not live dashboards.
