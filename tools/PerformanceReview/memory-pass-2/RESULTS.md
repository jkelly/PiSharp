# Memory optimization pass 2

Previous PiSharp means the previous optimization pass. Pi is the installed **1.1.0**, using Node **24.19.0**; PiSharp is .NET **10.0.11**, Release. The offline HTTP/SSE benchmarks verify every final message and use identical 2,000 × 32-character delta streams.

| Process RAM (MiB) | Previous PiSharp | Current PiSharp | Pi 1.1.0 |
|---|---:|---:|---:|
| Completions cold peak, median of 5 | 71.4 | 62.5 | 64.3 |
| Completions after 30 streams + GC, median of 3 | 108.9 | 84.9 | 77.8 |
| Mistral cold peak, median of 5 | 67.1 | 56.7 | 53.3 |
| Mistral after 30 streams + GC, median of 3 | 100.4 | 93.9 | 69.4 |

Both cold and sustained RSS fell. Completions cold peak is now close to Pi; **sustained RSS remains higher than Pi for both providers**. These provider benchmarks do not measure the full interactive CLI.

| .NET allocation per 2,000-delta stream | Previous | Current | Reduction |
|---|---:|---:|---:|
| openai-completions-http-stream | 29.4 MiB | 17.3 MiB | 41.1% |
| mistral-conversations-http-stream | 19.1 MiB | 9.0 MiB | 53.1% |

Allocation totals measure churn, not retained RAM. Timing in this campaign (before → after → Pi):

- openai-completions-http-stream: 21.44 ms → 19.35 ms → 13.16 ms.
- mistral-conversations-http-stream: 11.73 ms → 8.68 ms → 6.59 ms.

## Profile and fixes

- Mistral copied the entire remaining 4 KiB read buffer after each small frame. A consumed offset now advances through the buffer, compacting once per read. Single-line parsing avoids regex/LINQ arrays. Valid Unicode text avoids a redundant sanitization copy.
- Completions repeatedly built/serialized/parsed identical snapshots. It now reuses one frame capture and its immutable owned JSON, omits the message before cloning descriptors, and uses existing message/content builders before final strict admission.
- JSON writes use one growable byte buffer instead of the writer/MemoryStream copying path. Duplicate-key validation holds the first eight names inline, using a hash set for larger objects.
- Generic SSE decoding uses spans for single-chunk lines, allocating only retained event names/IDs. Existing multiline/fragmented behavior remains covered.

30-stream allocation sampling, openai-completions-http-stream: 889.3 → 526.3 MiB. Sampling includes startup and is approximate; exact per-stream allocation appears above.

30-stream allocation sampling, mistral-conversations-http-stream: 576.0 → 271.9 MiB. Sampling includes startup and is approximate; exact per-stream allocation appears above.

Remaining Completions allocation leaders are JSON writing/decoding and owned source snapshots. Mistral now spends its largest shares on framing strings, parsing and owned JSON validation rather than repeated remainder copies.

## Remaining RAM gap

- openai-completions-http-stream: live post-GC heap 3.6 MiB (Pi 11.4 MiB), while .NET holds 39.1 MiB of GC committed space. Sustained RSS ranges 84.8–86.5 MiB; Pi 77.7–79.4 MiB.
- mistral-conversations-http-stream: live post-GC heap 1.8 MiB (Pi 8.1 MiB), while .NET holds 48.4 MiB of GC committed space. Sustained RSS ranges 93.9–93.9 MiB; Pi 69.2–69.9 MiB.

Live heap is broadly stable at the 10/20/30-stream checkpoints. These runs point to GC space retained for reuse, plus runtime/native memory, as the largest remaining resident-memory cost; they do not establish absence of leaks in other workloads. No forced GC, global GC tuning, pooling of mutable snapshots or reduced limits were added to production.

## Behavior verification

- Frozen source captures: 38 anchored-tool + 184 thinking scenarios passed; 409 turns and 4,020 complete publications, matching snapshot shape/order and serialized values.
- Mistral 60/60; sessions 138/138; CLI JSON 17/17; selected interactive/component/startup cases 55/55 passed.
- Transport: 219 passed / 24 failed, exactly the existing failure IDs. Compatibility: 224 passed / 10 failed on both previous/current production sources, identical failure IDs. One existing failure requires a missing released npm catalog artifact. These suites are not fully green.
- Added allocation ceilings, fragmented-read/frame-limit checks, 9/12-KiB SSE line/buffer reuse checks and duplicate-key/depth controls. Release builds and whitespace checks passed.

See results.json for measurements, ranges, profile summaries, failure IDs and reproduction steps. Raw JSONL captures and allocation profiles are alongside it. The raw suite reports and restore-previous-pass.patch, which reconstructs this pass’s production baseline in an isolated candidate copy, are in commit 217b926 (git show 217b926:tools/PerformanceReview/memory-pass-2/<name>).
