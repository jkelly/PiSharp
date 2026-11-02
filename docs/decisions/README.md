# Decisions

The owner's decisions that set PiSharp's scope, targets and designs, newest last. Each record states its status and the branch it applies to. A later record that supersedes part of an earlier one says so in its status line; the earlier record is not rewritten.

| No. | Decision | Applies to |
| --- | --- | --- |
| [0001](0001-initial-implementation-scope.md) | Initial implementation scope: the offline .NET 10 stream seam and the pinned Pi source | `main` |
| [0002](0002-pi-1.1.0-sync.md) | Pi v1.1.0 sync | `main` |
| [0003](0003-codemode-javascript-engine.md) | Codemode JavaScript engine | `main` |
| [0004](0004-full-parity-owner-decisions.md) | Owner decisions for full Pi v1.1.0 parity | `main` |
| [0005](0005-pi-durable-experimental.md) | Experimental port of pi-durable (released as experimental in 1.2.0.1) | `main` |
| [0006](0006-pi-1.2.0-sync.md) | Pi v1.2.0 sync | `main` |
| [0007](0007-media-generation.md) | Media generation, embeddings and model operations (released as experimental in 1.2.0.2). [SQL Server storage for durable sessions](0007-durable-sql-server-storage.md) (experimental, not packed) uses the number too | `main` |
| [0008](0008-durable-network-host-and-web.md) | Durable network: device host, device server and web UI (the device server released as experimental in the 1.2.0.2 tool) | `main` |
| [0009](0009-recurring-jobs-and-execution-history.md) | Recurring jobs, scoped triggers, execution history and audits | `main` |
| [0010](0010-durable-application-contracts.md) | Durable application contracts (F1) | `main` |
| [0011](0011-web-agent.md) | PiSharp Web Agent: a local chat app and model playground on durable sessions | `main` |
| [0012](0012-custom-agents.md) | Custom agents, workflows and subagents on the durable harness (released as experimental in 1.2.0.2) | `main` |

Technical notes kept beside the numbered records:
- [ecmascript-json-boundary.md](ecmascript-json-boundary.md): the explicit boundary between PiSharp's native JSON values and JavaScript's parsed JSON.
