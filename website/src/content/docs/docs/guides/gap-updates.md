---
title: What's coming in 1.5.0.1
description: The gap implementation, its source guides, and the boundaries of release qualification.
---

:::note[Release candidate — unpublished]
1.5.0.1 is being prepared. The published release remains **1.5.0**. The source adds reviewed backports and native implementations on Pi 1.5.0; it does not move the upstream baseline or claim complete upstream parity. Use a source checkout for the features marked candidate below.
:::

The November 8 gap implementation covers 23 corrections and nine capability groups. The [implementation mapping](https://github.com/jkelly/PiSharp/blob/main/compatibility/gap-status-2026-11-08.json) links each area to source and tests; the [acceptance report](https://github.com/jkelly/PiSharp/blob/main/AI_DOCS/gap-implementation-results.md) separates local receipts from platform qualification.

| Area | Candidate behavior and guide |
| --- | --- |
| Chord | The v2 host, reload, jobs, links and remote routes; a distinct legacy protocol-4 runtime. [Chord and hosting](/docs/embed/chord/) |
| Durable on Chord | Shared SQLite store, followed views and composed registries, exercised by examples 35–37. [Durable.Store](/docs/embed/durable-store/) |
| Remote environments | Framing, reconnecting client, filesystem/process/watch adapters, SSH bootstrap and native daemon. [Remote environments](/docs/embed/env/) |
| Anthropic | Bounded continuation state, compatible request fingerprints, OAuth projection and prepared-request UTF-16 preservation. [Message threads](/docs/embed/anthropic-threads/) |
| Durable model requests | Per-conversation compaction, transaction lookup, thinking budgets, timeout, identity and request hooks. [Candidate options](/docs/embed/durable/#candidate-options-1501) |
| Provider correctness | Schema references and definitions, tool-history declarations, headers, retry classification, simple output budgets, Responses transcript tools, catalog updates, classifier overrides and Copilot System One routing. [Providers](/docs/guides/providers/) |
| Tools and sessions | Mixed line endings, bounded large-file reads/searches, relative globs, argument expansion, Unicode truncation, persisted thinking selection and streamed argument snapshots. [Tools](/docs/reference/tools/) |
| Extension and RPC lifecycle | MCP SSE line endings and callback refresh, joined shutdown, ordered RPC responses, hosted settings, macOS modifier queries and Node overlay handle lifecycle. [Node bridge](/docs/extensions/node-bridge/) |
| Auth and update | Shell authentication commands and accurate package update results. [CLI](/docs/reference/cli/) |
| Storage and telemetry | Corruption/refusal distinctions, bounded prepared statements, ordered SQLite bulk execution, SQLite telemetry recording and the delta tracker optimization. [Telemetry](/docs/embed/telemetry/) |
| Evaluations | Deterministic fixture and staged-runtime evaluation with per-case evidence. [Evaluation tooling](/docs/contribute/evaluations/) |

## Package and platform boundaries

The preparation inventory is **28 libraries and the CLI (29 packages)**. Five added experimental libraries—`PiSharp.Chord`, `PiSharp.Chord.Hosting`, `PiSharp.Durable.Store`, `PiSharp.Env.Protocol` and `PiSharp.Env`—are unpublished. Their [SDK entries](/sdk/) link to source and guides. The daemon is a separate native artifact.

Windows x64 local validation is recorded in the acceptance report. Hosted workflows and their artifacts qualify only their tested source revision and environment. Windows terminal creation explicitly returns `ENOTSUP`; ordinary filesystem/process operations remain available. Physical Apple Terminal keys, real SSH hosts, live provider accounts and additional RIDs require separate evidence.

Native C# plugins and framed process transports preserve documented differences from TypeScript/npm plugins and Node IPC. Browser-only and Cloudflare adapters remain exclusions. The provider differential retains an explicit Mistral callback diagnostic policy difference; it is not counted as an upstream match. Historical capture pins remain unchanged.

See [Releasing](/docs/contribute/releasing/) for the four required gates and the separate publication step.
