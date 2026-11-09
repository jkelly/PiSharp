# Verification record

Frozen product: `14dd34b369fba6d236684da6dfc8a39a03263321`. Frozen upstream: `7d073328e44337d3292a7db9b0f0308f679c5e97`. Date: 2026-11-08. This record describes actual audit execution, not future acceptance.

## Executed

| Check | Actual result | Evidence |
|---|---|---|
| Product identity | GitHub main SHA matched local origin/main; git archive produced an isolated snapshot | README.md; project-map.md |
| Upstream inventory | 4 compare pages, 326 unique commits; 2 full Git trees, neither truncated; 2,935 changed blob paths | [inventory](evidence/upstream-inventory.json) |
| Existing schema suite | 23 grouped authored-native cases, 0 failures; not new upstream captures | [schema-baseline.json](evidence/schema-baseline.json) |
| Focused core probes | 9 observations: 3 positive controls pass; 6 mismatch observations across 5 defects | [gap-probes.json](evidence/gap-probes.json) |
| SQLite failure-classification probe | Completed against actual in-memory SQLite; valid JSON invalid record shape caused Stored=1, FormatException refusal, receipt 0 and persisted first entry | [sqlite-probe.json](evidence/sqlite-probe.json) |
| Existing offline applicability tests | 3 pass, 0 fail, 1 explicitly skipped | [inventory-check.log](evidence/inventory-check.log) |

The six reproduced defect categories are C01 relative schema ID, C02 Anthropic definitions, C05 mixed line endings, C08 prompt argument parsing (two observations), C09 referenced tool-search terms, and C03 SQLite failure classification.

## Probe detail

- Schema positive control omits root ID and accepts a valid local reference. Adding relative ID produces `https://json-schema.invalid/schemas/schemas/tool.json` and throws. Existing 23-case suite still passes.
- Prompt `doesn't work` becomes `["doesnt work"]`; unmatched quote is lost. Properly quoted two-word control is preserved.
- Direct tool-schema description is indexed; the same description behind a local reference is not.
- Editing second line of `first\\r\\nsecond\\nthird\\r` normalizes untouched endings to CRLF.
- Non-strict Anthropic generated input schema drops a referenced root definition.
- First SQLite setup attempted syntactically invalid JSON `{`; SQLite correctly rejected it with CHECK(json_valid(record)). Revised setup used syntactically valid `{}` missing the required record fields. This simulates corrupted/incompatible stored shape, not ordinary writes. It demonstrates C# IStorage error classification; no exact upstream JavaScript result for this invalid-shape fixture is asserted.
- Probe code: [core](evidence/probes/CoreGapProbes.cs), [SQLite](evidence/probes/SqliteGapProbe.cs). They are test snippets, not product edits.

## Environment and commands

The global SDK does not satisfy the pinned 10.0.401/rollForward=disable. The existing exact SDK used was:
`E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe`.

All builds ran in `E:/AI/PiSharp/audit-2026-11-08`, outside the active Git checkout. DOTNET_CLI_HOME used the isolated audit directory. Six already-installed SQLite package versions were copied to an isolated cache; no external package download or version update was needed.

Commands actually executed (paths abbreviated only here):

```powershell
dotnet run --project source/tests/PiSharp.Schema.Tests/PiSharp.Schema.Tests.csproj -- --report schema-baseline.json
dotnet run --project probes/GapProbes.csproj
dotnet run --project sqlite-probe/SqliteProbe.csproj
node --test source/tools/CompatibilityReport/sync-applicability.test.mjs
```

The first SQLite fixture failure was corrected before the final evidence was saved. Focused probes report observed versus desired behavior; their process exit code 0 means the probe completed, not that the desired product behavior passed.

## Not executed / limits

- Full solution, all 22 release suites, all ten core suites, three-OS matrix, browser/native terminal tests and SQL Server integration were not executed.
- No live model or OAuth endpoint, real credential rotation, SSH target, production database or paid service was used.
- Upstream-aware applicability test skipped because a local Pi Git checkout with v1.2.0 and v1.5.0 was not supplied. Full remote tree/commit inventory was independently obtained, but it is not falsely substituted for execution of that test.
- Provider server rejection scenarios, OS-specific behavior, memory benchmarks and shutdown concurrency remain source-based findings with proposed fake/loopback or platform tests.
- No full differential upstream v1.5.0 runtime corpus was created. Such evidence is a planned gap.
- No product source, compatibility lock, release version, package dependency or historical fixture was changed.
