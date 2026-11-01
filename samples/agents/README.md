# 32 practical PiSharp agents

Runnable .NET 10 sample projects using **PiSharp.Agent** and **PiSharp.AI**, with explicit tool schemas, meaningful domain operations, fixture inputs, and focused tests. Each project stands alone; Shared contains the common host. The main PiSharp solution and its upstream-pinned catalogs are retained.

## Quick start

Use the SDK pinned in the repository's `global.json` (10.0.401). No external NuGet packages are required by the sample solution.

```powershell
dotnet build samples/agents/PiSharp.Samples.slnx -m:1
dotnet run --project samples/agents/01-InboxTriage -- --demo
dotnet run --project samples/agents/05-PersistentTaskManager -- --demo --approve
pwsh -File samples/agents/scripts/run-all.ps1
```

The script also accepts `-Examples 5,28,31,32`, `-SkipBuild`, and `-Dotnet <absolute-dotnet-path>`. It uses the verified local SDK in `artifacts/sample-tools/dotnet` when available. It runs shared runtime checks, each selected project's self-tests, and every fixture demo, then saves JSON reports under `artifacts/sample-validation`.

All demos are deterministic, use authored fixture data, make no provider requests, and create no external tickets, messages, PRs, or purchases. They still execute through the real PiSharp agent loop and tool scheduler. `--approve` allows the demo's guarded effects in its workspace; omit it to exercise denial. Each sample README documents its actual tool behavior, artifacts, and limitations.

## Live model execution

```powershell
dotnet run --project samples/agents/06-CompanyKnowledgeAssistant -- --live --model <model-id> --input my-documents.json --workspace artifacts/knowledge-run
```

The host reads the existing `OPENAI_API_KEY` by default and never writes it to disk. It does not read local env files automatically. Choose a provider with `--provider`, an API with `--api`, and optional `--base-url` and `--key-env`. Defaults are Responses for OpenAI, Messages for Anthropic, and Chat Completions for OpenRouter and other compatible providers. Native PiSharp transports handle requests, streaming and tool-call continuation. Use `--provider anthropic --key-env ANTHROPIC_API_KEY` for Anthropic. Use `--provider openrouter --key-env OPENROUTER_API_KEY` for OpenRouter.

Environment equivalents: `PISHARP_SAMPLE_MODEL`, `PISHARP_SAMPLE_PROVIDER`, `PISHARP_SAMPLE_API`, `PISHARP_SAMPLE_BASE_URL`, `PISHARP_SAMPLE_KEY_ENV`. A live model ID is required; no paid model is chosen silently. The existing key can be reused without exposing its value.

Run the complete live integration suite with the environment key:

```powershell
pwsh -File samples/agents/scripts/run-live.ps1 -Model gpt-6.1-sol -ApiKeyEnvironment OPENAI_API_KEY
```

This makes paid Responses API requests with up to four concurrent samples. It compares each live run's executed tool stages with a fresh fixture baseline, checks the requested model and provider token usage, and saves per-sample transcripts plus an aggregate report under `artifacts/live-samples`. Use `-Examples 1,16,31,32`, `-Concurrency 2`, or `-OutputDirectory` to bound a run. Domain services remain fixtures; this command does not publish GitHub PRs or invoke image generation. GPT-6.1 Sol tool calling requires `--api openai-responses` when invoking individual projects.

Live chat mode lets the model choose among each sample's actual domain tools; the underlying service data remains the input you supply. It does not silently connect an inbox, CRM, calendar, browser, or expense system. Those integrations have explicit sample-owned tool boundaries. Model 27 has an opt-in image API path; maintainer 31 and 32 use their own documented `--maintain` commands for live repository work.

An input JSON `task` string describes the concrete user request; the host includes it with the input data. `--prompt` overrides that complete user message. `--approve` is communicated to the model as well as enforced by the tool executor. `--image receipt.png` attaches a local PNG/JPEG/WebP (up to 20 MiB) to the native multimodal request; choose a vision-capable model. `--timeout` limits a run (default 120 seconds); Ctrl+C cancels it. `--workspace` selects an artifact directory; otherwise a unique temporary directory is used. Each run saves `report.json` and `transcript.json`. `--resume <transcript.json>` replays the retained conversation with fresh tool declarations; source-specific persisted state is described in the individual sample.

Token usage is reported by native transports. The generic sample host has no verified model-price table, so its zero-filled native cost fields are not a cost estimate. The pricing maintainer tracks independently verified prices.

## Automated maintainers

- [31: Model catalog and pricing](31-ModelCatalogMaintainer/README.md) supports provider toggles, official-source adapters, price-change evidence, a versioned overlay, PRs and linked price issues. First-run comparison can seed from the existing pinned source catalog. Missing or ambiguous prices stay unknown. New or unsupported metadata stays reviewable without pretending it is executable.
- [32: NuGet vulnerability remediation](32-NuGetVulnerabilityMaintainer/README.md) supports selected repositories, direct/transitive/all-framework audits, stable-version trials, lock files, builds/tests, source migrations and draft PRs. Unavailable advisory feeds, unsupported projects, missing tests and unresolved fixes remain incomplete.

Each maintainer includes an **inactive** GitHub Actions template and example configuration. Copy the template into a controller repository, configure the schedule, selected repositories/providers, and required secrets, then enable it. No live schedule is installed by running a demo. Follow the maintainer's `--maintain ... --approve` instructions to authorize a real run. Publishing creates or updates a reviewable PR and never merges it. Actual GitHub/provider operations require network access and the corresponding permissions; offline tests do not claim to verify those accounts.

### Consuming maintained models in PiSharp

`catalog-maintained/models.overlay.json` is a separate source-controlled catalog layer, initially empty. PiSharp copies it beside its CLI. Set `PISHARP_MAINTAINED_CATALOG` to an explicit alternative file if needed. Model composition is: pinned catalog, remote catalog, maintained catalog, user configuration, extension configuration. `/reload`/registry rebuild reads the maintained file again.

The native consumer admits complete supported chat entries and unambiguous USD-per-million-token base prices. It preserves existing transport routes and compatibility metadata. Unsupported billing dimensions, ambiguous conditions, incomplete fields and unavailable transport types remain in the maintained evidence file and produce diagnostics instead of guessed runtime prices. Updating a model with existing price tiers requires a reviewed tier mapping; a flat rate cannot silently replace only part of that table. The original pinned files, hashes and upstream parity checks remain unchanged.

## Validation

See the [live test report](validation/live-gpt-6.1-sol.md) and [machine-readable results](validation/live-gpt-6.1-sol.json) for the verified GPT-6.1 Sol runs, fixes discovered during testing, and current-main revalidation. Raw transcripts and build artifacts are not committed.

The `Agent samples` GitHub Actions workflow builds this solution and runs every self-test and fixture demo on Windows, Linux, and macOS. It uses no provider credentials or live service writes. The shared host treats turn limits and truncated final answers as incomplete runs.

## Catalog

See [catalog.json](catalog.json) for the machine-readable number, name and project path. Each directory below contains its own README, `fixture.json`, runnable project and `--self-test`.

| # | Sample |
|---|---|
| 1 | [Inbox Triage](01-InboxTriage/README.md) |
| 2 | [Email Reply Assistant](02-EmailReplyAssistant/README.md) |
| 3 | [Meeting Follow-through](03-MeetingFollowThrough/README.md) |
| 4 | [Daily Work Planner](04-DailyWorkPlanner/README.md) |
| 5 | [Persistent Task Manager](05-PersistentTaskManager/README.md) |
| 6 | [Company Knowledge Assistant](06-CompanyKnowledgeAssistant/README.md) |
| 7 | [Document Change Explainer](07-DocumentChangeExplainer/README.md) |
| 8 | [Form Intake Assistant](08-FormIntakeAssistant/README.md) |
| 9 | [Helpdesk Resolution Agent](09-HelpdeskResolution/README.md) |
| 10 | [IT Troubleshooting Assistant](10-ItTroubleshooting/README.md) |
| 11 | [Order Status Concierge](11-OrderStatusConcierge/README.md) |
| 12 | [Returns Assistant](12-ReturnsAssistant/README.md) |
| 13 | [CRM Update Assistant](13-CrmUpdateAssistant/README.md) |
| 14 | [Client Meeting Briefing](14-ClientMeetingBriefing/README.md) |
| 15 | [Customer Feedback Analyst](15-CustomerFeedbackAnalyst/README.md) |
| 16 | [Receipt-to-Expense Assistant](16-ReceiptToExpense/README.md) |
| 17 | [Invoice Reconciliation Agent](17-InvoiceReconciliation/README.md) |
| 18 | [Vendor Quote Comparator](18-VendorQuoteComparator/README.md) |
| 19 | [Inventory Reorder Assistant](19-InventoryReorder/README.md) |
| 20 | [Weekly Operations Reporter](20-WeeklyOperationsReporter/README.md) |
| 21 | [Spreadsheet Cleanup Agent](21-SpreadsheetCleanup/README.md) |
| 22 | [Repository Onboarding Guide](22-RepositoryOnboarding/README.md) |
| 23 | [Bug Investigation and Fix Agent](23-BugInvestigation/README.md) |
| 24 | [Pull Request Review Assistant](24-PullRequestReview/README.md) |
| 25 | [Release Notes Assistant](25-ReleaseNotes/README.md) |
| 26 | [Localization Assistant](26-LocalizationAssistant/README.md) |
| 27 | [Product Content Studio](27-ProductContentStudio/README.md) |
| 28 | [Research Workbench](28-ResearchWorkbench/README.md) |
| 29 | [Project Delivery Coordinator](29-ProjectDeliveryCoordinator/README.md) |
| 30 | [Desktop Workflow Assistant](30-DesktopWorkflowAssistant/README.md) |
| 31 | [Model Catalog and Pricing Maintainer](31-ModelCatalogMaintainer/README.md) |
| 32 | [NuGet Vulnerability Remediation Agent](32-NuGetVulnerabilityMaintainer/README.md) |
