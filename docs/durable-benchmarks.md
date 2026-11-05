# pi-durable benchmarks: PiSharp and upstream

Report only. These numbers are a snapshot of one machine; nothing in CI passes or fails on them. The `benchmarks.smoke` case in `tests/PiSharp.Durable.Tests` runs every benchmark at a tiny size so the ports keep working, and asserts completion only.

PiSharp ports eleven upstream benchmarks of `pi-durable` in `benchmarks/PiSharp.Durable.Benchmarks`: ten that Pi v1.2.0 (`06a466935536f8c9bcacb2d5efc0fc021be1bdff`) has, ported then, and `conversation-view-bench.ts`, new in v1.5.0. The Pi v1.5.0 sync checked them against v1.5.0 (`f9a26365568127461c47a07a37dcb42c140a3ac5`), which their citations name, and added three PiSharp-authored ones. They run over the storage benchmark kit `src/PiSharp.Durable/Testing/StorageBenchmark.cs` (upstream `src/testing/storage-benchmark.ts`):

| upstream | PiSharp | what it measures |
|---|---|---|
| `test/storage.bench.ts` (`npm run bench:storage`) | `StorageBench.cs` (`storage`) | read, write and reopen latency of memory, SQLite, the store and JSONL storage |
| `test/storage-memory.ts` (`npm run bench:storage:memory`) | `StorageMemory.cs` (`storage-memory`) | heap, RSS and disk footprint after the seed, one child process per backend (memory, SQLite, the store and JSONL) and scale |
| `test/tool-output-bench.ts` (`npm run bench:tool-output`) | `ToolOutputBench.cs` (`tool-output`) | the tool task, progress throttle and `pi.live` commits at low, normal and high output rates, the 1 GiB firehose and `cat` |
| `test/document-append-bench.ts` (`npm run bench:document-append`, new in v1.2.0) | `DocumentAppendBench.cs` (`document-append [N,...]`) | one commit per appended item: one array in one document against one family member per item |
| `test/session-document-cache-bench.ts` (`npm run bench:document-cache`, new in v1.2.0) | `SessionDocumentCacheBench.cs` (`document-cache [N,...]`), with `HeapRetained.cs` (`test/heap-retained.ts`) | the Session document cache at four retentions: cached documents, retained bytes per conversation and the cost of reading an old conversation's document again |
| `test/scheduler-memory-bench.ts` (`npm run bench:scheduler-memory`, new in v1.2.0) | `SchedulerMemoryBench.cs` (`scheduler-memory [N,...]`) | the scheduler's ownership index sizes and retained bytes after N ended subagent cycles under a live parent, and the storage reads a later message into an old subagent conversation costs |
| `test/scheduler-fanout-bench.ts` (`npm run bench:scheduler-fanout`, new in v1.2.0) | `SchedulerFanoutBench.cs` (`scheduler-fanout [N,...]`) | one tool making N parallel nested calls (`ExecuteToolAsync`, progress off) on memory storage |
| `test/nested-tools-bench.ts` (new in v1.2.0) | `NestedToolsBench.cs` (`nested-tools [streaming\|nesting]`) | commits, bytes written by write type, stored size and heap of 200 to 2,000 streaming or fast nested calls, at depth 1 and 2 |
| `test/nested-images-bench.ts` (new in v1.2.0) | `NestedImagesBench.cs` (`nested-images [count]`) | 1 MiB images passed between nested calls inline or by a `NestedCallDoc` reference |
| `test/codemode-bench.ts` (`npm run bench:codemode`, new in v1.2.0) | `CodemodeBench.cs` (`codemode [N,...]`) | durable code mode's overhead: N calls of a trivial tool, sequential or parallel, from a code mode script (journaled) and from a plain tool's nested calls, on SQLite |
| `test/conversation-view-bench.ts` (new in v1.5.0) | `ConversationViewBench.cs` (`conversation-view [sqlite\|store\|both]`) | starting, resuming and keeping watches of a conversation of thousands of entries: time, window entries and heap, on SQLite and the store |
| none: PiSharp-authored, Node twin `benchmarks/PiSharp.Durable.Benchmarks/node/scheduler-scale-bench.ts` (fan-out and chain only) | `SchedulerScaleBench.cs` (`scheduler-scale [fanout N,...] [chain N,...] [conversations N,...]`) | the scheduler's fan-out (one tool creating N child tasks at once), owned chains of N tasks settling bottom-up, and one-tool runs while the scheduler keeps N idle conversations' context ranges |
| none: PiSharp-authored, for Pi v1.5.0's partial commits (upstream bounds the same path in a test) | `PartialCommitsBench.cs` (`partial-commits [N,...]`) | the partial commits of one long streamed answer, a text and then a tool call of about N characters each: commits, bytes allocated and time per phase |
| none: PiSharp-authored, for Pi v1.5.0's group commit (upstream measured it with an uncommitted probe, `docs/group-commit.md` section 9) | `StorageBench.cs` (`storage-throughput [latency ms,...] [conversations N,...]`) | commits per second of one Session and runs per second of one Harness with N conversations working at once, over memory storage behind a fixed round trip and over SQLite, with the storage calls, the commits each call carried and the Storage reads per commit or run |

Run them with `dotnet run -c Release --project benchmarks/PiSharp.Durable.Benchmarks -- [storage] [storage-memory] [tool-output] [document-append [N,...]] [document-cache [N,...]] [scheduler-memory [N,...]] [scheduler-scale [fanout N,...] [chain N,...] [conversations N,...]] [scheduler-fanout [N,...]] [nested-tools [streaming|nesting]] [nested-images [count]] [codemode [N,...]] [partial-commits [N,...]] [conversation-view [sqlite|store|both]] [storage-throughput [latency ms,...] [conversations N,...]]`. There is no benchmark library and no new dependency. The timing loops mirror upstream's.

The store backend (`store`) is Durable's storage on Chord's store over one SQLite file (StoreStorage, `PiSharp.Durable.Store`, which the v1.5.0 sync added). `storage` and `storage-memory` gained it in that sync, after the storage latency and footprint tables below were measured on 2026-10-13, so those tables have no store rows yet. `conversation-view` measures it in its own table; with no backend argument it reads upstream's `PI_DURABLE_BENCH_STORAGE` (`store`, else SQLite).

## Machine

- AMD Ryzen 9 9950X3D (16 cores, 32 threads), 96 GB RAM, NVMe SSD.
- Windows 11 Pro 10.0.26300 (x64).
- .NET SDK 10.0.400, runtime Microsoft.NETCore.App 10.0.11, Release build, workstation GC.
- Node.js v22.18.0 (its built-in `node:sqlite`), running upstream from source with `--conditions=source --experimental-strip-types`.
- The bash tool runs Git for Windows bash.
- Measured on 2026-10-13, one run each, with other work on the machine. Treat differences under about 30 % as noise. The tool-output rows are single samples.

## How the upstream numbers were taken

- **`storage-memory.ts` and `tool-output-bench.ts`** ran unchanged under Node, from `packages/durable`, with `TEMP` pointing at a scratch directory.
- **The `cat` scenario.** On Windows, upstream's `tool-output-bench.ts` joins the 1 GiB file's path with backslashes, and bash reads those as escapes. Its `cat` therefore fails at once (88 to 313 ms, about 1 KiB of output in the unchanged run). The `cat` rows below come from a copy of the script whose only change is forward slashes in that path. They are the first of three runs; the three agreed within 15 %. PiSharp's port names the file with forward slashes on every OS.
- **`storage.bench.ts`** needs Vitest, which the scratch checkout does not install. It ran under a stand-in for Vitest's `describe`/`bench`/`afterAll` with tinybench 2's loop, the same loop as `BenchLoop.cs`:
  - warm up for at least `warmupTime` and `warmupIterations` runs;
  - then sample until at least `time` ms and `iterations` runs have passed;
  - interpolate quantiles linearly.

  Upstream's options are unchanged: reads 300 ms and 10 runs after a 75 ms and 3-run warmup; writes 20 samples after 5 warmups; reopen 20 after 3.

## Storage latency (`storage.bench.ts`)

Mean milliseconds per operation on the timing scale (1,000 entries, 300 tasks, 300 documents, 1,024-delta replay tails and an 8-deep fork chain). Each scenario's result is checked before it is timed. Write samples are 20 single operations, so their means are noisy.

| scenario | backend | Node mean ms | PiSharp mean ms | PiSharp / Node |
|---|---|---:|---:|---:|
| exact entry lookup | memory | 0.0011 | 0.0002 | 0.18 |
| exact entry lookup | sqlite | 0.0126 | 0.0114 | 0.90 |
| exact entry lookup | jsonl | 0.0008 | 0.0001 | 0.13 |
| entry page scan (100) | memory | 0.0190 | 0.0019 | 0.10 |
| entry page scan (100) | sqlite | 0.1465 | 0.2413 | 1.65 |
| entry page scan (100) | jsonl | 0.0248 | 0.0014 | 0.06 |
| filtered task scan (50) | memory | 0.0041 | 0.0008 | 0.20 |
| filtered task scan (50) | sqlite | 0.0338 | 0.0539 | 1.59 |
| filtered task scan (50) | jsonl | 0.0056 | 0.0007 | 0.13 |
| exact document address among many | memory | 0.0009 | 0.0005 | 0.56 |
| exact document address among many | sqlite | 0.0064 | 0.0086 | 1.34 |
| exact document address among many | jsonl | 0.0010 | 0.0004 | 0.40 |
| document replay tail (0) | memory | 0.0010 | 0.0001 | 0.10 |
| document replay tail (0) | sqlite | 0.0127 | 0.0155 | 1.22 |
| document replay tail (0) | jsonl | 0.0009 | 0.0001 | 0.11 |
| document replay tail (16) | memory | 0.0139 | 0.0015 | 0.11 |
| document replay tail (16) | sqlite | 0.0490 | 0.0563 | 1.15 |
| document replay tail (16) | jsonl | 0.0139 | 0.0020 | 0.14 |
| document replay tail (128) | memory | 0.0840 | 0.0116 | 0.14 |
| document replay tail (128) | sqlite | 0.2215 | 0.2179 | 0.98 |
| document replay tail (128) | jsonl | 0.0977 | 0.0121 | 0.12 |
| document replay tail (1024) | memory | 0.8626 | 0.0902 | 0.10 |
| document replay tail (1024) | sqlite | 1.6141 | 1.3647 | 0.85 |
| document replay tail (1024) | jsonl | 0.5832 | 0.0867 | 0.15 |
| ancient historical read before newer base | memory | 0.0818 | 0.0120 | 0.15 |
| ancient historical read before newer base | sqlite | 0.1932 | 0.1820 | 0.94 |
| ancient historical read before newer base | jsonl | 0.0761 | 0.0124 | 0.16 |
| recent historical read after newer base | memory | 0.0847 | 0.0114 | 0.13 |
| recent historical read after newer base | sqlite | 0.1917 | 0.1802 | 0.94 |
| recent historical read after newer base | jsonl | 0.0783 | 0.0132 | 0.17 |
| fork-depth history scan (100) | memory | 0.0213 | 0.0017 | 0.08 |
| fork-depth history scan (100) | sqlite | 0.1791 | 0.3143 | 1.75 |
| fork-depth history scan (100) | jsonl | 0.0182 | 0.0016 | 0.09 |
| fork-depth head lookup | memory | 0.0006 | 0.0002 | 0.33 |
| fork-depth head lookup | sqlite | 0.0550 | 0.1000 | 1.82 |
| fork-depth head lookup | jsonl | 0.0007 | 0.0002 | 0.29 |
| commit one entry | memory | 0.0109 | 0.0035 | 0.32 |
| commit one entry | sqlite | 0.0651 | 0.1109 | 1.70 |
| commit one entry | jsonl | 0.7909 | 0.3477 | 0.44 |
| commit 100 entries | memory | 0.1173 | 0.0650 | 0.55 |
| commit 100 entries | sqlite | 0.8511 | 2.0120 | 2.36 |
| commit 100 entries | jsonl | 0.8563 | 0.9458 | 1.10 |
| commit mixed entry/task/submission/document | memory | 0.0140 | 0.0064 | 0.46 |
| commit mixed entry/task/submission/document | sqlite | 0.3203 | 0.4078 | 1.27 |
| commit mixed entry/task/submission/document | jsonl | 2.0022 | 0.9930 | 0.50 |
| reopen and first exact read | sqlite | 1.4945 | 1.3953 | 0.93 |
| reopen and first exact read | jsonl | 644.6951 | 580.0312 | 0.90 |

- **Memory and JSONL reads.** PiSharp is 2 to 15 times faster. Both backends serve reads from in-memory indexes, so they mostly measure the runtime.
- **SQLite reads.** These range from 0.85 to 1.8 times Node's. Both sides run the same SQL against the same schema. The difference is the driver: `Microsoft.Data.Sqlite` with SQLitePCLRaw here, `node:sqlite` there. Scans returning 100 rows (entry pages, fork history) cost the most. The likely cause is the per-row materialization of records.
- **Writes.** SQLite's batched `commit 100 entries` is about 2 times slower in PiSharp. The JSONL single and mixed commits are about 2 times faster. In both runtimes, JSONL reopen replays the whole log (about 0.6 s at this scale).

## Footprint (`storage-memory.ts`)

Deltas after the deterministic seed, one child process per row. Node's heap is the V8 heap. PiSharp's heap is the GC heap after three forced full collections. RSS is the working set. Node's `external` column has no .NET counterpart and is left out.

Upstream under Node:

| backend | scale | heap after seed MiB | RSS after seed MiB | heap after reads MiB | heap bytes/primary record | disk main MiB | disk auxiliary MiB | disk total MiB | disk files | disk detail |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| memory | 1k | 1.37 | 6.45 | 0.15 | 862 | - | - | - | - | - |
| memory | 10k | 7.81 | 25.20 | 0.16 | 574 | - | - | - | - | - |
| sqlite | 1k | 0.18 | 2.53 | 0.13 | 112 | 0.61 | 3.99 | 4.60 | 3 | pages/free 167/0 |
| sqlite | 10k | 0.20 | 9.58 | 0.13 | 14 | 4.34 | 4.04 | 8.38 | 3 | pages/free 1122/0 |
| jsonl | 1k | 1.51 | 5.69 | 0.16 | 951 | 0.41 | 0.32 | 0.73 | 340 | documents/tasks 205/134 |
| jsonl | 10k | 7.29 | 32.41 | 0.16 | 535 | 2.73 | 1.22 | 3.95 | 3340 | documents/tasks 2005/1334 |

PiSharp:

| backend | scale | heap after seed MiB | RSS after seed MiB | heap after reads MiB | heap bytes/primary record | disk main MiB | disk auxiliary MiB | disk total MiB | disk files | disk detail |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| memory | 1k | 1.69 | 13.68 | 0.01 | 1063 | - | - | - | - | - |
| memory | 10k | 13.08 | 50.62 | 0.01 | 961 | - | - | - | - | - |
| sqlite | 1k | 0.02 | 52.52 | 0.02 | 11 | 0.61 | 3.99 | 4.60 | 3 | pages/free 167/0 |
| sqlite | 10k | 0.02 | 58.58 | 0.02 | 1 | 4.34 | 4.04 | 8.38 | 3 | pages/free 1122/0 |
| jsonl | 1k | 1.77 | 46.45 | 0.02 | 1114 | 0.41 | 0.32 | 0.73 | 340 | documents/tasks 205/134 |
| jsonl | 10k | 13.80 | 71.47 | 0.01 | 1014 | 2.73 | 1.22 | 3.95 | 3340 | documents/tasks 2005/1334 |

- **On disk** the footprint is identical to the byte on every row: the same SQLite page counts, and the same JSONL main, sidecar, document and task files. This matches decision 0005's rule that the stored format is shared.
- **In memory** the managed heap per record is 1.1 to 1.9 times V8's. Records are immutable C# objects with a dictionary per JSON object. RSS deltas are larger in PiSharp because the first seed also pays for JIT compilation and loading the SQLite native library.

## Tool output (`tool-output-bench.ts`)

Each cell is **upstream Node / PiSharp**, for each of upstream's 50 scenarios, each in its own child process.

- **Rate scenarios** write for 3 s (or 30 s). On SQLite and JSONL they then close the Harness mid-round and reopen it, which measures replay.
- **`api.output 1 GiB`** pushes 1 GiB through `api.output` in 64 KiB chunks.
- **`bash cat 1 GiB`** runs `cat` of a 1 GiB file of unique lines through the bash tool and the real Harness path.

| scenario | backend | ms | liveCommits | liveOpKiB | outputSets | commit p99 ms | peak RSS MiB | mid-round KiB | replay ms | final KiB |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| low head x1 | memory | 3061 / 3088 | 23 / 22 | 2 / 2 | 1 / 1 | 0.62 / 8.48 | 102 / 49 | - / - | - / - | - / - |
| low head x4 | memory | 3073 / 3226 | 74 / 74 | 7 / 7 | 4 / 4 | 0.9 / 7.2 | 101 / 56 | - / - | - / - | - / - |
| low tail x1 | memory | 3056 / 3242 | 23 / 23 | 2 / 2 | 1 / 1 | 0.64 / 7.16 | 102 / 49 | - / - | - / - | - / - |
| low tail x4 | memory | 3059 / 3297 | 74 / 74 | 7 / 7 | 4 / 4 | 0.74 / 7.63 | 102 / 56 | - / - | - / - | - / - |
| normal head x1 | memory | 3066 / 3132 | 36 / 35 | 53 / 52 | 1 / 1 | 0.72 / 8.16 | 103 / 55 | - / - | - / - | - / - |
| normal head x4 | memory | 3066 / 3117 | 126 / 126 | 209 / 209 | 4 / 4 | 0.16 / 0.77 | 106 / 68 | - / - | - / - | - / - |
| normal tail x1 | memory | 3065 / 3144 | 36 / 36 | 87 / 87 | 1 / 1 | 0.61 / 8.61 | 104 / 56 | - / - | - / - | - / - |
| normal tail x4 | memory | 3079 / 3094 | 126 / 126 | 351 / 347 | 4 / 4 | 0.26 / 0.9 | 115 / 76 | - / - | - / - | - / - |
| high head x1 | memory | 3009 / 3065 | 34 / 32 | 53 / 53 | 1 / 1 | 0.73 / 7.18 | 119 / 57 | - / - | - / - | - / - |
| high head x4 | memory | 3012 / 3078 | 118 / 110 | 213 / 212 | 4 / 4 | 0.22 / 1.8 | 178 / 94 | - / - | - / - | - / - |
| high tail x1 | memory | 3010 / 3069 | 15 / 14 | 356 / 305 | 7 / 6 | 0.63 / 7.16 | 117 / 59 | - / - | - / - | - / - |
| high tail x4 | memory | 3009 / 3078 | 38 / 38 | 1219 / 1219 | 24 / 24 | 0.62 / 7.15 | 169 / 95 | - / - | - / - | - / - |
| api.output 1 GiB head | memory | 206 / 658 | 9 / 9 | 51 / 51 | 1 / 1 | 0.81 / 8.67 | 107 / 49 | - / - | - / - | - / - |
| api.output 1 GiB tail | memory | 165 / 572 | 9 / 9 | 51 / 51 | 1 / 1 | 0.6 / 7.11 | 108 / 49 | - / - | - / - | - / - |
| api.output 1 GiB tail repetitive | memory | 2687 / 810 | 35 / 14 | 162 / 36 | 27 / 6 | 0.6 / 7.46 | 118 / 51 | - / - | - / - | - / - |
| bash cat 1 GiB ¹ | memory | 1781 / 2300 | 12 / 13 | 203 / 211 | 4 / 5 | 0.65 / 7.09 | 134 / 108 | - / - | - / - | - / - |
| low head x1 | sqlite | 3636 / 3716 | 23 / 23 | 2 / 2 | 1 / 1 | 0.97 / 18.74 | 103 / 58 | 716 / 716 | 5 / 15 | 470 / 470 |
| low head x4 | sqlite | 3634 / 3712 | 74 / 74 | 7 / 7 | 4 / 4 | 1 / 18.22 | 103 / 69 | 1573 / 1573 | 5 / 15 | 671 / 671 |
| low tail x1 | sqlite | 3622 / 3706 | 23 / 23 | 2 / 2 | 1 / 1 | 1 / 19.57 | 103 / 58 | 716 / 716 | 4 / 14 | 470 / 470 |
| low tail x4 | sqlite | 3631 / 3723 | 74 / 74 | 7 / 7 | 4 / 4 | 1.09 / 18.07 | 103 / 69 | 1573 / 1573 | 5 / 15 | 671 / 671 |
| normal head x1 | sqlite | 3628 / 3723 | 37 / 36 | 53 / 53 | 1 / 1 | 1.08 / 18.21 | 102 / 67 | 1074 / 1066 | 5 / 17 | 610 / 614 |
| normal head x4 | sqlite | 3635 / 3712 | 130 / 126 | 210 / 209 | 4 / 4 | 1.15 / 3.49 | 112 / 81 | 2949 / 2901 | 6 / 16 | 1116 / 1132 |
| normal tail x1 | sqlite | 3632 / 3718 | 37 / 36 | 94 / 87 | 1 / 1 | 0.93 / 18.47 | 106 / 69 | 1171 / 1146 | 5 / 17 | 662 / 674 |
| normal tail x4 | sqlite | 3638 / 3709 | 130 / 124 | 384 / 340 | 4 / 4 | 1.07 / 3.24 | 116 / 86 | 3343 / 3174 | 6 / 18 | 1344 / 1321 |
| high head x1 | sqlite | 3619 / 3711 | 35 / 33 | 53 / 53 | 1 / 1 | 0.95 / 18.31 | 121 / 66 | 973 / 941 | 4 / 16 | 594 / 594 |
| high head x4 | sqlite | 3632 / 3743 | 122 / 110 | 213 / 212 | 4 / 4 | 1.11 / 13.22 | 184 / 102 | 2583 / 2390 | 6 / 17 | 1092 / 1092 |
| high tail x1 | sqlite | 3620 / 3716 | 15 / 15 | 356 / 356 | 7 / 7 | 1.11 / 18.26 | 119 / 65 | 982 / 982 | 5 / 18 | 906 / 906 |
| high tail x4 | sqlite | 3634 / 3737 | 42 / 42 | 1422 / 1422 | 28 / 28 | 1.47 / 20.23 | 187 / 99 | 2623 / 2623 | 7 / 21 | 2324 / 2324 |
| api.output 1 GiB head | sqlite | 180 / 597 | 9 / 9 | 51 / 51 | 1 / 1 | 0.93 / 20.95 | 109 / 55 | - / - | - / - | 873 / 881 |
| api.output 1 GiB tail | sqlite | 168 / 693 | 9 / 10 | 51 / 51 | 1 / 1 | 0.95 / 17.84 | 109 / 57 | - / - | - / - | 873 / 897 |
| api.output 1 GiB tail repetitive | sqlite | 2662 / 911 | 35 / 15 | 162 / 42 | 27 / 7 | 0.99 / 20.32 | 120 / 58 | - / - | - / - | 1521 / 957 |
| high tail x1 30s | sqlite | 30650 / 30731 | 70 / 69 | 3144 / 3093 | 62 / 61 | 9.5 / 20.52 | 129 / 84 | 6808 / 6808 | 12 / 29 | 3766 / 3714 |
| bash cat 1 GiB ¹ | sqlite | 1753 / 2251 | 12 / 13 | 203 / 211 | 4 / 5 | 0.94 / 17.01 | 137 / 113 | - / - | - / - | 1102 / 1130 |
| low head x1 | jsonl | 3657 / 3762 | 23 / 22 | 2 / 2 | 1 / 1 | 9.38 / 21.34 | 103 / 55 | 11 / 11 | 8 / 66 | 9 / 9 |
| low head x4 | jsonl | 3686 / 3797 | 74 / 74 | 7 / 7 | 4 / 4 | 10.76 / 18.76 | 104 / 63 | 30 / 30 | 14 / 91 | 20 / 20 |
| low tail x1 | jsonl | 3665 / 3747 | 23 / 22 | 2 / 2 | 1 / 1 | 7.23 / 17.2 | 102 / 55 | 11 / 11 | 8 / 55 | 9 / 9 |
| low tail x4 | jsonl | 3665 / 3769 | 74 / 70 | 7 / 7 | 4 / 4 | 10.17 / 17.15 | 104 / 64 | 30 / 28 | 14 / 78 | 20 / 19 |
| normal head x1 | jsonl | 3673 / 3775 | 37 / 36 | 53 / 53 | 1 / 1 | 9.74 / 18.05 | 104 / 63 | 65 / 64 | 10 / 72 | 60 / 60 |
| normal head x4 | jsonl | 3685 / 3783 | 130 / 126 | 210 / 209 | 4 / 4 | 7.21 / 7.15 | 114 / 82 | 245 / 244 | 18 / 85 | 224 / 224 |
| normal tail x1 | jsonl | 3669 / 3773 | 37 / 36 | 93 / 85 | 1 / 1 | 8.2 / 16.93 | 104 / 60 | 105 / 97 | 11 / 71 | 60 / 60 |
| normal tail x4 | jsonl | 3690 / 3776 | 130 / 126 | 377 / 340 | 4 / 4 | 7.37 / 7.63 | 122 / 81 | 412 / 375 | 24 / 87 | 224 / 224 |
| high head x1 | jsonl | 3668 / 3763 | 35 / 33 | 53 / 53 | 1 / 1 | 8.32 / 16.32 | 121 / 64 | 65 / 64 | 11 / 67 | 60 / 59 |
| high head x4 | jsonl | 3676 / 3769 | 122 / 110 | 213 / 212 | 4 / 4 | 7.11 / 16.41 | 127 / 97 | 246 / 243 | 19 / 85 | 224 / 222 |
| high tail x1 | jsonl | 3654 / 3751 | 15 / 15 | 356 / 356 | 7 / 7 | 8.42 / 18.15 | 121 / 77 | 363 / 363 | 13 / 71 | 58 / 58 |
| high tail x4 | jsonl | 3688 / 3780 | 42 / 42 | 1422 / 1422 | 28 / 28 | 8.12 / 18.47 | 126 / 100 | 1437 / 1437 | 26 / 86 | 216 / 216 |
| api.output 1 GiB head | jsonl | 249 / 592 | 9 / 9 | 51 / 51 | 1 / 1 | 12.88 / 17.7 | 106 / 52 | - / - | - / - | 57 / 57 |
| api.output 1 GiB tail | jsonl | 277 / 682 | 9 / 9 | 51 / 51 | 1 / 1 | 10 / 17.01 | 108 / 53 | - / - | - / - | 57 / 57 |
| api.output 1 GiB tail repetitive | jsonl | 10525 / 758 | 110 / 14 | 610 / 36 | 102 / 6 | 124.45 / 17.2 | 119 / 52 | - / - | - / - | 22 / 13 |
| high tail x1 30s | jsonl | 30781 / 30790 | 69 / 69 | 3094 / 3093 | 61 / 61 | 32.13 / 16.87 | 128 / 117 | 3113 / 3112 | 65 / 86 | 63 / 63 |
| bash cat 1 GiB ¹ | jsonl | 1739 / 2569 | 12 / 14 | 203 / 262 | 4 / 6 | 9.33 / 16.21 | 135 / 110 | - / - | - / - | 58 / 58 |

¹ Upstream's `cat` with forward slashes (see above).

- **Rate scenarios match closely.** Live commit counts, operation bytes, output sets, mid-round sizes and final sizes agree within a few percent. Most SQLite and JSONL stored sizes are identical. The throttle and Chord's append and trim operations behave as upstream does.
- **Commit p99 is higher in PiSharp (7 to 21 ms).** With 15 to 130 commits per run, the 99th percentile is in effect the first commit, which includes JIT compilation. The p50 values are close to Node's.
- **Replay after reopen is slower in PiSharp.** It takes 14 to 29 ms on SQLite and 55 to 91 ms on JSONL; Node takes 4 to 26 ms and 8 to 65 ms. Both are dominated by opening the storage in a fresh process.
- **The firehose is about 3 times slower in PiSharp.** Pushing 1 GiB of unique lines through `api.output` takes 0.6 to 0.7 s; Node takes 0.17 to 0.28 s. It stays linear in the bytes pushed, which is upstream's requirement for the firehose: accepting a chunk must not cost work proportional to the retained window.
- **The repetitive firehose is 3 to 14 times faster in PiSharp, with far fewer whole-window sets** (6 to 7 against Node's 27 to 102). Operation shape is not canonical (decision 0005), so this is allowed. The difference is in how often the tail window's overlap search falls back to a full set.

## Document append (`document-append-bench.ts`)

Chord v1.2.0 fixed appending to an array costing a walk over the whole array: the delta tracker plans inserts and removals that keep the remaining elements in order from the changed ranges alone, and `applyImmutable` builds an appended array in one copy instead of two. Upstream reports 32,000 one-item appends, one commit each, taking 1.4 s instead of 11.3 s. PiSharp ports both: an array draft stays a piece table while elements are only inserted or removed (`DraftArray.Pieces`), and `DeltaApply` has the `appendCopy` fast path. An append still costs one copy of the array for the next immutable value, so time per append grows with the array; the family stays flat.

Measured on 2026-10-15, same machine, memory storage, one run each. Before is the branch at `5aa1b93` with the same benchmark.

| appends | array, before | array, after | family, after |
|---:|---:|---:|---:|
| 1,000 | 88 ms | 58 ms | 21 ms |
| 8,000 | 1.38 s | 0.20 s | 0.19 s |
| 16,000 | 6.49 s | 1.29 s | 0.15 s |
| 32,000 | 33.0 s | 5.22 s | 0.25 s |

- **32,000 appends are about 6 times faster.** Before, every append made an entry for each element of the array, walked them to plan the operations and copied them into the next value.
- **The remaining cost is the .NET runtime's large object heap, not the tracker.** Each revision of an array of more than about 10,600 elements is a new large object, and the background GC throttles large allocations while it runs. With `DOTNET_gcConcurrent=0`, 32,000 appends take 1.81 s after (27.4 s before), close to upstream's 1.4 s, and the microseconds per append grow in step with the array (15, 32 and 57 µs at 8,000, 16,000 and 32,000), the one copy per append.

## Document cache (`session-document-cache-bench.ts`)

Pi v1.2.0 bounded the Session's document cache: a loaded document that no watch or document state observes leaves memory once it was not read or written for `documentRetentionMs` (default ten minutes, `0` drops it right after use) and loads from Storage again on its next access. A Harness on SQLite creates N conversations and runs one short task in each, after 200 warm-up cycles. Retained bytes are the growth over those N conversations: Node sums a heap snapshot's self sizes, PiSharp reads the managed heap after a blocking, compacting full collection (`HeapRetained.cs`). The old-document read is the mean of 100 reads of the first conversation's `pi.live`.

Measured on 2026-10-15, same machine, one run each.

| conversations | retention | Node ms | PiSharp ms | cached docs (both) | Node retained | PiSharp retained | Node read | PiSharp read |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1,000 | infinite | 827 | 1,285 | 6,000 | 4.4 MiB, 4,610 B/conv | 4.6 MiB, 4,804 B/conv | 2 µs | 8 µs |
| 1,000 | 10 min | 930 | 1,060 | 6,000 | 3.9 MiB, 4,131 B/conv | 4.6 MiB, 4,802 B/conv | 1 µs | 1 µs |
| 1,000 | 50 ms | 910 | 1,009 | 0 | -0.4 MiB | -0.2 MiB | 9 µs | 45 µs |
| 1,000 | 0 | 955 | 880 | 0 | 0.4 MiB, 384 B/conv | 0.1 MiB, 54 B/conv | 30 µs | 28 µs |
| 5,000 | infinite | 4,329 | 3,943 | 26,000 | 18.4 MiB, 3,865 B/conv | 23.1 MiB, 4,845 B/conv | 1 µs | 0 µs |
| 5,000 | 10 min | 4,508 | 3,971 | 26,000 | 18.4 MiB, 3,850 B/conv | 23.1 MiB, 4,845 B/conv | 1 µs | 1 µs |
| 5,000 | 50 ms | 4,599 | 3,954 | 0 | 0.1 MiB, 27 B/conv | 0.0 MiB, 4 B/conv | 6 µs | 4 µs |
| 5,000 | 0 | 4,765 | 4,067 | 0 | 0.4 MiB, 92 B/conv | 0.3 MiB, 58 B/conv | 30 µs | 28 µs |

- **The cache behaves as upstream's.** Kept documents leave the same 6,000 and 26,000 documents (the five built-in documents of each conversation, warm-up ones included), and both evicting retentions leave none. Upstream's changelog reports 26,000 documents and about 3.8 KB each for 5,000 conversations without eviction.
- **A kept conversation costs about 4.8 KB in PiSharp, about 3.9 to 4.6 KB under Node.** With eviction, a conversation retains well under 100 bytes in both.
- **Reading an evicted document costs about 30 µs in both**, a SQLite reload; a cached read costs a few microseconds. The 50 ms rows read after the timer evicted.

## Scheduler (`scheduler-memory-bench.ts` and `scheduler-scale`)

Pi v1.2.0 rewrote the scheduler (work list area c): no pass visits every live task any more, and the ownership indexes are bounded by live work. Measured on 2026-10-15, same machine, one run each. "Before" is PiSharp's branch at `8b97eb5`, the v1.1.0 scheduler, with the same `SchedulerScaleBench.cs`.

**Memory** (`scheduler-memory-bench.ts`, SQLite). Retained bytes are the growth over the cycles (Node: heap snapshot self sizes; PiSharp: `HeapRetained.cs`).

| cycles | Node ms (unchanged) | Node ms (release without polling) | PiSharp ms | settled, edges, below (all) | Node retained | PiSharp retained | reads for a later message (Node / PiSharp) |
|---:|---:|---:|---:|---|---:|---:|---|
| 1,000 | 11,912 | 1,099 | 1,559 | 0, 1, 1 | 5.0 MiB, 5,191 B/cycle | 4.6 MiB, 4,859 B/cycle | 7 / 6 |
| 10,000 | 125,432 | 11,328 | 11,279 | 0, 1, 1 | 35.9 MiB, 3,763 B/cycle | 44.2 MiB, 4,638 B/cycle | 7 / 6 |

- **The indexes stay bounded in both.** After the cycles nothing is settled, and only the parent's conversation edge and its one listed child remain, as upstream's changelog reports. The benchmark's own check passes in both.
- **The unchanged Node time is the benchmark's `release()`**, which polls with `setTimeout(0)` until the task waits, about 11 ms a cycle on Windows. With a release that opens the gate before or after the task waits, as PiSharp's does, Node runs 1,000 cycles in 1.1 s and 10,000 in 11.3 s; PiSharp takes 1.6 s and 11.3 s.
- **A later message reads the same chain in both:** the old conversation and its ended tool task, two reads from the scheduler's chain load. The seventh read under the unchanged Node run is its final `waitForTask`, which finds the late task already terminal after the polling delay; without the polling Node reads 6 too.
- **A cycle retains about 4.6 to 4.9 KB in PiSharp and 3.8 to 5.2 KB under Node.** The benchmark runs with the default ten-minute document retention, so each subagent conversation's documents stay cached; the scheduler keeps none of the ended cycles.
- **Pi v1.5.0 sync, area publication-caches** (2026-11-05, one run, PiSharp only): a later message reads 1 record after 1,000 cycles and 2 after 10,000, instead of 6, because the Session's record cache serves the old conversation and its ended tool task, which it wrote. The 1 and 2 fit the cache's bounds (not traced): 1,000 cycles end more tasks than the 256 ended tasks it keeps, and 10,000 also create more conversations than the 4,096 it keeps. The cycles take 2,196 and 13,663 ms and retain 5,399 and 4,750 B per cycle (5.1 and 45.3 MiB), the cache's bounded records included. `benchmarks.smoke` checks 0 reads after 3 cycles.

**Scale** (`scheduler-scale`, memory storage; the Node twin runs the same scenarios on the pinned upstream). Commits are equal in all three for every size.

| scenario | N | before (v1.1.0 scheduler) | PiSharp | Node | commits |
|---|---:|---:|---:|---:|---:|
| fan-out | 250 | 259 ms | 219 ms | 35 ms | 516 |
| fan-out | 500 | 151 ms | 22 ms | 37 ms | 1,016 |
| fan-out | 1,000 | 304 ms | 46 ms | 64 ms | 2,016 |
| fan-out | 2,000 | 488 ms | 93 ms | 114 ms | 4,016 |
| fan-out | 4,000 | 1,093 ms | 228 ms | 209 ms | 8,016 |
| chain | 500 | 2,432 ms | 31 ms | 19 ms | 504 |
| chain | 2,000 | 1,061,190 ms | 76 ms | 78 ms | 2,004 |
| chain | 20,000 | not run | 771 ms | 887 ms | 20,004 |

- **Owned chains settle in linear time.** Before, a 500-task chain took 2.4 s and a 2,000-task chain almost 18 minutes; now 31 ms and 76 ms, and 20,000 tasks 0.8 s, close to Node (upstream's changelog: 500 tasks took 12 s before, 50 ms after, 20,000 take 1.5 s).
- **Fan-out is linear in both runtimes, and PiSharp matches Node from 500 children up.** The first PiSharp row includes JIT warm-up. Before the rewrite it was about four to seven times slower, as each pass scanned every live child.
- Upstream's own fan-out benchmark (nested calls, area a) reports 8,000 nested calls in 2.7 s instead of 18.8 s; see the next section.

**Pi v1.5.0 sync, area scheduler-idle** (2026-11-05; Pi 35dfa7573, durable 1.5.0 Changed#8). This area measures PiSharp only. The Node twin does not run the new `conversations` scenario, and upstream v1.5.0 was not run. The runs use memory storage and alternate between two builds, three times each; the table shows the medians. "Before" is the branch's base `0f4a25c2` built with the same `SchedulerScaleBench.cs`, and "after" is the area's head. Other work was running on the machine.

`conversations` keeps N idle conversations' context ranges in the scheduler: N ownerless conversations with one entry each, all read by one task. It then times 128 one-tool runs (a tool call, then a text), round robin over the conversations. Every run makes 12 commits in both builds.

| scenario | N | before | after | commits |
|---|---:|---:|---:|---:|
| conversations | 1 | 2.15 ms per run (465 runs/s) | 2.71 ms per run (370 runs/s) | 1,536 |
| conversations | 256 | 2.34 ms (427 runs/s) | 2.04 ms (491 runs/s) | 1,536 |
| conversations | 1,024 | 3.36 ms (298 runs/s) | 1.85 ms (541 runs/s) | 1,536 |
| conversations | 2,048 | 4.64 ms (216 runs/s) | 1.81 ms (552 runs/s) | 1,536 |
| conversations | 4,096 | 7.60 ms (132 runs/s) | 1.88 ms (531 runs/s) | 1,536 |
| fan-out | 250 | 241 ms | 223 ms | 514 |
| fan-out | 500 | 77 ms | 41 ms | 1,014 |
| fan-out | 1,000 | 237 ms | 70 ms | 2,014 |
| fan-out | 2,000 | 434 ms | 166 ms | 4,014 |
| fan-out | 4,000 | 923 ms | 404 ms | 8,014 |
| chain | 500 | 38 ms | 66 ms | 504 |
| chain | 2,000 | 237 ms | 265 ms | 2,004 |
| chain | 20,000 | 3,408 ms | 2,050 ms | 20,004 |

- **The time per run no longer grows with the kept conversations.** Before the change, every commit that changed a task walked every kept context. A run took 2.2 ms with 1 kept conversation and 7.6 ms with 4,096. After it, a commit settles only the conversations above its changed tasks, and a run takes 1.8 to 2.0 ms from 256 conversations up.
- **The 1-conversation row is noise.** Both builds take 2 to 3 ms per run there. In a separate run of seven rows with 1 to 64 conversations, both builds took 2.6 to 3.4 ms per run.
- **No comparison with upstream is made.** Upstream's commit message reports one Session doing one-tool runs: 2,048 conversations went from 100 to 805 runs/s, and 4,096 from 46 to 772. That workload is not in the upstream repository. This scenario is PiSharp's own, so the two sets of numbers cannot be compared.
- **Fan-out is about twice as fast.** While invocations run, `Wake()` no longer scans the live tasks.
- **Chains: PiSharp's walk differs from upstream's.** Upstream's `#addScopesAbove` stops only at a conversation, so a commit that creates or finalizes a chain walks every task's owners, which is quadratic in the depth. This comes from reading the code, not from running upstream. Ported as written, the 20,000-deep chain took 7.9 to 8.4 s here, and `harness.ownership.tracks-ownership-chains-deeper-than-the-call-stack` took 17 s instead of 2 s. PiSharp also stops a walk at a task that an earlier walk of the same publication passed. The scopes are the same (see the `Scheduler.cs` header), and the chain takes 2.1 s.
- **The 500-deep chain row is noisy.** It runs right after the 4,000-task fan-out. In a run with only a 250-task fan-out before it, four 500-deep chains took 31 to 53 ms after the change and 58 to 100 ms before it, and two 2,000-deep chains took about 200 ms after and 318 to 445 ms before.

## Nested tool calls (`scheduler-fanout-bench.ts`, `nested-tools-bench.ts`, `nested-images-bench.ts`)

Tool task v2 (work list area a). Measured on 2026-10-15, same machine, one run each, with Node and PiSharp run one after the other; heaps are V8's `heapUsed` and the managed heap (`GC.GetTotalMemory`, `HeapRetained.cs` after a collection), which also counts garbage between collections, so the heap columns compare only roughly.

**Fan-out** (memory storage). Commits are equal for every size.

| calls | Node | PiSharp | commits |
|---:|---:|---:|---:|
| 250 | 119 ms | 347 ms | 767 |
| 500 | 150 ms | 214 ms | 1,517 |
| 1,000 | 221 ms | 531 ms | 3,017 |
| 2,000 | 403 ms | 1,079 ms | 6,017 |
| 4,000 | 775 ms | 1,711 ms | 12,017 |

Both are linear (0.2 ms per call under Node, 0.4 to 0.5 ms in PiSharp after warm-up); PiSharp takes about twice as long.

**Nested calls** (`nested-tools-bench.ts`, run 1 of 3). Without progress, commits, logical bytes written by write type and stored sizes are equal in both runtimes on both backends, but for two more commits at depth 2 in PiSharp (699 and 6,819 against 697 and 6,817). With progress, the number of progress commits follows timing (816 to 1,616 in both).

| backend | calls | depth | stream | Node | PiSharp |
|---|---:|---:|---|---:|---:|
| SQLite | 200 | 1 | 1 KiB, 2 ms gaps, yes | 387 ms | 375 ms |
| SQLite | 200 | 1 | 16 KiB, 50 ms gaps, yes | 751 ms | 680 ms |
| SQLite | 2,000 | 1 | no | 2,222 ms | 2,160 ms |
| SQLite | 2,000 | 2 | no | 2,720 ms | 2,395 ms |
| JSONL | 200 | 1 | 1 KiB, 2 ms gaps, yes | 1,869 ms | 1,140 ms |
| JSONL | 2,000 | 1 | no | 16,258 ms | 9,752 ms |
| JSONL | 2,000 | 2 | no | 19,015 ms | 10,528 ms |

**Images** (`nested-images-bench.ts`, 1 MiB base64 each). Commits, bytes written, stored sizes, nested task counts and the largest task record are equal in both runtimes.

| backend | variant | count | Node | PiSharp | settled heap, Node / PiSharp |
|---|---|---:|---:|---:|---|
| SQLite | inline | 200 | 11,496 ms | 16,602 ms | 1.0 / -1.0 MiB |
| SQLite | reference | 200 | 2,171 ms | 2,669 ms | 0.6 / 0.1 MiB |
| JSONL | inline | 200 | 7,807 ms | 10,608 ms | 1.2 / 401.0 MiB |
| JSONL | reference | 200 | 4,037 ms | 3,094 ms | 1.1 / -801.8 MiB |

- **Passing references instead of image data** writes 201 MiB instead of 1,401 MiB in both, as upstream's benchmark intends to show.
- **PiSharp's JSONL storage holds what it wrote.** After the inline run settles, PiSharp keeps about 400 MiB (the 200 MiB of live JSONL records as UTF-16), where Node keeps about 1 MiB; after reopening, 803 MiB against 202 MiB. This is the JSONL storage's in-memory record copy, not the nested calls (SQLite keeps nothing in either runtime), and is noted for a later look at JSONL memory. Negative heap figures are collections of an earlier scenario's garbage after the baseline.

## Durable code mode (`codemode-bench.ts`)

Work list area d. Measured on 2026-10-15, same machine, two runs each with Node and PiSharp run one after the other; the table shows the second (the first agreed within the noise, but for PiSharp's slower first row, which includes the JIT's warm-up). PiSharp's scripts run on Jint engines on threads (the benchmark sets no worker launcher), upstream's on QuickJS in worker threads.

| shape | n | plain, Node | plain, PiSharp | code mode, Node | code mode, PiSharp | commits/call, plain | commits/call, code mode (Node / PiSharp) |
|---|---:|---:|---:|---:|---:|---:|---|
| sequential | 100 | 195 ms | 434 ms | 323 ms | 710 ms | 4.15 | 6.18 / 6.18 |
| sequential | 1,000 | 1,302 ms | 2,238 ms | 1,656 ms | 2,167 ms | 4.01 | 6.02 / 6.02 |
| parallel | 100 | 88 ms | 86 ms | 152 ms | 140 ms | 3.16 | 3.22 / 3.26 |
| parallel | 1,000 | 951 ms | 829 ms | 940 ms | 971 ms | 3.02 | 3.02 / 3.04 |

- **Commits are upstream's** for every plain run and every sequential script: a sequential script costs two commits per call on top of its nested call's own, the journal entry and the acknowledged count, as upstream's design says.
- **A parallel script's journal batches follow timing.** Results that finish together are journaled in one commit. Under Node every result that is ready when a commit lands joins the next one; PiSharp's nested calls finish on several threads, so a few more batches form (322 to 326 commits for 100 calls, 3,040 to 3,050 for 1,000, against Node's 322 and 3,020).
- **Code mode's own cost** in a sequential script, over the plain tool's: 1.3 ms per call at 100 calls and 0.4 ms at 1,000 under Node; in PiSharp 2.8 ms at 100 calls (its first script, with the engine's warm-up) and none measurable at 1,000. Parallel scripts cost little in either. PiSharp's plain sequential calls are slower than Node's (2.2 against 1.3 ms per call at 1,000), as in the fan-out rows above.

## Partial commits (`partial-commits`)

Pi v1.5.0 sync, durable area partial-commits. Measured on 2026-11-04, same machine, one run each. Upstream has no benchmark for this path: its changelog reports about 20 s of CPU and a 100 MB heap peak for a 500 KB tool call before the fix, and its test (`harness-view-run.test.ts`, "commits a growing text and tool call at a cost that follows the new text"; `harness.view-run.streamed-partials.*` here) bounds the per-commit ratio of a 400 KB answer over a 40 KB one below 2. The benchmark streams the answer in 256-character pieces through the real generation task on memory storage, with a partial interval of 0 and each piece waiting for its commit, so every piece that changes the partial gets one commit.

| size | phase | before: commits | before: KiB/commit | before: total | after: commits | after: KiB/commit | after: total |
|---:|---|---:|---:|---:|---:|---:|---:|
| 40,000 | text | 165 | 303.5 | 48.9 MiB | 165 | 26.5 | 4.3 MiB |
| 40,000 | tool call | 1 | 16,681.9 | 16.3 MiB | 180 | 33.6 | 5.9 MiB |
| 400,000 | text | 1,641 | 2,480.8 | 3,975.6 MiB | 1,641 | 26.0 | 41.6 MiB |
| 400,000 | tool call | 1 | 1,603,567.9 | 1,566.0 MiB | 1,797 | 31.4 | 55.1 MiB |
| 500,000 | text | 2,051 | 3,047.0 | 6,102.9 MiB | 2,051 | 26.0 | 52.1 MiB |
| 500,000 | tool call | 1 | 2,670,936.1 | 2,608.3 MiB | 2,247 | 31.4 | 68.9 MiB |

- **Before**, each text commit reassigned the whole growing string, which Chord compared with the committed one and .NET copied: the per-commit cost grew with the length (10.0 times from 40,000 to 500,000 characters). A tool call's partial kept the arguments it started with (`{}`) until the call ended, so its pieces committed nothing and the call's end committed it whole.
- **After**, a commit appends what text and thinking grew by and applies the argument changes of PiSharp.AI's `StreamingJsonParser` since the last commit, and an appended `JsonStringValue` defers its concatenation until it is read. The per-commit cost is flat in both phases (ratios 0.98 and 0.93), and the partials now show the streamed arguments, as upstream's do.
- The 500,000-character text phase took 2,332 ms before and 161 ms after.

## Conversation view (`conversation-view-bench.ts`)

Pi v1.5.0 sync, durable area view: `PiSharp.Durable.Benchmarks conversation-view both` (ConversationViewBench.cs), measured on 2026-11-04, same machine, one run, each scenario in its own child process. Upstream prints its table and records no numbers, and no upstream run was taken here. The view is a window over the transcript (the newest 50 entries at start, up to 100 while a watch runs), so starting and resuming a watch do not scan the conversation's 5,000 entries, and a watch's heap stays flat while 20,000 entries are appended.

| scenario | storage | p50 ms | µs per commit | window entries | heap with watches MiB | first entry collected | CPU ms | wall ms |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| watch start, 5000 entries | sqlite | 0.44 | | 50 | | | 31 | 24 |
| resume 20 back, 5000 entries | sqlite | 0.22 | | 20 | | | 16 | 22 |
| append x1 per commit, 1 watch | sqlite | | 96.6 | 61 | 0.5 | | 188 | 209 |
| append x1 per commit, 4 watches | sqlite | | 91.4 | 61 | 0.5 | | 203 | 201 |
| append x50 per commit, 4 watches | sqlite | | 1,785.8 | 50 | 0.5 | | 406 | 375 |
| pi.live only, 4 watches | sqlite | | 122.9 | | | | 672 | 630 |
| long-lived watch, 20000 appends | sqlite | | | 59 | 0.5 | yes | 1,906 | 1,487 |
| watch start, 5000 entries | store | 0.43 | | 50 | | | 31 | 23 |
| resume 20 back, 5000 entries | store | 0.23 | | 20 | | | 31 | 22 |
| append x1 per commit, 1 watch | store | | 203.2 | 61 | 0.7 | | 500 | 423 |
| append x1 per commit, 4 watches | store | | 207.8 | 61 | 0.7 | | 594 | 435 |
| append x50 per commit, 4 watches | store | | 2,301.7 | 50 | 0.7 | | 547 | 483 |
| pi.live only, 4 watches | store | | 215.4 | | | | 1,531 | 1,094 |
| long-lived watch, 20000 appends | store | | | 59 | 0.8 | yes | 4,016 | 2,721 |

- The heap is the GC heap after two blocking full collections (upstream: `heapUsed` after two `gc()` calls). It was 0.4 to 0.7 MiB before each scenario, so neither the stored history nor the watches' windows stay on it.
- Delivery waits poll with `Task.Delay(1)`, which ticks at the timer resolution (15.6 ms on Windows), so the per-commit times include up to one tick spread over a scenario's commits.

## Group commit throughput (`storage-throughput`)

Pi v1.5.0 sync, durable areas group-commit (the Session half of Pi `e5c6182ba`), group-commit-scheduler (its scheduler half, in the second table) and publication-caches (its read caches, in the third and fourth tables). `PiSharp.Durable.Benchmarks storage-throughput` (StorageBench.cs), measured on 2026-11-05, same machine and runtime, so the numbers are machine-local. In the first table, *before* is the benchmark at `5eb9562c` (the commit before area group-commit, with the same StorageBench.cs), built from an archive copy, and *after* is area group-commit's branch, one run each. Each Session row times 512 commits split across N conversations, each conversation committing in sequence, and each Harness row times 128 runs of one faux text reply. The memory backend sits behind a fixed round trip on every storage call. "Per s" is commits or runs per second, and "commits per call" is the mean group size, the commits each storage call carried.

Upstream's changelog (durable 1.5.0) quotes about 5 to 440 agent runs per second for one Harness with 128 conversations over storage with a 4 ms round trip (median run 25 s to 0.28 s), measured with the probe that `docs/group-commit.md` section 9 describes; the probe is not in the repository, so no upstream run was taken here, and **no parity is claimed**. In the first two tables a PiSharp Harness run still read from Storage on the line, because the read caches that let a normal run make no storage reads (durable 1.5.0's Changed#4) came with area publication-caches, measured last.

| scenario | backend | round trip ms | conversations | before: per s | before: commits per call | after: per s | after: commits per call | after / before |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| session commits | memory | 0 | 1 | 55,937 | 1.00 | 45,559 | 1.00 | 0.81 |
| session commits | memory | 0 | 8 | 202,172 | 1.00 | 129,097 | 1.00 | 0.64 |
| session commits | memory | 0 | 32 | 204,008 | 1.00 | 121,255 | 1.00 | 0.59 |
| session commits | memory | 0 | 128 | 211,553 | 1.00 | 130,240 | 1.00 | 0.62 |
| session commits | memory | 1 | 1 | 491 | 1.00 | 480 | 1.00 | 0.98 |
| session commits | memory | 1 | 8 | 494 | 1.00 | 963 | 1.00 | 1.95 |
| session commits | memory | 1 | 32 | 494 | 1.00 | 972 | 1.00 | 1.97 |
| session commits | memory | 1 | 128 | 491 | 1.00 | 969 | 1.00 | 1.97 |
| session commits | memory | 4 | 1 | 124 | 1.00 | 124 | 1.00 | 1.00 |
| session commits | memory | 4 | 8 | 124 | 1.00 | 247 | 1.00 | 1.99 |
| session commits | memory | 4 | 32 | 124 | 1.00 | 248 | 1.00 | 2.00 |
| session commits | memory | 4 | 128 | 124 | 1.00 | 248 | 1.00 | 2.00 |
| session commits | sqlite | 0 | 1 | 11,073 | 1.00 | 9,847 | 1.00 | 0.89 |
| session commits | sqlite | 0 | 8 | 14,771 | 1.00 | 11,176 | 1.00 | 0.76 |
| session commits | sqlite | 0 | 32 | 11,286 | 1.00 | 12,733 | 1.00 | 1.13 |
| session commits | sqlite | 0 | 128 | 13,029 | 1.00 | 9,979 | 1.00 | 0.77 |
| session changes | memory | 0 | 1 | 119,654 | 1.00 | 73,888 | 1.00 | 0.62 |
| session changes | memory | 0 | 8 | 145,525 | 1.00 | 88,112 | 1.00 | 0.61 |
| session changes | memory | 0 | 32 | 86,501 | 1.00 | 86,576 | 1.00 | 1.00 |
| session changes | memory | 0 | 128 | 148,924 | 1.00 | 86,387 | 1.00 | 0.58 |
| session changes | memory | 1 | 1 | 946 | 1.00 | 938 | 1.00 | 0.99 |
| session changes | memory | 1 | 8 | 968 | 1.00 | 4,175 | 4.49 | 4.31 |
| session changes | memory | 1 | 32 | 971 | 1.00 | 19,170 | 21.33 | 19.74 |
| session changes | memory | 1 | 128 | 977 | 1.00 | 57,611 | 73.14 | 58.97 |
| session changes | memory | 4 | 1 | 248 | 1.00 | 248 | 1.00 | 1.00 |
| session changes | memory | 4 | 8 | 247 | 1.00 | 1,091 | 4.41 | 4.42 |
| session changes | memory | 4 | 32 | 248 | 1.00 | 5,398 | 22.26 | 21.77 |
| session changes | memory | 4 | 128 | 248 | 1.00 | 16,632 | 73.14 | 67.06 |
| session changes | sqlite | 0 | 1 | 9,686 | 1.00 | 9,172 | 1.00 | 0.95 |
| session changes | sqlite | 0 | 8 | 8,490 | 1.00 | 10,618 | 1.00 | 1.25 |
| session changes | sqlite | 0 | 32 | 10,055 | 1.00 | 11,073 | 1.00 | 1.10 |
| session changes | sqlite | 0 | 128 | 7,718 | 1.00 | 8,279 | 1.00 | 1.07 |
| harness runs | memory | 0 | 1 | 652.2 | 1.00 | 580.2 | 1.00 | 0.89 |
| harness runs | memory | 0 | 8 | 4,433 | 1.00 | 3,624 | 1.00 | 0.82 |
| harness runs | memory | 0 | 32 | 3,592 | 1.00 | 3,068 | 1.00 | 0.85 |
| harness runs | memory | 0 | 128 | 2,969 | 1.00 | 2,757 | 1.00 | 0.93 |
| harness runs | memory | 4 | 1 | 10.7 | 1.00 | 10.7 | 1.00 | 1.00 |
| harness runs | memory | 4 | 8 | 11.7 | 1.00 | 11.6 | 1.00 | 0.99 |
| harness runs | memory | 4 | 32 | 11.8 | 1.00 | 11.6 | 1.00 | 0.98 |
| harness runs | memory | 4 | 128 | 11.8 | 1.00 | 11.8 | 1.00 | 1.00 |
| harness runs | sqlite | 0 | 1 | 726.0 | 1.00 | 632.8 | 1.00 | 0.87 |
| harness runs | sqlite | 0 | 8 | 794.9 | 1.00 | 777.7 | 1.00 | 0.98 |
| harness runs | sqlite | 0 | 32 | 883.4 | 1.00 | 819.5 | 1.00 | 0.93 |
| harness runs | sqlite | 0 | 128 | 827.8 | 1.00 | 905.3 | 1.00 | 1.09 |

- **Session commits (entry appends) did not batch yet** (1.00 commits per call everywhere). Each `AppendEntryAsync` read its conversation record from Storage on the line, a round trip that serialized the jobs until area publication-caches gave the entry path its record cache (below). The line no longer waits for the previous commit's publication, so one job's read overlaps the commit before it: about twice the commits per second at 1 and 4 ms once 8 or more conversations work at once (124 to 248 at 4 ms).
- **Session changes (a cached conversation document) batch.** Such a commit reads nothing from Storage, so every part admitted while a call is in flight goes in the next call: 73 commits per call at 128 conversations, 248 to 16,632 commits per second at 4 ms and 977 to 57,611 at 1 ms. One conversation still pays one round trip per commit, as upstream's does.
- **Harness runs did not change in area group-commit, by design.** `DurableHarness` kept the line waiting for publication (`LineWaitsForPublication`) until area group-commit-scheduler ported the scheduler's regions and membership keys, so its runs still committed one at a time: 11.8 runs per second at 4 ms with 128 conversations before and after (median run 9,319 and 9,336 ms), not upstream's 440.
- **Memory storage at 0 ms costs more CPU per commit after.** Session commits fall from about 200,000 to about 130,000 per second at 8 or more conversations (19 to 41 % fewer across the memory rows at 0 ms), and Harness runs are 7 to 18 % slower, because the writer's admission, its publication hop and the outcome fences replace a direct call. Upstream's changelog reports the same direction for in-memory storage with no latency ("up to 25% fewer runs per second"). Its gain of about 30 % with local SQLite at 128 conversations is a Harness number, and PiSharp's Harness did not batch yet; the SQLite rows here move both ways within the noise.

**Area group-commit-scheduler** (the Harness half). The barrier is gone: the scheduler's decisions record and touch the keys they read, every commit writes its membership keys, and Harness line jobs overlap storage calls. Harness rows only, two runs each (the second with `latency 0,4`); *before* is the benchmark at `5f7043a5` (area group-commit merged, barrier mode), built from an archive copy, *after* is area group-commit-scheduler's branch. Each cell gives run 1, run 2; "after / before" compares the means. Commits per run (4.0 with one conversation, 3.0 to 3.5 with more) and commits per call (1.00) are the same in every row before and after. The Session rows were taken again too and match the first table's *after* column within the noise (the Session code did not change).

| backend | round trip ms | conversations | before: runs per s | before: median run ms | after: runs per s | after: median run ms | after / before |
|---|---:|---:|---:|---:|---:|---:|---:|
| memory | 0 | 1 | 585.0, 519.2 | 0.5, 0.7 | 549.6, 506.0 | 0.6, 0.6 | 0.96 |
| memory | 0 | 8 | 3,900, 2,290 | 1.6, 2.9 | 3,329, 2,444 | 1.9, 2.8 | 0.93 |
| memory | 0 | 32 | 3,215, 2,939 | 9.5, 11.8 | 2,833, 2,089 | 10.3, 15.0 | 0.80 |
| memory | 0 | 128 | 2,700, 3,014 | 34.3, 31.6 | 2,067, 1,838 | 45.6, 44.2 | 0.68 |
| memory | 4 | 1 | 10.7, 10.7 | 93.1, 93.3 | 11.2, 11.2 | 89.2, 89.2 | 1.05 |
| memory | 4 | 8 | 11.7, 11.6 | 681.0, 689.7 | 13.7, 13.7 | 584.4, 584.8 | 1.18 |
| memory | 4 | 32 | 11.7, 11.8 | 2,667, 2,670 | 13.8, 13.8 | 2,285, 2,277 | 1.17 |
| memory | 4 | 128 | 11.8, 11.8 | 9,323, 9,318 | 13.8, 13.8 | 8,015, 8,023 | 1.17 |
| sqlite | 0 | 1 | 638.0, 704.2 | 1.3, 1.2 | 597.9, 666.4 | 1.4, 1.2 | 0.94 |
| sqlite | 0 | 8 | 843.9, 945.5 | 9.4, 8.2 | 790.7, 820.0 | 9.9, 9.2 | 0.90 |
| sqlite | 0 | 32 | 857.8, 928.7 | 36.0, 31.8 | 770.3, 863.9 | 38.8, 35.8 | 0.91 |
| sqlite | 0 | 128 | 900.2, 790.0 | 104.5, 127.2 | 831.2, 958.5 | 114.7, 105.8 | 1.06 |

- **Harness runs at 4 ms gain about 17 % with 8 or more conversations, not upstream's 88-fold.** 11.6 to 11.8 runs per second become 13.7 to 13.8 (median run 9,320 to 8,020 ms at 128), because a job's reads now overlap the storage call of the commit before it. The runs still do not batch (1.00 commits per call): a run reads records and entries from Storage on the line, and a read on the line holds the line for its round trip, so the 128 conversations' jobs take turns. 9.3 s for 128 runs amounts to about 2,300 round trips of 4 ms in sequence, about 18 a run, of which 3 are its commits. Upstream's 440 runs per second rest on a normal run making no storage reads (the record cache, conversation tails, the task-document index and kept context ranges), which is area publication-caches' (below).
- **Memory storage at 0 ms costs more CPU per run after**: 4 to 32 % fewer runs per second, the most at 128 conversations. This was not profiled; the scheduler now records and touches the keys of every decision, computes each commit's membership keys and starts the reservation passes a publication kicks on the thread pool. Upstream reports up to 25 % fewer runs per second for in-memory storage with no latency. The SQLite rows move both ways within the run-to-run spread.
- **Nested tool calls and fan-out are unchanged** (`nested-tools`, `scheduler-fanout`; before `5f7043a5`, after this branch, one run each, and a second fan-out run at 4,000 and 8,000 calls). Commits are equal (fan-out 765 to 24,015; nested calls 614, 696, 6,014 and 6,817), except one JSONL run of 200 calls at depth 2 that stored 695 commits against 696 before. Timings stay within the spread between runs: fan-out at 4,000 calls 2,060 and 2,936 ms before, 2,690 and 3,048 ms after; 2,000 nested calls at depth 2, 2,480 to 2,600 ms on SQLite and 7,326 to 7,360 ms on JSONL.

**Area publication-caches** (the read caches, conversation tails, the task-document index, kept context ranges and the publication order; the cache part of Pi `e5c6182ba`). Two runs of the default `storage-throughput` (all rows), on 2026-11-05, same machine; *before* is the *after* column of the table above it (area group-commit-scheduler's branch; for the Session rows the first table's *after*, one run), *after* is area publication-caches' branch. The benchmark now also counts Storage reads (every `LatencyStorage` read call, including SQLite's, where it adds no round trip) per commit or run. Each cell gives run 1, run 2; "after / before" compares the means.

Harness runs:

| backend | round trip ms | conversations | before: runs per s | after: runs per s | after: median run ms | after: commits per call | after: reads per run | after / before |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| memory | 0 | 1 | 549.6, 506.0 | 471.7, 488.2 | 0.7, 0.7 | 1.00, 1.00 | 0.01 | 0.91 |
| memory | 0 | 8 | 3,329, 2,444 | 2,926, 2,908 | 2.3, 2.4 | 1.00, 1.00 | 0.06 | 1.01 |
| memory | 0 | 32 | 2,833, 2,089 | 2,579, 2,956 | 11.9, 10.1 | 1.00, 1.00 | 0.25 | 1.12 |
| memory | 0 | 128 | 2,067, 1,838 | 1,367, 1,883 | 50.4, 33.6 | 1.00, 1.00 | 1.00 | 0.83 |
| memory | 4 | 1 | 11.2, 11.2 | 58.1, 58.0 | 17.2, 17.2 | 1.00, 1.00 | 0.01 | 5.2 |
| memory | 4 | 8 | 13.7, 13.7 | 154.5, 152.8 | 50.0, 53.5 | 2.37, 2.37 | 0.06 | 11.2 |
| memory | 4 | 32 | 13.8, 13.8 | 584.8, 607.5 | 47.6, 47.3 | 8.20, 8.70 | 0.25 | 43.2 |
| memory | 4 | 128 | 13.8, 13.8 | 1,798, 1,820 | 62.3, 57.8 | 32.17, 32.17 | 1.00 | 131 |
| sqlite | 0 | 1 | 597.9, 666.4 | 703.1, 619.6 | 1.2, 1.2 | 1.00, 1.00 | 0.01 | 1.05 |
| sqlite | 0 | 8 | 790.7, 820.0 | 968.6, 976.2 | 6.9, 6.9 | 1.04, 1.05 | 0.06 | 1.21 |
| sqlite | 0 | 32 | 770.3, 863.9 | 1,044, 1,130 | 28.4, 26.1 | 1.18, 1.08 | 0.25 | 1.33 |
| sqlite | 0 | 128 | 831.2, 958.5 | 1,378, 1,129 | 87.4, 82.6 | 2.78, 1.47 | 1.00 | 1.40 |

Session commits (entry appends):

| backend | round trip ms | conversations | before: commits per s | after: commits per s | after: commits per call | after / before |
|---|---:|---:|---:|---:|---:|---:|
| memory | 0 | 1 | 45,559 | 46,032, 44,317 | 1.00, 1.00 | 0.99 |
| memory | 0 | 8 | 129,097 | 118,818, 120,323 | 1.00, 1.00 | 0.93 |
| memory | 0 | 32 | 121,255 | 112,222, 106,600 | 1.00, 1.00 | 0.90 |
| memory | 0 | 128 | 130,240 | 99,354, 114,629 | 1.00, 1.00 | 0.82 |
| memory | 1 | 1 | 480 | 936, 934 | 1.00, 1.00 | 1.95 |
| memory | 1 | 8 | 963 | 4,238, 4,196 | 4.61, 4.57 | 4.38 |
| memory | 1 | 32 | 972 | 18,398, 19,123 | 22.26, 23.27 | 19.3 |
| memory | 1 | 128 | 969 | 49,025, 53,022 | 73.14, 73.14 | 52.7 |
| memory | 4 | 1 | 124 | 247, 247 | 1.00, 1.00 | 1.99 |
| memory | 4 | 8 | 247 | 1,117, 1,080 | 4.53, 4.38 | 4.45 |
| memory | 4 | 32 | 248 | 5,158, 5,381 | 21.33, 22.26 | 21.2 |
| memory | 4 | 128 | 248 | 17,234, 20,284 | 73.14, 85.33 | 75.6 |
| sqlite | 0 | 1 | 9,847 | 11,896, 12,920 | 1.00, 1.00 | 1.26 |
| sqlite | 0 | 8 | 11,176 | 12,878, 13,045 | 1.00, 1.00 | 1.16 |
| sqlite | 0 | 32 | 12,733 | 11,847, 13,189 | 1.00, 1.00 | 0.98 |
| sqlite | 0 | 128 | 9,979 | 13,079, 11,013 | 1.00, 1.00 | 1.21 |

- **A Harness run reads from Storage once per conversation, in its first run: the scan of its entries for its first context, which the kept range then serves.** 0.01, 0.06, 0.25 and 1.00 reads per run are 1, 8, 32 and 128 reads for 128 runs (the reads were traced to `ScanEntriesAsync` once while measuring). Before, a run made about 15 reads on the line (the table above estimates 18 round trips a run, 3 of them its commits). Every later run of a conversation reads nothing, which is durable 1.5.0's Changed#4 ("a normal agent run makes no storage reads") as this benchmark measures it.
- **Harness runs at 4 ms batch now**: 2.4 to 32 commits per call with 8 to 128 conversations, and 13.8 runs per second at 128 conversations become about 1,800 (median run 8,020 ms becomes about 60 ms). One conversation still pays its commits' round trips in sequence (four commits a run, 17.2 ms). Upstream's changelog quotes about 440 runs per second for 128 conversations at 4 ms (median run 0.28 s) with a probe that is not in the repository. PiSharp's row is a different workload, 128 runs of one faux text reply, one per conversation, timed over about 70 ms, so the two numbers are not comparable and **no parity is claimed**; what both show is that the runs no longer take turns on Storage round trips.
- **Session entry appends batch now** (73 commits per call at 128 conversations, as session changes did already): `AppendEntryAsync` finds its conversation in the record cache and the newest entry in the conversation tails, so it reads nothing (0.00 reads per commit in every row). 248 commits per second at 4 ms with 128 conversations become 17,234 and 20,284. One conversation gains about twice, as each commit no longer pays a read's round trip before its own. Session changes (not tabulated) move within -10 to +23 % of the first table's single *after* run, as they read nothing before either, except one conversation at 0 ms: 73,888 per second in the first table, 50,441 and 48,977 here (each sample times about 10 ms; not profiled).
- **Memory storage at 0 ms is not faster with the caches.** A MemoryStorage read costs microseconds, so serving it from memory saves little, while every publication now applies its batch to the caches and the tails and every read takes the cache's lock. The Harness rows at 0 ms land within the spread of the run before (0.83 to 1.12) and stay below the barrier mode measured before area group-commit-scheduler (for 128 conversations 2,700 and 3,014 runs per second there, 1,367 and 1,883 here); Session entry appends lose up to 18 % at 128 conversations. This was not profiled. Upstream's changelog reports up to 25 % fewer runs per second for in-memory storage with no latency.
- **Local SQLite gains 21 to 40 % with 8 or more conversations**, batching 1.04 to 2.78 commits per call, and SQLite entry appends 16 to 26 % except at 32 conversations. Upstream's changelog reports about 30 % more runs per second with local SQLite at 128 conversations; that is the same direction, not a measured comparison.
- **SQL Server storage (decision 0007) is not measured**: StorageBench has no MSSQL backend, and adding one gives the benchmark project a new project reference (PiSharp.Durable.MSSQL) and a packages.lock.json change, which this area does not make. Its network round trips are what the 1 and 4 ms rows stand in for.

## Throughput target

Upstream's handoff (`docs/pico-v5-handoff.md`, "Tool output benchmark") sets one throughput target. A plain `cat` of a 1 GiB file of unique lines should complete in about 0.4 s through the ported bash tool and the real Harness path: env capture and spill, `api.output`, throttled `pi.live` commits and the result entry. That target was measured on an Apple M5 Max. `docs/spec.md` sets no numeric targets; it leaves schema, WAL cadence and synchronous defaults to "conformance, reopen, query-plan, and storage benchmarks".

| | memory | SQLite | JSONL |
|---|---:|---:|---:|
| upstream, Node | 1.78 s | 1.75 s | 1.74 s |
| PiSharp | 2.30 s | 2.25 s | 2.57 s |

**Neither runtime meets the 0.4 s target on this Windows machine.**

- A bare Git Bash `cat <file> | wc -c` of the same file alone takes 0.38 to 0.58 s here, so the pipe from bash costs about the whole target.
- The Harness path adds about 1.3 s under Node and about 1.9 s in PiSharp.
- **PiSharp misses the target by a further 30 to 45 % over upstream.** Its firehose numbers suggest the gap is in `api.output` and the environment's capture and spill, not in storage. Each backend's commit count for the `cat` (12 to 14) is close to upstream's.
- The second half of the handoff's requirement holds in both runtimes: driving `api.output()` directly with 1 GiB in 64 KiB chunks scales linearly and does not grow with the retained window.
- This is a Windows measurement. Linux and macOS have not been benchmarked.
