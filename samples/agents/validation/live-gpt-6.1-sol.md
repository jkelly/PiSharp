# GPT-6.1 Sol live sample validation

Validated on October 29, 2026 (America/Chicago).

## Results

- **32/32 live agent integrations passed** with `gpt-6.1-sol` through `openai-responses`.
- Authentication used the existing `OPENAI_API_KEY` environment variable. The value was neither displayed nor saved.
- The final suite recorded 31,902 input tokens and 6,378 output tokens, including the coordinator's child agents. These counts exclude earlier attempts and supplemental tests; no cost estimate is claimed.
- A supplemental live image test read a synthetic receipt (no OCR text supplied), captured its lines, confirmed a total of **17.82**, and classified it as **Meals** with balanced charges.
- A separate live smoke test passed without an explicit `--api` flag: OpenAI now defaults to Responses.
- The solution builds without warnings/errors. All **11 shared-runtime regression tests** and the receipt-specific self-test pass.

## What the live tests verify

Each sample ran its real PiSharp agent loop against the requested model. The harness requires a successful process and host result, every expected fixture workflow tool stage, the requested model in the assistant transcript, and nonzero provider token usage. Distinct tool arguments and call counts remain model-driven. This verifies model/tool integration, not a comprehensive model-quality evaluation.

Domain data remained controlled fixtures. No external repository was audited or changed, no GitHub PR/issue was published, no image-generation provider was invoked, and no schedule was enabled. The receipt-image check used a generated test receipt, not personal data.

## Findings and fixes

1. Initial live pass: **29/32**. The task-manager, knowledge, and CRM fixtures lacked sufficiently concrete user tasks. The CRM input did not actually request an update. Added explicit task descriptions and taught the shared host to use the input `task` field.
2. The host now communicates already-granted tool approval to the model while retaining runtime enforcement and the user's narrower task scope.
3. The live image check exposed the default text-only Responses projection. Enabled native image projection and added an intercepted-HTTP image/tool continuation regression.
4. Receipt tool calls containing fractional dollar arguments could not be replayed by the existing native Responses argument profile. The receipt interface now uses validated integer cents and converts to decimal display amounts. Generic fractional-number argument replay was not changed.
5. Added provider-appropriate protocol defaults: Responses for OpenAI, Messages for Anthropic, and Completions for compatible providers.

## Reproduce

```powershell
pwsh -File samples/agents/scripts/run-live.ps1 -Model gpt-6.1-sol -ApiKeyEnvironment OPENAI_API_KEY
```

The harness uses four concurrent samples by default. Override `-Examples`, `-Concurrency`, `-TimeoutSeconds`, or `-OutputDirectory` as needed. Each invocation requires a fresh output destination and retains transcripts/reports. It makes paid API requests.

## Evidence and source basis

The live test evidence was recaptured on current-main base `f49f212fc476c9e3a1879bd11c5c209222a29e1f` plus this PR. The suite passed again after integration. The portable [JSON summary](live-gpt-6.1-sol.json) records all cases; raw transcripts remain local ignored artifacts. Offline revalidation passed all 32 sample self-test programs and demos, 11 shared-runtime checks, and 75 model-registry cases. The live receipt-image check was also repeated successfully on this base.

| # | Sample | Result | Tool stages |
|---|---|---|---|
| 1 | Inbox Triage | PASS | `assess_message`, `list_messages` |
| 2 | Email Reply Assistant | PASS | `draft_reply`, `read_thread` |
| 3 | Meeting Follow-through | PASS | `extract_actions`, `prepare_followup` |
| 4 | Daily Work Planner | PASS | `build_plan`, `list_commitments` |
| 5 | Persistent Task Manager | PASS | `inspect_task`, `list_tasks` |
| 6 | Company Knowledge Assistant | PASS | `get_passage`, `search_passages` |
| 7 | Document Change Explainer | PASS | `compare_sections`, `read_versions` |
| 8 | Form Intake Assistant | PASS | `prepare_routing`, `validate_form` |
| 9 | Helpdesk Resolution Agent | PASS | `inspect_ticket`, `suggest_resolution` |
| 10 | IT Troubleshooting Assistant | PASS | `diagnose_device`, `inspect_device` |
| 11 | Order Status Concierge | PASS | `estimate_delivery`, `lookup_order` |
| 12 | Returns Assistant | PASS | `calculate_refund`, `check_eligibility` |
| 13 | CRM Update Assistant | PASS | `find_contact`, `preview_update` |
| 14 | Client Meeting Briefing | PASS | `build_meeting_brief`, `fetch_meeting_context` |
| 15 | Customer Feedback Analyst | PASS | `analyze_themes`, `rank_themes` |
| 16 | Receipt-to-Expense Assistant | PASS | `classify_expense`, `parse_receipt_text` |
| 17 | Invoice Reconciliation Agent | PASS | `calculate_payable`, `match_invoice` |
| 18 | Vendor Quote Comparator | PASS | `normalize_quotes`, `rank_quotes` |
| 19 | Inventory Reorder Assistant | PASS | `build_reorder_plan`, `calculate_stock_gaps` |
| 20 | Weekly Operations Reporter | PASS | `calculate_week_report`, `fetch_week_metrics` |
| 21 | Spreadsheet Cleanup Agent | PASS | `clean_csv`, `quality_report` |
| 22 | Repository Onboarding Guide | PASS | `inspect_layout`, `write_guide` |
| 23 | Bug Investigation and Fix Agent | PASS | `patch_and_verify`, `reproduce` |
| 24 | Pull Request Review Assistant | PASS | `scan_diff`, `write_review` |
| 25 | Release Notes Assistant | PASS | `classify_commits`, `draft_notes` |
| 26 | Localization Assistant | PASS | `validate_strings`, `write_review` |
| 27 | Product Content Studio | PASS | `prepare_visual_asset`, `write_copy` |
| 28 | Research Workbench | PASS | `branch_inquiry`, `capture_evidence` |
| 29 | Project Delivery Coordinator | PASS | `dispatch_analysts`, `synthesize_delivery` |
| 30 | Desktop Workflow Assistant | PASS | `prepare_brief`, `validate_request` |
| 31 | Model Catalog and Pricing Maintainer | PASS | `draft_change_review`, `inspect_providers`, `refresh_catalog` |
| 32 | NuGet Vulnerability Remediation Agent | PASS | `maintain_fixture` |
