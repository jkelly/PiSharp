# Samples 33–62: GPT-6.1 Sol validation

Validated November 1, 2026 (America/Chicago), using the existing `OPENAI_API_KEY` with `gpt-6.1-sol` through the native Responses transport.

## Results

- **30/30 new live integrations passed**, including strict comparisons of computed tool results against fresh fixture baselines.
- All **62 sample self-test programs and 62 offline demos passed**; 13 shared runtime checks passed.
- The full sample solution built with zero warnings and errors.
- Final validated attempts recorded 56,270.0 input tokens and 8,234.0 output tokens. No cost estimate is claimed.
- Across all 55 attempts (including regression reruns), usage was 90,109.0 input and 13,822.0 output tokens. Five initial attempts were blocked by the local network sandbox before provider usage; rerunning with network access succeeded.

Each live run required success from the process and native host, every expected tool stage, the requested model in the transcript, nonzero provider usage, and matching structured fixture results. Comparison normalizes JSON property order and workspace roots while preserving meaningful array order. It does not grade free-form prose or establish comprehensive production correctness.

## Review fixes

Independent review and added negative regressions addressed malformed calendar/source records, persisted roster recovery, ambiguous incident deduplication keys, stale runbook reports, omitted retention intervals, contradictory duplicate-group identities, missing redaction source text and redaction-marker collisions. PR review additionally fixed blank approvers, overflowed travel arithmetic, malformed runbook steps, chained migration verification, reused merge IDs and redaction replacement-boundary collisions. Every affected workflow was rebuilt and retested live after its final source change.

## Scope and reproduction

All domain inputs were authored fixtures. Tools performed real calculations and local workspace file operations; no messages were sent, tickets purchased, accounts changed, production records deleted or external services mutated. The API key and raw transcripts remain outside source control. This evidence was produced from a working tree based on `585a7c61d89e70c2604756c919070e0687a5e21a` with the sample changes applied.

```powershell
dotnet build samples/agents/PiSharp.Samples.slnx -m:1
./samples/agents/scripts/run-all.ps1 -SkipBuild
./samples/agents/scripts/run-live.ps1 -Examples (33..62) -Model gpt-6.1-sol -ApiKeyEnvironment OPENAI_API_KEY -CompareToolResults -SkipBuild
```

Use `-Dotnet <absolute-sdk-path>` when needed. Live tests make paid API requests. [Portable machine-readable results](live-gpt-6.1-sol-33-62.json) omit local paths, credentials and transcripts.

| # | Sample | Result | Verified tool stages |
|---|---|---|---|
| 33 | Calendar Scheduling Coordinator | PASS | `book_local_meeting`, `find_shared_slots`, `inspect_calendars`, `save_proposal` |
| 34 | Employee Onboarding Coordinator | PASS | `build_checklist`, `inspect_employee`, `save_onboarding_plan` |
| 35 | Employee Offboarding Coordinator | PASS | `inspect_departure`, `plan_offboarding`, `revoke_local_access`, `save_offboarding_plan` |
| 36 | Access Review Assistant | PASS | `inspect_access`, `remove_local_access`, `review_access`, `save_access_review` |
| 37 | Contract Obligation Tracker | PASS | `calculate_deadlines`, `inspect_contract`, `save_obligation_register` |
| 38 | Subscription Renewal Assistant | PASS | `compare_renewal`, `inspect_subscription`, `save_renewal_plan` |
| 39 | RFP Response Builder | PASS | `inspect_rfp`, `match_requirements`, `save_response_draft` |
| 40 | Purchase Request Coordinator | PASS | `evaluate_purchase`, `inspect_purchase`, `submit_purchase` |
| 41 | Shipment Exception Agent | PASS | `detect_exception`, `inspect_tracking`, `save_notification_drafts` |
| 42 | Field Service Dispatcher | PASS | `assign_technician`, `inspect_dispatch`, `rank_candidates` |
| 43 | Shift Coverage Coordinator | PASS | `assign_replacement`, `inspect_shift`, `rank_replacements` |
| 44 | Training Compliance Tracker | PASS | `assess_compliance`, `draft_reminders`, `inspect_training` |
| 45 | Incident Timeline Builder | PASS | `build_timeline`, `collect_evidence`, `publish_timeline` |
| 46 | Runbook Execution Assistant | PASS | `execute_runbook`, `inspect_runbook`, `write_execution_report` |
| 47 | Backup Restore Verification Agent | PASS | `create_snapshot`, `plan_backup`, `verify_restore` |
| 48 | Deployment Readiness Reviewer | PASS | `evaluate_gates`, `inspect_release`, `write_readiness_report` |
| 49 | Flaky Test Investigator | PASS | `analyze_runs`, `draft_repair`, `write_investigation` |
| 50 | Database Migration Reviewer | PASS | `review_rollback`, `simulate_migration`, `write_migration_review` |
| 51 | API Deprecation Migration Agent | PASS | `apply_migration`, `inspect_migration`, `verify_migration` |
| 52 | Documentation Drift Detector | PASS | `inspect_document`, `propose_corrections`, `write_corrections` |
| 53 | Accessibility Audit Assistant | PASS | `apply_accessibility_fixes`, `inspect_accessibility`, `propose_accessibility_fixes` |
| 54 | Website Change Monitor | PASS | `commit_monitor_results`, `compare_page_content`, `inspect_recorded_pages` |
| 55 | Data Pipeline Failure Investigator | PASS | `apply_remediation_and_rerun`, `propose_pipeline_remediation`, `trace_pipeline` |
| 56 | Data Retention Cleanup Agent | PASS | `execute_retention_cleanup`, `inspect_retention`, `plan_retention_cleanup` |
| 57 | Duplicate Record Resolver | PASS | `apply_duplicate_merges`, `inspect_duplicate_evidence`, `propose_duplicate_merges` |
| 58 | Knowledge Gap Finder | PASS | `analyze_coverage`, `inspect_signals`, `save_article_tasks` |
| 59 | Grant Application Organizer | PASS | `assemble_review_package`, `inspect_requirements`, `validate_completeness` |
| 60 | Travel Disruption Assistant | PASS | `inspect_disruption`, `rank_alternatives`, `rebook_local`, `save_rebooking_plan` |
| 61 | Document Redaction Assistant | PASS | `detect_sensitive`, `export_redacted`, `preview_redaction`, `save_redaction_review` |
| 62 | Insurance Claim Organizer | PASS | `assemble_claim_manifest`, `inventory_evidence`, `reconcile_receipts` |
