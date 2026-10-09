---
title: Architecture
description: How PiSharp is put together.
---

PiSharp is built as layered libraries. Each one depends only on the layers below it.

Three libraries use third-party packages: `PiSharp.Tools.Skia` (SkiaSharp, for images), `PiSharp.Codemode` (Jint and its parser Acornima, for codemode scripts) and `PiSharp.PromptTemplates.Yaml` (YamlDotNet). The rest use only .NET.

| Layer | Libraries |
| --- | --- |
| Contracts | `PiSharp.Contracts` |
| Providers | `PiSharp.AI` |
| Agent | `PiSharp.Agent`, `PiSharp.Tools`, `PiSharp.Tools.Skia`, `PiSharp.Sessions` |
| Coding agent | `PiSharp.CodingAgent`, `PiSharp.Codemode`, `PiSharp.PromptTemplates.Yaml` |
| Frontends | `PiSharp.Cli`, `PiSharp.Rpc`, `PiSharp.Tui` |
| Extensions | `PiSharp.Extensions.Abstractions`, `PiSharp.Extensions.Runtime`, `PiSharp.Extensions.Agent`, `PiSharp.ExtensionHost`, `PiSharp.Compatibility.Node` (the Node bridge) |

## Design principles

- **Streaming is explicit.** Transport fragments, incremental updates and committed messages are separate. A partial tool call is for display only; it can't run until its arguments are fully parsed and validated.
- **Cancellation flows everywhere.** Cancellation tokens reach every transport, tool and hook, and each operation ends in exactly one outcome.
- **Retries are safe.** A provider retry never reruns a completed shell command or file change.
- **Sessions are append-only.** The event log is the source of truth, and the current branch and context are built from it.

The full design is in [`docs/architecture.md`](https://github.com/jkelly/PiSharp/blob/main/docs/architecture.md), and the phased plans are in [`docs/plans`](https://github.com/jkelly/PiSharp/tree/main/docs/plans).
