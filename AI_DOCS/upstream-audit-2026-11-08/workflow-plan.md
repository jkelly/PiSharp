# Audit workflow

The source snapshot and frozen refs in README.md are the shared inputs. This is a planning task; implementation packages will remain proposed.

| id | agent_type | complexity | model | reasoning_effort | service_tier | complexity_reason | goal | depends_on | owned_paths | inputs | outputs | verification | status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| A0 | main | hard | gpt-6.1-sol | high | ultrafast | Resolve competing branches and provenance | Freeze refs, snapshot source, initialize audit | none | audit docs and snapshot | repository and GitHub | README, snapshot | exact HEAD and target pins | complete |
| A1 | explorer | hard | gpt-6.1-sol | high | ultrafast | Provider payloads, auth and schema contracts | Audit AI/core changes and C# applicability | A0 | read-only AI, Contracts, Agent, provider tests | frozen refs | provider findings and acceptance cases | caller and test tracing | complete |
| A2 | explorer | very hard | gpt-6.1-sol | xhigh | ultrafast | Durable transactions, persistence and replay | Audit Durable, SQLite and hosting gaps | A0 | read-only Durable packages and tests | frozen refs | durable findings and contract migration plan | source and storage invariant review | complete |
| A3 | explorer | hard | gpt-6.1-sol | high | ultrafast | CLI/tool lifecycle and cross-layer behavior | Audit tools, MCP, sessions, CLI and codemode | A0 | read-only Tools, CLI, CodingAgent, Extensions, Codemode, tests | frozen refs | core/CLI findings and acceptance cases | caller and test tracing | complete |
| A4 | explorer | hard | gpt-6.1-sol | high | ultrafast | Existing deferrals versus claimed completion | Audit v1.5.0 inventory, release gaps, test/build coverage | A0 | read-only compatibility, docs, test/build infrastructure | frozen refs | baseline/coverage findings and commands | reconcile all inventory rows | complete |
| A5 | main | hard | gpt-6.1-sol | high | ultrafast | Avoid missed upstream changes and duplicate findings | Inventory complete commit delta and validate findings | A0 | audit evidence and isolated probes | upstream APIs, independent findings | classified inventory, verification record | frozen comparisons and focused offline checks | complete |
| A6 | main | very hard | gpt-6.1-sol | xhigh | ultrafast | Sequence API, data and integration work safely | Synthesize prioritized, dependency-marked implementation plan | A1,A2,A3,A4,A5 | audit docs | validated findings | gap-audit, contracts, implementation-plan | trace every finding to work package and test | complete |

Main model rows are planning guidance, not a claim that this chat changed models. Requested tier is Ultrafast; effective tier is unverified because the spawn API does not expose tier control.
