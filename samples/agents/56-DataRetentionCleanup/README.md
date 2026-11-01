# Data Retention Cleanup

Evaluate deterministic retention policies and legal holds, preview an exact record selection, and remove only approved expired payloads from local fixture JSON.

```powershell
dotnet run --project samples/agents/56-DataRetentionCleanup -- --demo --approve
dotnet run --project samples/agents/56-DataRetentionCleanup -- --self-test
dotnet run --project samples/agents/56-DataRetentionCleanup -- --input path/to/fixture.json --workspace path/to/output --approve
```

`inspect_retention` accepts explicit `datasetIds`. `plan_retention_cleanup` and `execute_retention_cleanup` additionally require exact `recordKeys` in `datasetId:recordId` form. Those keys must appear in the fixture request's `approvedRecordKeys`, be inside the selected datasets, and be eligible. Host approval and an in-callback approval check are also mandatory.

Input `asOf` is a fixed `yyyy-MM-dd` date. Each policy requires a nonempty `category` and an explicit integer `retainDays` in 0–36,500; a missing interval is rejected rather than defaulting to zero. Expiry is inclusive (`createdOn + retainDays <= asOf`). Records have `id`, `category`, `createdOn`, explicit boolean `legalHold`, and `payload`. Missing hold status, held records, invalid/future creation dates, and unknown policies prevent deletion. IDs are unique within a dataset and cannot contain a colon. Data, hold, policy or approval changes invalidate the preview.

For `2026-11-01`, the fixture removes exactly `logs:log-old` and `exports:export-old`; three records remain, including a legal hold and a record without a policy. `retention-state.json` contains remaining payloads and metadata-only tombstones. It contains no deleted payload content. `retention-plan.json` is the sensitive pre-deletion review artifact and retains the selected source snapshots; production retention of review artifacts requires its own policy. `retention-receipt.json` identifies the actual removed keys. Replays, including after restart with the same policy/request, do not remove records twice.

Eight offline cases test exact boundaries, legal holds, missing hold status or retention interval, unknown/missing IDs, stale plans, approval and persistent replay. Cleanup only rewrites the sample's local JSON store. It never enumerates or deletes arbitrary files, cloud objects, databases, or user-selected directories.
