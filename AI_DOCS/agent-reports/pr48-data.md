# PR 48 data review: samples 53–62

Status: review complete; two confirmed findings fixed and verified locally. No remaining confirmed finding in this scope. Reviewed base: `34f19a66fa547e8a12cc4aef025cbd44635ce740` in `E:/AI/PiSharp/agent-samples-pr`, 2026-11-01.

Assignment: `pr48-data`, very hard, assigned `gpt-6.1-sol` / `xhigh`; requested service tier `ultrafast`, effective tier unverified because the delegation surface has no tier control. Initial review was read-only. The main agent subsequently authorized the two narrow fixes below. No live API calls, full-solution build, commits or pushes were performed.

## [P2] Reusing a completed merge ID overwrites the first group's archived originals

Original locations at reviewed head: `samples/agents/57-DuplicateRecordResolver/Workflow.cs:87–95` and `:137–138`.

`Build` checked a history entry only for an identical-request replay. An existing ID with a different request fell through into ordinary planning. `Apply` then replaced `mergeHistory[merge.Id]`, permanently removing the earlier group's original-record archive after its duplicate had already been removed from the current records.

Confirmed using two approved, offline native demo runs with the pre-fix compiled DLL and one workspace:

1. Use the standard fixture records and only the `merge-alex` request (`c1 <- c2`). The run exits 0 and stores originals `c1,c2` under that ID.
2. Supply the surviving source record inventory and the standard Jordan request (`c3 <- c4`), but reuse ID `merge-alex`. The run also exits 0.
3. The history still contains one entry, now with originals `c3,c4`; `c2` is absent from both current records and the archived originals. Both valid merge groups were accepted, but the first group's provenance was lost.

Reproduction artifacts: `C:/Users/jksof/AppData/Local/Temp/pr48-data-eb07620610b642a2ac7c240489edbd0f/first.json`, `second.json`, and `merge/customer-state.json`.

Fix: reject an existing history ID whose stored request differs before producing a new plan or mutation. Identical replay remains supported. Current guard: `Workflow.cs:88–89`. Added the two-group/reused-ID regression in `SelfTests.cs:42`, asserting unchanged store bytes, retained `c4`, and retained original `c2` provenance. README now states that completed IDs cannot be reused for different requests.

Post-fix native reproduction: the first run exits 0, the second exits 1, history remains `c1,c2`, and `c4` remains in the current records. Evidence is in the same temporary root under `merge-fixed/`.

## [P2] Adjacent redaction markers can recreate a detected literal and reject a valid document

Original locations at reviewed head: `samples/agents/61-DocumentRedactionAssistant/Workflow.cs:73–86` and `:92`.

The marker fallback examined each marker in isolation. For valid plain text `][][` with the supplied sensitive literal `][`, detection produces two adjacent spans. Neither individual `[REDACTED]` marker contains `][`, so the normal marker was retained. Joining the replacements produced `[REDACTED][REDACTED]`, which recreated `][` at their boundary. Final verification then rejected the document during `detect_sensitive`; no preview/export could succeed. This is within the documented arbitrary supplied-literal contract and is distinct from the previously fixed literal-inside-marker case.

Confirmed using the pre-fix compiled DLL with `--demo --input redaction.json`: exit 1; first tool error `Sanitization verification failed; a detected value remains.` Reproduction fixture and native report are under the same temporary root in `redaction.json` and `redaction/`.

Fix: construct the complete substituted text first; if it contains a detected value, retry using the deterministic source-absent single-character marker, then retain full final verification. Current implementation: `Workflow.cs:74–91`. Added an adjacent-span regression in `SelfTests.cs:52` that verifies both preview text and exported readback equal `██`. README now documents replacement-boundary collisions.

Post-fix native approved reproduction exits 0, completes all four tools and exports `██`; evidence is under `redaction-fixed/`.

## Coverage and verification

Read the final PR 48 review instructions, sample contracts, all ten workflow implementations, all ten self-test implementations and their README contracts. Examined malformed input handling, approval callback guards, selected-ID binding, plan freshness, replay, retained originals, privacy and source-linked evidence. Intentional local algorithms and fixture-only effects were treated as scope boundaries, not missing integrations. No other concrete defect was confirmed in samples 53–56, 58–60 or 62.

SDK: `E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe`.

- Focused builds of 57 and 61 with `--no-restore -p:BuildProjectReferences=false --nologo -v:minimal`: both pass, zero warnings/errors.
- Offline self-tests using each existing compiled DLL for all samples 53–62: all pass. Counted suites: 53 = 7/7; 54 = 8/8; 55 = 6/6; 56 = 8/8; 57 = 9/9. Suites 58–62 report one aggregate successful result each, covering their individual assertions and native fixture paths.
- Both concrete reproduction sequences were repeated against the fixed binaries, with the expected rejection/preservation and successful sanitized export described above.
- Scoped `git diff --check`: passes.

Changed files: only the Workflow, SelfTests and README files for samples 57 and 61, plus this report. The existing modified workflow plan and other agents' work were preserved.

Blockers: none. Main agent owns integration review, broader CI and any merge decision. This is a focused correctness review, not an exhaustive proof or production security audit.
