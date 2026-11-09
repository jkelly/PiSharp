# Independent audit reports — consolidated

All explorers used the frozen source snapshot and exact upstream refs from README.md. Requested model was gpt-6.1-sol; A1/A3/A4 high reasoning, A2 xhigh. Requested service tier Ultrafast; effective tier unverified because the tool exposes no tier control. Explorers performed read-only inspection; main created the reports and probes.

| Work item | Completed scope | Main accepted findings | Important exclusions / qualifications |
|---|---|---|---|
| A1 providers/schema | Payload projectors, schema resolver, simple budgets, catalogue and retry metadata | C01/C02/C17–C21; F05/F08/F09 | JS lazy imports not mechanically applicable; tool-ID shortening was reverted; server-side failure not live-retested |
| A2 Durable/storage | Settings, transaction reads, compaction, request checkpoints, SQLite batching/cache and host consumers | C03/C04; F01–F04; identity/hooks and Chord host debt | Existing caches/events intentional. SQL invalid syntax constrained; valid invalid shape probe is C# contract evidence, not exact upstream fixture equivalence |
| A3 CLI/tools | File tools, prompt/text handling, sessions, MCP auth/framing, shutdown ownership, update/auth UX | C05–C16/C22; F06/F07 | Existing caps, callback disposal, provider-token transmission and diff-before-write not missing; unverified UI/concurrency candidates retained explicitly |
| A4 parity/tests | Full baseline inventory counts, deferrals, workflow coverage, tests and release evidence | C23; B01–B06; V01–V05 | No unported row assigned to released 1.5.0. Old citations not missing ports. Core-suite CI omission is coverage risk, not observed test failure |

A4 independently ran the offline applicability suite: 3 passes, 1 prerequisite skip. Main repeated it and preserved output. Main ran schema and focused probes; see verification.md. No explorer reported an unresolved blocker to completing the audit or plan.

Follow-up qualification for C03: SQL schema CHECK enforces syntactic JSON, but not the record's C# shape. Main verified the latter case with actual SQLite. Prioritize ordinary-path tool/input/settings defects ahead of this corrupt-store path; fix its storage error contract independently of the larger batching optimization.
