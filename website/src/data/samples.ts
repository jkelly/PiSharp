import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

export type SampleRuntime = 'Agent' | 'Durable' | 'CustomAgents' | 'Chord' | 'Extensions';

export type SampleCategory =
	| 'Productivity & Docs'
	| 'Support & Commerce'
	| 'Finance & Ops'
	| 'Engineering & Maintainers'
	| 'SRE, Security & Governance'
	| 'Multimodal & Research'
	| 'Durable, Workflows & Extensions';

export type SamplePattern =
	| 'Approval gate'
	| 'Crash & resume'
	| 'Subagents & teams'
	| 'Hybrid tools'
	| 'Multimodal & media'
	| 'Contradictions & audit'
	| 'Session branching'
	| 'Automated PRs'
	| 'Steering & signals';

export interface SampleEntry {
	id: string;
	number?: number;
	badgeLabel: string;
	title: string;
	runtime: SampleRuntime;
	category: SampleCategory;
	patterns: SamplePattern[];
	tools: string[];
	summary: string;
	requiresApproval?: boolean;
	experimental?: boolean;
	unreleased160?: boolean;
	demoCommand: string;
	approveCommand?: string;
	liveCommand?: string;
	selfTestCommand?: string;
	repoPath: string;
	walkthroughUrl?: string;
	exampleCount?: number;
}

interface CatalogRow {
	number: number;
	title: string;
	folder: string;
	project: string;
}

interface AgentMeta {
	category: SampleCategory;
	patterns: SamplePattern[];
	tools: string[];
	summary: string;
	requiresApproval?: boolean;
	demoCommand?: string;
	approveCommand?: string;
	liveCommand?: string;
	walkthroughUrl?: string;
}

const agentMeta: Record<number, AgentMeta> = {
	1: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['list_messages', 'assess_message'],
		summary: 'Reads local message records and scores priority from sender, request and due date with explicit source citations.',
	},
	2: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['read_thread', 'draft_reply'],
		summary: 'Inspects a local email thread and prepares a grounded reply draft without sending external mail.',
	},
	3: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['extract_actions', 'prepare_followup'],
		summary: 'Extracts owners and due dates from local meeting notes and produces a structured follow-up draft.',
	},
	4: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['list_commitments', 'build_plan'],
		summary: 'Schedules daily work blocks against fixed commitments and declared focus capacity from a fixture.',
	},
	5: {
		category: 'Productivity & Docs',
		patterns: ['Approval gate'],
		tools: ['list_tasks', 'inspect_task', 'add_task', 'complete_task'],
		summary: 'Persists a local task board to tasks.json exclusively through approval-gated write tools; denies mutations without --approve.',
		requiresApproval: true,
		approveCommand: 'dotnet run --project samples/agents/05-PersistentTaskManager -- --persist-demo --approve --workspace artifacts/task-board',
		walkthroughUrl: '/docs/samples/persistent-task-manager/',
	},
	6: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['search_passages', 'get_passage'],
		summary: 'Searches versioned company knowledge passages and answers with explicit source citations.',
	},
	7: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['read_versions', 'compare_sections'],
		summary: 'Compares two revisions of a document section by section and explains concrete wording changes.',
	},
	8: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['validate_form', 'prepare_routing'],
		summary: 'Validates required fields on an intake submission and prepares a deterministic team routing payload.',
	},
	9: {
		category: 'Support & Commerce',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_ticket', 'suggest_resolution'],
		summary: 'Matches a helpdesk ticket to grounded knowledge-base resolution steps without mutating external tickets.',
	},
	10: {
		category: 'Support & Commerce',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_device', 'diagnose_device'],
		summary: 'Evaluates local device observations against deterministic diagnostic rules without executing remote commands.',
	},
	11: {
		category: 'Support & Commerce',
		patterns: ['Contradictions & audit'],
		tools: ['lookup_order', 'estimate_delivery'],
		summary: 'Looks up an order and calculates a delivery estimate directly from carrier scan history.',
	},
	12: {
		category: 'Support & Commerce',
		patterns: ['Contradictions & audit'],
		tools: ['check_eligibility', 'calculate_refund'],
		summary: 'Checks a return against policy windows and calculates exact refund amounts without issuing payouts.',
	},
	13: {
		category: 'Support & Commerce',
		patterns: ['Approval gate'],
		tools: ['find_contact', 'preview_update', 'save_update'],
		summary: 'Finds a CRM contact, previews validated field changes and saves an updated workspace record only when approved.',
		requiresApproval: true,
	},
	14: {
		category: 'Support & Commerce',
		patterns: ['Subagents & teams'],
		tools: ['fetch_meeting_context', 'build_meeting_brief'],
		summary: 'Fetches account and opportunity records concurrently, then assembles a factual client briefing.',
	},
	15: {
		category: 'Support & Commerce',
		patterns: ['Contradictions & audit'],
		tools: ['analyze_themes', 'rank_themes'],
		summary: 'Groups customer feedback by issue theme and ranks themes by frequency and severity.',
	},
	16: {
		category: 'Finance & Ops',
		patterns: ['Multimodal & media', 'Contradictions & audit'],
		tools: ['parse_receipt_text', 'capture_receipt', 'classify_expense'],
		summary: 'Parses OCR text or attaches a real PNG/JPEG/WebP receipt image (--image), validates integer-cent totals and classifies expenses.',
		liveCommand: 'dotnet run --project samples/agents/16-ReceiptToExpense -- --live --api openai-responses --model gpt-6.2-sol --image receipt.png',
		walkthroughUrl: '/docs/samples/multimodal-receipt-and-studio/',
	},
	17: {
		category: 'Finance & Ops',
		patterns: ['Contradictions & audit'],
		tools: ['match_invoice', 'calculate_payable'],
		summary: 'Performs three-way matching across an invoice, purchase order and receiving record to compute payment readiness.',
	},
	18: {
		category: 'Finance & Ops',
		patterns: ['Contradictions & audit'],
		tools: ['normalize_quotes', 'rank_quotes'],
		summary: 'Normalizes vendor quotes including shipping costs and ranks options alongside lead time.',
	},
	19: {
		category: 'Finance & Ops',
		patterns: ['Contradictions & audit'],
		tools: ['calculate_stock_gaps', 'build_reorder_plan'],
		summary: 'Computes stock shortfalls from demand and lead time, then produces a budgeted reorder plan.',
	},
	20: {
		category: 'Finance & Ops',
		patterns: ['Subagents & teams'],
		tools: ['fetch_week_metrics', 'calculate_week_report'],
		summary: 'Fetches sales, support and fulfillment metrics concurrently and computes weekly operational rates.',
	},
	21: {
		category: 'Finance & Ops',
		patterns: ['Contradictions & audit'],
		tools: ['clean_csv', 'quality_report'],
		summary: 'Normalizes names and emails, deduplicates spreadsheet rows and outputs a structured data quality report.',
	},
	22: {
		category: 'Engineering & Maintainers',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_layout', 'write_guide'],
		summary: 'Inspects a repository structure and produces an onboarding guide highlighting missing docs.',
	},
	23: {
		category: 'Engineering & Maintainers',
		patterns: ['Contradictions & audit'],
		tools: ['reproduce', 'patch_and_verify'],
		summary: 'Reproduces a boundary comparison bug, prepares a targeted fix and verifies boundary test cases.',
	},
	24: {
		category: 'Engineering & Maintainers',
		patterns: ['Contradictions & audit'],
		tools: ['scan_diff', 'write_review'],
		summary: 'Scans a pull request diff for leaked credentials and risky null dereferences, then drafts review findings.',
	},
	25: {
		category: 'Engineering & Maintainers',
		patterns: ['Contradictions & audit'],
		tools: ['classify_commits', 'draft_notes'],
		summary: 'Classifies commit records by impact category and generates structured release notes.',
	},
	26: {
		category: 'Engineering & Maintainers',
		patterns: ['Contradictions & audit'],
		tools: ['validate_strings', 'write_review'],
		summary: 'Detects missing translation keys and placeholder mismatches across locale catalogs.',
	},
	27: {
		category: 'Multimodal & Research',
		patterns: ['Multimodal & media', 'Approval gate'],
		tools: ['write_copy', 'prepare_visual_asset'],
		summary: 'Drafts constrained product copy offline and includes an opt-in, approval-gated OpenRouter image generation path.',
		requiresApproval: true,
		liveCommand: 'dotnet run --project samples/agents/27-ProductContentStudio -- --generate-image --provider openrouter --model IMAGE_MODEL --key-env OPENROUTER_API_KEY --approve --workspace artifacts/studio',
		walkthroughUrl: '/docs/samples/multimodal-receipt-and-studio/',
	},
	28: {
		category: 'Multimodal & Research',
		patterns: ['Session branching'],
		tools: ['capture_evidence', 'branch_inquiry'],
		summary: 'Persists evidence claims in a native PiSharp v3 JSONL session log and forks a follow-up inquiry with SessionBranchPlanner.Fork.',
	},
	29: {
		category: 'Multimodal & Research',
		patterns: ['Subagents & teams'],
		tools: ['count_tasks', 'rank_risks', 'dispatch_analysts', 'synthesize_delivery'],
		summary: 'Dispatches two separate PiSharp Agent instances concurrently to evaluate status and risk, then synthesizes delivery.json.',
	},
	30: {
		category: 'Productivity & Docs',
		patterns: ['Steering & signals'],
		tools: ['validate_request', 'prepare_brief'],
		summary: 'Exposes a newline-delimited --stdio JSON loop for desktop clients to run agent tasks and receive structured receipts.',
	},
	31: {
		category: 'Engineering & Maintainers',
		patterns: ['Automated PRs', 'Approval gate', 'Contradictions & audit'],
		tools: ['inspect_providers', 'refresh_catalog', 'draft_change_review'],
		summary: 'Fetches official provider model/pricing evidence with SHA-256 hashes, updates models.overlay.json and can open draft PRs with --maintain --approve --publish.',
		requiresApproval: true,
		demoCommand: 'dotnet run --project samples/agents/31-ModelCatalogMaintainer -- --maintain --workspace artifacts/catalog-run',
		approveCommand: 'dotnet run --project samples/agents/31-ModelCatalogMaintainer -- --maintain --live --approve --config catalog.json --workspace artifacts/catalog-live',
	},
	32: {
		category: 'Engineering & Maintainers',
		patterns: ['Automated PRs', 'Approval gate', 'Contradictions & audit'],
		tools: ['audit_graph', 'trial_upgrade', 'verify_tests'],
		summary: 'Audits direct and transitive NuGet advisories, tests minimal stable upgrades with rollback, refreshes lock files and drafts remediation PRs.',
		requiresApproval: true,
		approveCommand: 'dotnet run --project samples/agents/32-NuGetVulnerabilityMaintainer -- --maintain repositories.json --approve --workspace artifacts/nuget-runs',
	},
	33: {
		category: 'Productivity & Docs',
		patterns: ['Approval gate'],
		tools: ['inspect_calendars', 'find_shared_slots', 'save_proposal', 'book_local_meeting'],
		summary: 'Resolves cross-timezone availability (rejecting ambiguous DST instants) and books a local meeting only with approval.',
		requiresApproval: true,
	},
	34: {
		category: 'SRE, Security & Governance',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_employee', 'build_checklist', 'save_onboarding_plan'],
		summary: 'Builds a prerequisite-ordered onboarding checklist and assigns tasks to verified role owners.',
	},
	35: {
		category: 'SRE, Security & Governance',
		patterns: ['Approval gate', 'Contradictions & audit'],
		tools: ['inspect_departure', 'plan_offboarding', 'save_offboarding_plan', 'revoke_local_access'],
		summary: 'Creates an auditable offboarding plan, gates local access revocation behind approval and retains unresolved obligations.',
		requiresApproval: true,
	},
	36: {
		category: 'SRE, Security & Governance',
		patterns: ['Approval gate', 'Contradictions & audit'],
		tools: ['inspect_access', 'review_access', 'save_access_review', 'remove_local_access'],
		summary: 'Reviews grants against role policy, expiry and approved exceptions using a deterministic asOf clock, with approval-gated removals.',
		requiresApproval: true,
	},
	37: {
		category: 'Support & Commerce',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_contract', 'calculate_deadlines', 'save_obligation_register'],
		summary: 'Computes sourced contract obligations, overdue notices and unknown trigger dependencies against a fixed evaluation date.',
	},
	38: {
		category: 'Support & Commerce',
		patterns: ['Approval gate', 'Contradictions & audit'],
		tools: ['inspect_subscription', 'compare_renewal', 'save_renewal_plan'],
		summary: 'Computes integer-cent SaaS renewal economics from active vs. licensed seats and writes a host-reviewed action plan.',
		requiresApproval: true,
	},
	39: {
		category: 'Support & Commerce',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_rfp', 'match_requirements', 'save_response_draft'],
		summary: 'Matches RFP requirements against non-expired approved evidence, flagging mandatory gaps and rejecting unsupported claims.',
	},
	40: {
		category: 'Finance & Ops',
		patterns: ['Approval gate'],
		tools: ['inspect_purchase', 'evaluate_purchase', 'submit_purchase'],
		summary: 'Computes integer-cent purchase totals against department budgets, determines approval routing and gates local submission.',
		requiresApproval: true,
	},
	41: {
		category: 'Finance & Ops',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_tracking', 'detect_exception', 'save_notification_drafts'],
		summary: 'Sorts shipment scan events, detects overdue/stalled thresholds and drafts grounded customer and carrier notices.',
	},
	42: {
		category: 'Finance & Ops',
		patterns: ['Approval gate'],
		tools: ['inspect_dispatch', 'rank_candidates', 'assign_technician'],
		summary: 'Ranks field technicians by skill, shift window and travel buffers, then records a local dispatch assignment when approved.',
		requiresApproval: true,
	},
	43: {
		category: 'Finance & Ops',
		patterns: ['Approval gate'],
		tools: ['inspect_shift', 'rank_replacements', 'assign_replacement'],
		summary: 'Ranks qualified shift replacements and records an approved coverage assignment in a local roster.',
		requiresApproval: true,
	},
	44: {
		category: 'SRE, Security & Governance',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_training', 'assess_compliance', 'draft_reminders'],
		summary: 'Evaluates training completion records against an explicit asOf date and drafts reminder and escalation notices.',
	},
	45: {
		category: 'SRE, Security & Governance',
		patterns: ['Contradictions & audit'],
		tools: ['collect_evidence', 'build_timeline', 'publish_timeline'],
		summary: 'Normalizes multi-source incident records to UTC, deduplicates shared IDs, preserves conflicting variants and prevents stale publication.',
		walkthroughUrl: '/docs/samples/incident-timeline-builder/',
	},
	46: {
		category: 'SRE, Security & Governance',
		patterns: ['Approval gate'],
		tools: ['inspect_runbook', 'execute_runbook', 'write_execution_report'],
		summary: 'Executes an allowlisted local runbook (write_text, check_exists, check_contains, sha256) with zero shell execution.',
	},
	47: {
		category: 'SRE, Security & Governance',
		patterns: ['Contradictions & audit'],
		tools: ['plan_backup', 'create_snapshot', 'verify_restore'],
		summary: 'Snapshots local fixture files, restores into an isolated folder and verifies SHA-256 digests against the manifest.',
	},
	48: {
		category: 'Engineering & Maintainers',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_release', 'evaluate_gates', 'write_readiness_report'],
		summary: 'Evaluates structured release evidence against required quality and sign-off gates to write a readiness report.',
	},
	49: {
		category: 'Engineering & Maintainers',
		patterns: ['Contradictions & audit'],
		tools: ['analyze_runs', 'draft_repair', 'write_investigation'],
		summary: 'Analyzes historical test runs (seed, execution order, worker metadata) to isolate flake patterns and draft a repair.',
	},
	50: {
		category: 'Engineering & Maintainers',
		patterns: ['Contradictions & audit'],
		tools: ['simulate_migration', 'review_rollback', 'write_migration_review'],
		summary: 'Simulates forward and rollback database migrations over fixture table schemas and checks nullability safety.',
	},
	51: {
		category: 'Engineering & Maintainers',
		patterns: ['Approval gate'],
		tools: ['inspect_migration', 'apply_migration', 'verify_migration'],
		summary: 'Replaces deprecated API usage across allowlisted workspace files with occurrence checks and approval-gated writes.',
		requiresApproval: true,
	},
	52: {
		category: 'Engineering & Maintainers',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_document', 'propose_corrections', 'write_corrections'],
		summary: 'Checks documentation commands and setting keys against a repository snapshot and proposes grounded corrections.',
	},
	53: {
		category: 'SRE, Security & Governance',
		patterns: ['Approval gate', 'Contradictions & audit'],
		tools: ['inspect_accessibility', 'propose_accessibility_fixes', 'apply_accessibility_fixes'],
		summary: 'Audits HTML documents for missing lang, alt text and control labels, applies approved fixes and rescans.',
		requiresApproval: true,
	},
	54: {
		category: 'SRE, Security & Governance',
		patterns: ['Approval gate', 'Contradictions & audit'],
		tools: ['inspect_recorded_pages', 'compare_page_content', 'commit_monitor_results'],
		summary: 'Compares recorded page snapshots against persistent baselines to detect meaningful content changes while ignoring markup noise.',
		requiresApproval: true,
	},
	55: {
		category: 'SRE, Security & Governance',
		patterns: ['Approval gate', 'Contradictions & audit'],
		tools: ['trace_pipeline', 'propose_pipeline_remediation', 'apply_remediation_and_rerun'],
		summary: 'Traces failing data rows through extraction, validation and aggregation, then applies approved repairs or quarantine and reruns totals.',
		requiresApproval: true,
	},
	56: {
		category: 'SRE, Security & Governance',
		patterns: ['Approval gate', 'Contradictions & audit'],
		tools: ['inspect_retention', 'plan_retention_cleanup', 'execute_retention_cleanup'],
		summary: 'Evaluates retention rules and legal holds, previews exact deletions and removes only approved expired payloads.',
		requiresApproval: true,
	},
	57: {
		category: 'SRE, Security & Governance',
		patterns: ['Approval gate', 'Contradictions & audit'],
		tools: ['inspect_duplicate_evidence', 'propose_duplicate_merges', 'apply_duplicate_merges'],
		summary: 'Applies conflict-aware merges for verified duplicate identities while preserving ambiguous shared-mailbox records.',
		requiresApproval: true,
	},
	58: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_signals', 'analyze_coverage', 'save_article_tasks'],
		summary: 'Clusters unresolved questions and zero-result searches to measure doc coverage and draft grounded article tasks.',
	},
	59: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['inspect_requirements', 'validate_completeness', 'assemble_review_package'],
		summary: 'Validates grant attachments against count and expiry rules and honestly assembles incomplete review packages.',
	},
	60: {
		category: 'Productivity & Docs',
		patterns: ['Approval gate'],
		tools: ['inspect_disruption', 'rank_alternatives', 'save_rebooking_plan', 'rebook_local'],
		summary: 'Filters feasible rebooking itineraries by connection windows and integer-cent budget, with approval-gated local booking.',
		requiresApproval: true,
	},
	61: {
		category: 'Productivity & Docs',
		patterns: ['Approval gate', 'Contradictions & audit'],
		tools: ['detect_sensitive', 'preview_redaction', 'save_redaction_review', 'export_redacted'],
		summary: 'Detects PII spans in UTF-16 offsets without echoing raw values, merges overlaps and exports redacted text when approved.',
		requiresApproval: true,
	},
	62: {
		category: 'Productivity & Docs',
		patterns: ['Contradictions & audit'],
		tools: ['inventory_evidence', 'reconcile_receipts', 'assemble_claim_manifest'],
		summary: 'Reconciles claim receipts in integer cents, deduplicates copies while retaining source IDs and reports missing documents.',
	},
};

function loadPracticalAgents(): SampleEntry[] {
	const catalogPath = resolve(process.cwd(), '../samples/agents/catalog.json');
	const rows = JSON.parse(readFileSync(catalogPath, 'utf8')) as CatalogRow[];
	const seen = new Set<number>();

	const entries = rows.map((row) => {
		seen.add(row.number);
		const meta = agentMeta[row.number];
		if (!meta) {
			throw new Error(`samples.ts is missing metadata for catalog.json entry #${row.number} (${row.folder}).`);
		}
		const num = String(row.number).padStart(2, '0');
		const projectDir = `samples/agents/${row.folder}`;
		return {
			id: row.folder,
			number: row.number,
			badgeLabel: `#${num}`,
			title: row.title,
			runtime: 'Agent' as const,
			category: meta.category,
			patterns: meta.patterns,
			tools: meta.tools,
			summary: meta.summary,
			requiresApproval: meta.requiresApproval ?? false,
			demoCommand: meta.demoCommand ?? `dotnet run --project ${projectDir} -- --demo`,
			approveCommand:
				meta.approveCommand ??
				(meta.requiresApproval ? `dotnet run --project ${projectDir} -- --demo --approve` : undefined),
			liveCommand:
				meta.liveCommand ??
				`dotnet run --project ${projectDir} -- --live --model gpt-6.2-sol --api openai-responses`,
			selfTestCommand: `dotnet run --project ${projectDir} -- --self-test`,
			repoPath: projectDir,
			walkthroughUrl: meta.walkthroughUrl,
			exampleCount: 1,
		};
	});

	for (const key of Object.keys(agentMeta)) {
		const num = Number(key);
		if (!seen.has(num)) {
			throw new Error(`samples.ts contains metadata for sample #${num}, which is not in samples/agents/catalog.json.`);
		}
	}

	return entries;
}

export const platformSamples: SampleEntry[] = [
	{
		id: 'CustomAgents.Examples',
		badgeLabel: 'Workflow + Team',
		title: 'Hybrid Orders Workflow & 4-Agent Team',
		runtime: 'CustomAgents',
		category: 'Durable, Workflows & Extensions',
		patterns: ['Crash & resume', 'Subagents & teams', 'Hybrid tools'],
		tools: ['lookup_order (C#)', 'shipping_quote (JS)', 'check_stock (Roslyn)', 'run_workflow_orders', 'web_search'],
		summary:
			'7-step durable workflow mixing C# actions, PromptAsync, typed Prompt<RiskVerdict>, Jint JS, Roslyn C# scripts and an Agent step, plus a 4-agent team and --crash-in resume.',
		experimental: true,
		demoCommand: 'dotnet run --project samples/CustomAgents.Examples -c Release -- all',
		approveCommand: 'dotnet run --project samples/CustomAgents.Examples -c Release -- orders --crash-in summarize',
		liveCommand: 'dotnet run --project samples/CustomAgents.Examples -c Release -- --provider openai --model gpt-5.4-mini --dir ./demo-openai',
		repoPath: 'samples/CustomAgents.Examples',
		walkthroughUrl: '/docs/samples/hybrid-orders-workflow/',
		exampleCount: 1,
	},
	{
		id: 'CustomAgents.Research',
		badgeLabel: 'Research Swarm',
		title: 'Parallel Multi-Agent Research Team',
		runtime: 'CustomAgents',
		category: 'Multimodal & Research',
		patterns: ['Subagents & teams', 'Crash & resume'],
		tools: ['delegate (parallel)', 'x_search (Grok)', 'web_search (7 backends)'],
		summary:
			'Lead agent splits a topic into N parts, delegates to parallel researcher subagents combining X search and web search, and merges a cited Markdown report with SQLite resume.',
		experimental: true,
		demoCommand: 'dotnet run --project samples/CustomAgents.Research -c Release -- "How developers use AI coding agents in 2026"',
		liveCommand: 'dotnet run --project samples/CustomAgents.Research -c Release -- "Humanoid robots in 2026" --provider xai --model grok-4.7 --days 90',
		repoPath: 'samples/CustomAgents.Research',
		walkthroughUrl: '/docs/samples/parallel-research-team/',
		exampleCount: 1,
	},
	{
		id: 'Durable.Examples.Core',
		badgeLabel: 'Durable 00–12',
		title: 'Durable Sessions, Forks, Watches & Recovery (00–12)',
		runtime: 'Durable',
		category: 'Durable, Workflows & Extensions',
		patterns: ['Crash & resume', 'Session branching'],
		tools: ['ISession', 'Rewindable docs', 'WatchAsync', 'Registry reload', 'Crash recovery'],
		summary:
			'13 focused offline examples covering durable conversations, rewindable documents, forks, watch folding, harness config, extension state, custom tasks and crash recovery on Memory, JSONL or SQLite.',
		experimental: true,
		demoCommand: 'dotnet run --project samples/Durable.Examples -- --storage sqlite 00 02 04 12',
		selfTestCommand: 'dotnet run --project samples/Durable.Examples -- 00 01 02 03 04 05 06 07 08 09 10 11 12',
		repoPath: 'samples/Durable.Examples',
		walkthroughUrl: '/docs/samples/durable-and-extensions/',
		exampleCount: 13,
	},
	{
		id: 'Durable.Examples.Agents',
		badgeLabel: 'Durable 13–34',
		title: 'Durable Coding Tools, Inbox, Subagents, Plan Mode & Compaction (13–34)',
		runtime: 'Durable',
		category: 'Durable, Workflows & Extensions',
		patterns: ['Crash & resume', 'Subagents & teams', 'Steering & signals', 'Approval gate'],
		tools: ['coding tools', 'inbox steering', 'foreground/background subagent', 'plan mode', 'compaction'],
		summary:
			'22 offline examples covering durable chat, coding tools, JSON mode, steering inbox, late join, foreground & background subagents, compaction, plan mode, sandboxes, tool overrides and turn limits.',
		experimental: true,
		demoCommand: 'dotnet run --project samples/Durable.Examples -- --storage sqlite 19 21 24 26 32',
		liveCommand: 'dotnet run --project samples/Durable.Examples -- 15-real-model',
		repoPath: 'samples/Durable.Examples',
		walkthroughUrl: '/docs/samples/durable-and-extensions/',
		exampleCount: 22,
	},
	{
		id: 'Durable.Examples.160',
		badgeLabel: 'Durable 38–47 · 1.6.0',
		title: 'Signals, Parked Tools, BeforeTool Approval Gates & Hook Tasks (38–47)',
		runtime: 'Durable',
		category: 'Durable, Workflows & Extensions',
		patterns: ['Approval gate', 'Steering & signals', 'Crash & resume', 'Multimodal & media'],
		tools: ['Signals', 'Parked webhook/poll tools', 'BeforeTool gate', 'OnYield/AfterTools HookTask', 'FitImages'],
		summary:
			'10 unreleased 1.6.0 source-candidate examples: typed signals, ask-a-person sleeps, webhook/polling parked tools, parallel children, BeforeTool approval gates, OnYield review tasks and image fitting.',
		experimental: true,
		unreleased160: true,
		demoCommand: 'dotnet run --project samples/Durable.Examples -- --storage sqlite 38 39 40 44 45 47',
		selfTestCommand: 'dotnet run --project samples/Durable.Examples -- 38 39 40 41 42 43 44 45 46 47',
		repoPath: 'samples/Durable.Examples',
		walkthroughUrl: '/docs/samples/durable-and-extensions/',
		exampleCount: 10,
	},
	{
		id: 'Durable.Chord.Examples',
		badgeLabel: 'Chord 35–37',
		title: 'Durable on the Real Chord Host & Shared Store (35–37)',
		runtime: 'Chord',
		category: 'Durable, Workflows & Extensions',
		patterns: ['Crash & resume', 'Hybrid tools'],
		tools: ['StoreStorage', 'ViewBridge', 'RegistryReader', 'Hosts.Create'],
		summary:
			'3 offline SQLite examples sharing an owner/allocator/archive between Chord tables and Durable sessions, projecting typed views into Chord members and composing plugin extensions.',
		experimental: true,
		demoCommand: 'dotnet run --project samples/Durable.Chord.Examples -- 35 36 37',
		repoPath: 'samples/Durable.Chord.Examples',
		walkthroughUrl: '/docs/samples/durable-and-extensions/',
		exampleCount: 3,
	},
	{
		id: 'Durable.ReleaseWatcher',
		badgeLabel: 'Device Client',
		title: 'Durable Release Watcher (Schedules, Policies & Review Loop)',
		runtime: 'Durable',
		category: 'SRE, Security & Governance',
		patterns: ['Approval gate', 'Crash & resume', 'Contradictions & audit'],
		tools: ['pisharp-source-check', 'pisharp-judge', 'pisharp-research', 'DurableDeviceClient'],
		summary:
			'End-to-end durable device client watching release feeds with content-addressed evidence, confidence-routed human review, budgets, feedback and idempotent restarts.',
		experimental: true,
		demoCommand: 'dotnet run --project samples/Durable.ReleaseWatcher -- demo',
		repoPath: 'samples/Durable.ReleaseWatcher',
		exampleCount: 1,
	},
	{
		id: 'Durable.Chat',
		badgeLabel: 'CLI App',
		title: 'Durable Interactive Chat Host',
		runtime: 'Durable',
		category: 'Durable, Workflows & Extensions',
		patterns: ['Crash & resume', 'Steering & signals'],
		tools: ['NativeDurableModels', 'SqliteStorage', 'Durable coding tools'],
		summary:
			'Complete command-line durable chat application over NativeDurableModels with selectable memory, JSONL or SQLite storage and --continue recovery.',
		experimental: true,
		demoCommand: 'dotnet run --project samples/Durable.Chat -- --help',
		liveCommand: 'dotnet run --project samples/Durable.Chat -- --provider openai --model gpt-6.2-sol --storage sqlite --dir ./chat-state --tools',
		repoPath: 'samples/Durable.Chat',
		exampleCount: 1,
	},
	{
		id: 'StatefulTodo',
		badgeLabel: 'C# Extension',
		title: 'StatefulTodo Native C# Extension',
		runtime: 'Extensions',
		category: 'Durable, Workflows & Extensions',
		patterns: ['Session branching', 'Hybrid tools'],
		tools: ['todo (list/add/toggle/clear)', 'IPiSharpExtension'],
		summary:
			'Native in-process C# extension whose todo list is rebuilt exclusively from the active acknowledged /tree session branch so branching never corrupts tool state.',
		demoCommand: 'dotnet build samples/extensions/StatefulTodo',
		liveCommand: 'pisharp -e samples/extensions/StatefulTodo/bin/Debug/net10.0/StatefulTodo.dll',
		repoPath: 'samples/extensions/StatefulTodo',
		walkthroughUrl: '/docs/samples/durable-and-extensions/',
		exampleCount: 1,
	},
	{
		id: 'SessionCheckpoint',
		badgeLabel: 'C# Extension',
		title: 'SessionCheckpoint Commands & Lifecycle Hooks',
		runtime: 'Extensions',
		category: 'Durable, Workflows & Extensions',
		patterns: ['Approval gate', 'Session branching', 'Hybrid tools'],
		tools: ['/checkpoint', '/checkpoint-create', 'session_start', 'RegisterSessionSwitchHandler'],
		summary:
			'Native C# extension registering /checkpoint commands, confirmation dialogs on session switches and creation, and stale-context write protection across replacements.',
		demoCommand: 'dotnet build samples/extensions/SessionCheckpoint',
		liveCommand: 'pisharp -e samples/extensions/SessionCheckpoint/bin/Debug/net10.0/SessionCheckpoint.dll',
		repoPath: 'samples/extensions/SessionCheckpoint',
		walkthroughUrl: '/docs/samples/durable-and-extensions/',
		exampleCount: 1,
	},
	{
		id: 'Chord.NativePlugins',
		badgeLabel: 'Chord Plugin',
		title: 'Chord Native Plugin Generations & Hot Reload',
		runtime: 'Chord',
		category: 'Durable, Workflows & Extensions',
		patterns: ['Hybrid tools', 'Crash & resume'],
		tools: ['IChordPlugin', 'PluginRuntime', 'PluginManager', 'chord.plugin.json'],
		summary:
			'Complete host, shared contracts and collectible Counter plugin demonstrating digest-based reloads, private dependencies and graceful retired-generation draining.',
		experimental: true,
		demoCommand: 'dotnet build samples/chord/NativePlugins/Host/Host.csproj',
		repoPath: 'samples/chord/NativePlugins',
		exampleCount: 1,
	},
];

export const practicalAgentSamples: SampleEntry[] = loadPracticalAgents();

/** Highlighted platform samples first, followed by all 62 practical agents in catalog order. */
export const allSamples: SampleEntry[] = [...platformSamples, ...practicalAgentSamples];

export const sampleStats = {
	practicalAgents: practicalAgentSamples.length,
	durableAndChordExamples: 48, // 00-34 (35) + 35-37 (3) + 38-47 (10)
	durableApps: 2, // Durable.Chat, Durable.ReleaseWatcher
	customAgentApps: 2, // CustomAgents.Examples, CustomAgents.Research
	extensionAndPluginSamples: 3, // StatefulTodo, SessionCheckpoint, chord/NativePlugins
	totalRunnableSamples: allSamples.reduce((sum, item) => sum + (item.exampleCount ?? 1), 0),
	totalCards: allSamples.length,
};

export const sampleRuntimes: SampleRuntime[] = ['Agent', 'Durable', 'CustomAgents', 'Chord', 'Extensions'];

export const sampleCategories: SampleCategory[] = [
	'Productivity & Docs',
	'Support & Commerce',
	'Finance & Ops',
	'Engineering & Maintainers',
	'SRE, Security & Governance',
	'Multimodal & Research',
	'Durable, Workflows & Extensions',
];

export const samplePatterns: SamplePattern[] = [
	'Approval gate',
	'Crash & resume',
	'Subagents & teams',
	'Hybrid tools',
	'Multimodal & media',
	'Contradictions & audit',
	'Session branching',
	'Automated PRs',
	'Steering & signals',
];
