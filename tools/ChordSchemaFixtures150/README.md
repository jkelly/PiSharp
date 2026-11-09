# Complete Chord v1.5.0 schema-switch corpus

This independent capture executes the exact upstream schema-switch proof, including contracts, documents, all four table definitions and the persisted job. It also writes fresh SQLite and JSONL stores through upstream's real host, then migrates the table and resumes the persisted jobs. Existing `Fixtures/schema` files are preserved.

```powershell
node tools/ChordSchemaFixtures150/capture.mjs E:/AI/PiSharp/upstream-v150 <new-output-directory>
```

The checkout must be unmodified at `f9a26365568127461c47a07a37dcb42c140a3ac5`, with lockfile dependencies already installed. The output directory must not exist. No package installation, provider or network access is performed. The driver passes only a minimal environment allowlist and emits local schema/telemetry JavaScript with the lockfile's TypeScript compiler. The worker denies `fetch` before upstream imports.

The driver copies the three original upstream artifacts into `historical/` and executes two independent captures. In each run:

1. Invoke upstream `proofs.fingerprints()` and `proofs.storedReads()`, and assert the **entire** result equals original `recorded.json`.
2. Invoke upstream `ownerHost` with table version 1. Write the three notes through its real contract method, gating each job's persisted progress before the next write.
3. Export `running.jsonl` while jobs are running, close the host, export `stopped.jsonl`, and retain the closed `store.db`.
4. Reopen a SQLite copy using table version 3 and finishing jobs. Separately import the running archive and perform the same reopen. Capture the full native results and post-resumption archives.

`recorded.json` is a newly executed complete proof; `fresh-read.json` is the fresh store and archive's observed result. The original files in `historical/` are visibly copied evidence, not newly written evidence. The manifest identifies both categories, hashes every file and records all input source hashes, dependency lock, Node executable and scenario/driver hashes.

Only exact UUID strings appearing as persisted `chord.jobs` document keys are normalized, in creation order, across comparison outputs. Every other identifier, schema, fingerprint, row, document revision, sequence, state, outcome and field order is preserved. Raw files retain original UUIDs. SQLite page layout is not compared; every semantic row/document is captured in the corresponding archive and file bytes are hashed. Both runs must have identical output inventories and six normalized JSON/JSONL outputs.

`tests/PiSharp.Chord.Tests --only schema.corpus.` checks the complete native declaration proof, imports/exports real storage, and opens original and fresh stores through the real C# host. The native reader migrates the table to version 3, loads singleton/keyed documents, reruns stopped/interrupted jobs and compares full observed results with upstream. Integrity and independent-run checks also execute there. Only these new cases receive `PASS_UPSTREAM_CAPTURE`; other existing Chord tests retain their authored status.
