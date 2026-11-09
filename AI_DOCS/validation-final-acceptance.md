# Final local validation acceptance

Actual Windows x64 execution with SDK10.0.401, dirty checkout14dd34b369fba6d236684da6dfc8a39a03263321. Exact before/after manifests, raw logs and prerequisite stages remain attached to each receipt. Counts overlap and are not summed. Machine details: `validation-final-acceptance.json`; complete history: `validation-status.json`.

| Gate | Actual result | Source qualification |
| --- | --- | --- |
| Compatibility |236/236 passed | Final shared reader, captures and provider fixes; stable |
| Transport |245/245 passed | Final code; stable |
| Schema |34/34 passed | Final Contracts; stable |
| Threads / Simple |4 observed groups /13 cases passed | Full projects; stable |
| Responses transcript / Azure / Provider APIs |1 console group /17 /60 passed | Full projects; stable; transcript group includes18 placement assertions |
| Provider offline |30 native contracts passed |29 source-derived matches;1 explicit native-policy difference retained; original upstream not executed |
| Full Chord |875/875 passed | Final current Contracts/AI closure; stable |
| Legacy cluster |25/25 passed | Stable tested source; no Unix/physical claims |
| Hosting |14/14 passed | Own sources/tests stable; five unrelated Anthropic files and five lock files changed during run |
| RPC |137/137 passed | Stable tested source |
| Settings / original compaction+mapping |2 /16 passed | Lock-only / friend declaration plus locks; exact deltas recorded |
| Overlay |5/5 passed | Includes real Node bounds/focus and deterministic composition controls |
| Tools |164 wrapper groups passed | Symlink branch unavailable: explicit IOException observation; that branch is unqualified |
| Windows NativeAOT |Publish + verified notice bundle +9 smoke checks passed | Exactly ten RID-dependent lock changes, no code changes; current binarySHA25625c85ef42b715051196bd90780b2b52ea197f615facb61802a91ef4380190c87 |
| Normal Release artifacts |29 packages built and validated | Analyzers enabled; final source |
| External package-only consumer |5 nupkgs admitted; negative diagnostics, opt-in build and actual run passed | Fresh outside-checkout cache; exact source hash1b14874943524f66b87ece8e0cfaa79e7f59b520dfaafc4b2632fd9f9d75e05c unchanged |
| WebAgent / Evals normal Release |Both builds passed, zero warnings/errors | Unpacked production tools; analyzers enabled |
| Production installer |1 selected case passed, zero skips |123 actual packages installed in fresh private cache; registry integrity and provenance verified; unrelated provider serializer drift recorded |
| Validation tooling |28 bounded-runner controls,19 local evidence controls,3 manifest tests passed | Nested markers are observations, not extra cases; root final10 structural controls passed with zero skips |

No required local gate remains failed. Earlier failures, interrupted runs, lock prerequisites and parser-recovered evidence remain in the history. I18 measurements are separately validated artifacts, not test cases; latency injection and Windows timer scheduling prevent interpreting the timings as raw SQLite throughput.

Hosted three-OS jobs have not executed here. Unix native behavior, physical macOS modifier input, the unavailable symlink branch, separate native source admission and publication are not established by these local results. No publication occurred.
