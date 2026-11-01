# Data Pipeline Investigator

Trace recorded sales/refund rows through extraction, validation and aggregation; identify the failing stage; apply supplied repairs or quarantine; and rerun the actual validators and totals.

```powershell
dotnet run --project samples/agents/55-DataPipelineInvestigator -- --demo --approve
dotnet run --project samples/agents/55-DataPipelineInvestigator -- --self-test
dotnet run --project samples/agents/55-DataPipelineInvestigator -- --input path/to/fixture.json --workspace path/to/output --approve
```

The native agent calls `trace_pipeline`, `propose_pipeline_remediation`, then `apply_remediation_and_rerun` with all requested `pipelineIds`. Each input pipeline supplies `id`, `currency`, known `accountIds` and `rows`. Row IDs must be unique. Validation checks account references, integer `amountCents` in 0–1,000,000,000, and the exact expected currency. Any invalid row blocks aggregation and is reported with field-level evidence.

`remedies` explicitly name a `pipelineId`, `rowId`, and `action` (`repair` or `quarantine`). Repairs can change only `accountId`, `amountCents`, and `currency`, using supplied `values`; the corrected row must pass validation. Missing or invalid remedies stay unresolved and block the whole mutation batch. Fingerprints protect against source/remedy changes. Both host and callback require approval.

The fixture repairs a string amount, quarantines an unknown-account row, and fixes a refund currency. The rerun computes **3,700 sales cents** and **400 refund cents**. `pipeline-state.json` preserves accepted rows and complete quarantined rows with reasons; `pipeline-plan.json` and `pipeline-rerun.json` explain changes and actual stage results. Repeated application does not duplicate quarantine entries, and restarted workflows read the persisted data.

Six offline regression cases cover both pipelines, invalid/missing data, failed corrections, stale inputs, approval, replay and restart. All extraction, repairs and reruns use local fixture JSON. No production ETL service is queried, restarted or modified; this sample does not claim execution of an external pipeline.
