# CLI2 late macOS parity corrections

Status: test edits complete; root owns all .NET validation. CLI2 ran no builds/tests or Git/remote commands and changed no product sources, captured scenario files, website files or changelog. Requested hard/high sol worker, ultrafast; effective tier unverified.

Evidence: actual `artifacts/release-prep/macos-42544805821/report-PiSharp.CliParity.Tests/PiSharp.CliParity.Tests.json` (14 failures among 220 cases) and `report-PiSharp.InteractiveParity.Tests/PiSharp.InteractiveParity.Tests.json` (one failure among 723), indexed by `artifacts/release-prep/macos-late-triage.json`. Original run is 42544805821 at 3024b1e0; no candidate passing result is inferred.

## Every failure disposition

| Actual failed case | Correction and retained controls |
|---|---|
| `entry.initial-message-merge-and-remaining-messages` | Enabled threads now require create then two continuations, each referencing its unique preceding fake response and carrying exactly its ordered new prompt. Continuations omit inherited tools/system. A separate explicitly disabled run still requires full request message counts 1/3/5 and identical final output. Merge/remaining-message assertions remain. |
| `validation.read-runs-with-coerced-arguments-and-the-session-keeps-the-model-arguments` | Explicit full-transcript fixture preserves exact replayed string arguments, persisted original arguments and the real read's coerced numeric offset/limit and output. |
| `tools.nameless-call-gets-tool-not-found-persisted-and-replayed` | Explicit full-transcript fixture preserves exact nameless call/error replay, persisted result, and assertion that secret file contents never reached the provider. |
| `tools.id-less-nameless-calls-share-the-empty-id` | Same full-transcript fixture, preserving both calls/results and their shared empty ID in exact order. |
| `tools.id-less-read-runs-and-its-result-keeps-the-empty-id` | Full-transcript fixture preserves actual file read, empty call/result ID, persisted result and exact replay. |
| `tools.two-id-less-reads-both-run-and-results-follow-call-order` | Same fixture retains both actual file reads and exact call/result order, content and empty IDs. |
| `json-fidelity.lone-surrogate-names-travel-through-rpc-tool-arguments-and-session` | Full-transcript fixture preserves every original byte assertion for lone surrogate names/values in RPC events, tool input, session and reopened request. |
| `json-fidelity.deep-values-pass-rpc-tool-arguments-and-sessions` | Full-transcript fixture retains 900-level arguments, 990-level command acceptance, 1,001-level rejection, actual write, persisted value and whole replay after reopening. |
| `resume.restores-the-branch-model-or-falls-back-like-sdk` | Exact reviewed fallback is `anthropic/claude-opus-5-5`; request model is checked too. Available branch/explicit CLI model, no-auth alternate provider, unknown model, refused prompt and no spurious model-change checks remain. |
| `resume.switch-session-falls-back-to-find-initial-model` | Exact reviewed fallback changes to `claude-opus-5-5`; subsequent available Haiku restoration remains required. |
| `switch.sessions-without-messages-choose-their-model-like-find-initial-model` | Exact unscoped default changes to `claude-opus-5-5`; scoped/saved/explicit choices remain independently checked for new and missing sessions. |
| `thinking-clamp.resume-opens-at-the-clamped-level-and-later-turns-run` | Opening still writes nothing. First real prompt must checkpoint exactly one low selection before both turns; original off entry remains. The next reopened explicit low selection is inert, high is recorded once, and its turn runs. |
| `session-format.resume` | Preserve captured fixture bytes. Apply an explicit authored C13 expectation: insert a low checkpoint immediately before the second user and update that user's parent. Exact checkpoint, chain, all remaining lines, JSON.stringify and request route checks remain. |
| `session-format.resumemodel` | Same narrow C13 delta with high. The third resumed assistant now records high restored from that acknowledged checkpoint, rather than historical unpersisted low. No actual entry is ignored. |
| `e2e.resume-reload-and-turns-on-a-clamped-thinking-level` | Startup and reload must preserve original session bytes. The real prompt must append exactly off→low thinking history, with low before its user entry, while retaining earlier bytes, live low state and the answer. |

## Contract/source checkpoint

The release baseline remains upstream `f9a26365568127461c47a07a37dcb42c140a3ac5`; reviewed gap source is frozen at `7d073328e44337d3292a7db9b0f0308f679c5e97`. `AI_DOCS/upstream-audit-2026-11-08/contracts.md` requires effective live thinking selection to be recorded before branch prompt execution. C13 traces to upstream `7860ff52892c1a9f0b017be78fc75dd30b5d5c98`.

- `AnthropicMessageThreadStore.Prepare` removes inherited system/tools/messages only for a valid continuation; direct Anthropic threads default on, and `supportsMessageThreads:false` is an explicit supported opt-out. The eight legacy replay controls now opt out individually through `models.json`, and their endpoint rejects any thread field or thread beta. All unrelated fixtures retain their default behavior. This does not weaken the original replay assertions or invent inherited content.
- `ModelResolver.DefaultModelPerProvider` explicitly names `anthropic/claude-opus-5-5` in the reviewed F09 map.
- `PersistentAgentSession.InitialThinking` keeps opening read-only; `RecordLiveThinkingAsync` checks branch level against effective level, appends/acknowledges a checkpoint only on divergence, and runs before prompt execution. Tests preserve both halves of this contract.
- Session-format expected changes are clearly authored deltas in the comparison layer, like the existing Pi 1.2/1.5 text migrations. Historical captured JSON files remain untouched; no broad normalization, skipped case, ignored checkpoint or recapture claim was introduced.

Product source SHA256 at CLI2 review checkpoint (these files were not edited by CLI2):

| Source | SHA256 |
|---|---|
| `src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessageThreads.cs` | `a1176415eb8b68c197bea2d5e87363f825c4abf95dbe7112333ba6c9310df210` |
| `src/PiSharp.ModelRuntime/Models/ModelResolver.cs` | `d98fe91ece87bac7227d4c080da71c2c61a0933d09fe2aaf8d20c5c59d5cbc21` |
| `src/PiSharp.CodingAgent/PersistentAgentSession.ThinkingPersistence.cs` | `fb5a0f813acb21d475cd439c7022e7c0b4bad1574237dff8d29145f18ca1febe` |
| `src/PiSharp.CodingAgent/PersistentAgentSession.InitialThinking.cs` | `3f3bc2befd29f532291507a39d8c5708dd731047cfe67943c33c071899c7aab4` |

## Owned changes and validation handoff

Changed `tests/PiSharp.CliParity.Tests/{Program,EntryCases,ValidationCases,NamelessToolCallCases,JsonFidelityCases,ResumeModelCases,SwitchPathCases,ThinkingClampCases,SessionFormatCases}.cs` and `tests/PiSharp.InteractiveParity.Tests/InteractiveModeCases.cs`, plus this report. No case registration/filter changes.

Root should build both projects and run full CliParity (220) and InteractiveParity (723), retaining existing explicit platform skips. Focused prefixes for fast diagnosis: `CLIPARITY_FILTER=entry.initial`, `validation.read-runs`, `tools.`, `json-fidelity.`, `resume.`, `switch.sessions-without`, `thinking-clamp.resume`, `session-format.resume`; `INTERACTIVEPARITY_FILTER=e2e.resume-reload-and-turns-on-a-clamped-thinking-level`. Root separately owns the current `changelog.sync` verification. Hosted macOS rerun remains necessary; this worker claims source review only, not passing execution.

## Root validation and final CR fixture correction

Root's actual Windows full CliParity receipt `artifacts/gap-validation/release-cli-parity-afc303321c724cd8a7f99e37900cec19/tests.stdout.log` confirms that all 14 original failures now pass. The remaining failure is `sync150.grep-lone-cr-context.real-ripgrep-line-numbers`: the old assertion requires `beforeprogress`, but the actual PiGrepTool output preserves `before\rprogress` on line 1, with the match on line 2 and trailing context on line 3. Root separately reports full Tools 164 passing; this is not a new worker-run claim.

Source confirmation: the reviewed C06 bounded-context implementation uses `rg --context` JSON records directly (`PiGrepTool.SearchAsync`), stripping a trailing CR only when it belongs to a trailing CRLF. Interior CR remains in the original ripgrep line. The audit identifies C06 as upstream `8da141c2ad8f9e9b5e9d3e42d78c4ed90c4fe125`, reviewed at `7d073328`; the former generic GrepTool's fake-context tests still describe their separate legacy reader path. The production PiGrepTool contract therefore supports the preserved byte, not global CR deletion.

Root authorized a final narrow `tests/PiSharp.CliParity.Tests/Sync150Cases.cs` correction. Its header now explicitly labels this expectation as an authored postbaseline C06 delta. The real-rg assertion compares the complete three-line output, retaining the lone CR and exact path/line-number separators. A paired CRLF control requires all delimiter CRs removed, still exactly three numbered lines. Product source and legacy test expectations are unchanged. Proposed final root selection: `CLIPARITY_FILTER=sync150.grep-lone-cr`. No .NET execution by CLI2.
