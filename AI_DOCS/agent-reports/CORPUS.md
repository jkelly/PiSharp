# CORPUS — full Chord schema, table and job compatibility evidence

## Implemented

- `tools/ChordSchemaFixtures150/capture.mjs` and `worker.mjs`: separate, exact-pin upstream capture tooling, minimal inherited environment, network denied, source/lock/Node/script hashes and original output bytes retained.
- `tests/PiSharp.Chord.Tests/Fixtures/v1.5.0/schema-switch`: original upstream files separately identified as historical evidence plus two complete independent runtime captures.
- `Schema/FullDeclarations.cs`: completes the native schema-switch declarations with `Rows`, table versions `EditsV1/V2/V3`, JSON columns and `ImportJob`.
- `Schema/Proofs.cs`: full contract/document/table/job proof, no longer contracts/documents only.
- `Schema/CorpusCases.cs`: twelve genuine capture-backed checks, including real-host stored-data readers.
- Chord `Program.cs`: labels only `schema.corpus.*` as `PASS_UPSTREAM_CAPTURE`; other authored cases keep their classification.

Original `Fixtures/schema`, old Durable fixtures/pins, and the V04 capture remain unchanged.

## Exact source and actual execution

Upstream checkout `E:/AI/PiSharp/upstream-v150`, Git HEAD `f9a26365568127461c47a07a37dcb42c140a3ac5`, Node v24.22.0. Source evidence is the exact upstream:

- `packages/chord/test/next/schema/schema-switch.test.ts`
- `packages/chord/test/next/schema/{declarations,proofs,owner,write-stored}.ts`
- `packages/chord/test/next/schema/recorded.json` and `stored/{store.db,archive.jsonl}`
- production `packages/chord/src/next/{contracts,documents,jobs,store,host}`

Ran successfully:

```powershell
node tools/ChordSchemaFixtures150/capture.mjs E:/AI/PiSharp/upstream-v150 tests/PiSharp.Chord.Tests/Fixtures/v1.5.0/schema-switch
node --check tools/ChordSchemaFixtures150/capture.mjs
node --check tools/ChordSchemaFixtures150/worker.mjs
git diff --check -- tests/PiSharp.Chord.Tests/Schema/Declarations.cs tests/PiSharp.Chord.Tests/Schema/Proofs.cs tests/PiSharp.Chord.Tests/Program.cs
```

Both independent runs completed. All six JSON/JSONL outputs agree after only explicitly documented persisted-job UUID normalization. Every run executes upstream `fingerprints()` and `storedReads()` and asserts the complete result equals upstream `recorded.json`, including table and job proof omitted by the previous native partial port.

Fresh writes use upstream `ownerHost` and its real method calls. The worker writes three notes, updates persisted singleton/keyed documents and typed table rows, starts three jobs with persisted progress, exports running jobs, cleanly stops them, retains the real SQLite store, then reopens both the store copy and imported archive with table version 3. Both paths return the captured profile, notes, typed/defaulted/optional/JSON-column rows, fully migrated edits, three completed results and the keyed-note read. No expected session, record, archive, row or fingerprint is authored by the capture.

The first preparation attempt checked a JSON delta revision for an inline value and failed; corrected readiness to the actual persisted document revision. Final captures gate each progress commit before the next owner transaction, which gives a fixed valid transaction order without rewriting recorded sequences. Job UUIDs remain genuinely random and are normalized solely by their actual `chord.jobs` document keys, not by a general UUID regex.

## Native comparison and coordination

The new native readers use the production host, state handles, `self.Job`, store tables, table migrations and SQLite adapter. They run on temporary copies of both historical and fresh input stores, and import the corresponding genuine running archives. The comparisons cover complete fingerprint/normal-form objects and full post-resumption read results. A separate check exports the fresh stopped SQLite store and compares the actual upstream archive, without the older test's authored close rewrite.

J1 supplied the current JobDefinition/state/host API and persistence contracts. X1 supplied its real-host/table/store setup. No changes were made to their runtime sources or integration test projects.

I13's centralized `CORPUS-runtime3` validation passed on 2026-11-08 at 23:07 UTC. Inspected the actual receipt, build output, test output and source snapshots:

- [Receipt](../../artifacts/gap-validation/CORPUS-runtime3-2fde54dd11984d8c808afe28186ffde0/receipt.json)
- [Build output](../../artifacts/gap-validation/CORPUS-runtime3-2fde54dd11984d8c808afe28186ffde0/build.stdout.log): Debug build succeeded with **0 warnings and 0 errors**. The central command disabled analyzers and shared compilation; this does not claim an analyzer or Release build.
- [Actual case output](../../artifacts/gap-validation/CORPUS-runtime3-2fde54dd11984d8c808afe28186ffde0/tests.stdout.log): **12 selected, 12 executed, 12 `PASS_UPSTREAM_CAPTURE`, 0 failures, 0 skips** under `--only schema.corpus.`. The twelve cases are two integrity/reproducibility checks, two complete fingerprint proofs, two stopped-SQLite export comparisons, four fresh-store/archive host readers across both captures, and two original historical store/archive host readers.
- Build/test processes both exited 0; both owned process jobs were empty and their parents joined.

The receipt's before/after source fingerprint is identical:
`5d8548bca9ff3dcfadc15934084ad589fe29da87dd731732e943ad045f8f02ac`, with `changedDuringRun: false`. The two source snapshot files are also byte-identical. A read-only recheck of all 356 scoped files after inspecting the receipt found no runtime/test source changes. Only `src/PiSharp.Contracts/packages.lock.json` and `src/PiSharp.Telemetry/packages.lock.json` had since changed physical line endings from LF to CRLF; normalizing those two files back to LF reproduces their exact receipt hashes. No package/dependency content changed in them. This recheck ran no build or tests.

Existing older schema-switch tests remain independently executable under `--only schema.schema-switch.`. The separate V04 Durable capture test project remains a distinct I13 validation gate; this Chord receipt does not qualify it.

## Limits

This completes the specified schema-switch declaration/store/job corpus, not every Chord jobs or tables scenario. It covers rerun-resume and table version migration, not abandon policies, adapter-version migration, restart exhaustion or every host reload race. Those remain in the separately authored/runtime test groups. No live network, provider credentials, model calls or paid services were used. Requested Ultrafast execution tier remains unverified.
