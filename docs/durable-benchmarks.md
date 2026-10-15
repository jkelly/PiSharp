# pi-durable benchmarks: PiSharp and upstream

Report only. These numbers are a snapshot of one machine; nothing in CI passes or fails on them. The `benchmarks.smoke` case in `tests/PiSharp.Durable.Tests` runs every benchmark at a tiny size so the ports keep working, and asserts completion only.

PiSharp ports four upstream benchmarks of Pi v1.2.0 (`06a466935536f8c9bcacb2d5efc0fc021be1bdff`) in `benchmarks/PiSharp.Durable.Benchmarks`, over the storage benchmark kit `src/PiSharp.Durable/Testing/StorageBenchmark.cs` (upstream `src/testing/storage-benchmark.ts`):

| upstream | PiSharp | what it measures |
|---|---|---|
| `test/storage.bench.ts` (`npm run bench:storage`) | `StorageBench.cs` (`storage`) | read, write and reopen latency of memory, SQLite and JSONL storage |
| `test/storage-memory.ts` (`npm run bench:storage:memory`) | `StorageMemory.cs` (`storage-memory`) | heap, RSS and disk footprint after the seed, one child process per backend and scale |
| `test/tool-output-bench.ts` (`npm run bench:tool-output`) | `ToolOutputBench.cs` (`tool-output`) | the tool task, progress throttle and `pi.live` commits at low, normal and high output rates, the 1 GiB firehose and `cat` |
| `test/document-append-bench.ts` (`npm run bench:document-append`, new in v1.2.0) | `DocumentAppendBench.cs` (`document-append [N,...]`) | one commit per appended item: one array in one document against one family member per item |

Run them with `dotnet run -c Release --project benchmarks/PiSharp.Durable.Benchmarks -- [storage] [storage-memory] [tool-output] [document-append [N,...]]`. There is no benchmark library and no new dependency. The timing loops mirror upstream's.

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
