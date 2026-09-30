# PiSharp implementation plans

Status: planning. The tasks and acceptance tests below are proposed work, not completed implementation.

PiSharp targets C# and .NET 10, with a native plugin SDK and an optional Node.js extension bridge. Native installation and runtime must work with Node absent. The public project baseline is a fresh standalone PiSharp implementation.

The compatibility target is [Pi v0.99.1](https://github.com/earendil-works/pi/releases/tag/v0.99.1), pinned to commit `d86654abb8862e201933517d6f1fce9f88dd117f`. See the [architecture and implementation strategy](../architecture.md) for scope and core design decisions.

## Eight phase plans

1. [Freeze the compatibility target](01-compatibility-target.md)
2. [Provider layer](02-provider-layer.md)
3. [Agent loop and tools](03-agent-loop-and-tools.md)
4. [Sessions](04-sessions.md)
5. [Headless RPC then terminal UI](05-headless-rpc-terminal-ui.md)
6. [Native extension SDK](06-native-extension-sdk.md)
7. [Optional Node bridge](07-node-bridge.md)
8. [Hardening and packaging](08-hardening-and-packaging.md)

## Dependency and release rules

- Phase 1 freezes the source/artifact baseline, compatibility manifest, shared contracts and reference harness
- Phases 2 and 3 begin together once message/stream contracts are stable; phase 3 uses fake providers before the first real adapter is ready
- Phase 4 integrates the agent with durable session projections and recovery; phase 5 headless/RPC consumes those stable contracts
- Define phase 6 hook and UI contracts during phases 1 and 3. Core hook tests use doubles, so plugin loading is not a prerequisite for agent-loop implementation
- Terminal work in phase 5 and native plugin work in phase 6 can overlap through the frozen capability contract
- Phase 7 is optional and adds only the explicitly tested bridge tiers; its delay cannot block a qualified native release
- Phase 8 starts CI, security and packaging work early. Native release acceptance requires all mandatory outcomes of phases 1–6; bridge release acceptance additionally requires phase 7

## Evidence and change control

Every task needs an output artifact, an owner and test/evidence links before it is marked complete. Preserve behavioral parity, session/RPC compatibility and TypeScript source compatibility as separate claims. Keep mandatory gaps visible; do not silently narrow scope or regenerate golden fixtures. Security improvements that differ from upstream need explicit compatibility entries.

The phase estimates are non-binding human-equivalent planning ranges. Work overlaps across contracts, implementation and qualification; estimates must not be added mechanically. Re-estimate after the baseline and headless integration checkpoints.

## Shared repository conventions

- Core libraries: `PiSharp.Contracts`, `PiSharp.AI`, `PiSharp.Agent`, `PiSharp.Tools`, `PiSharp.Sessions`, `PiSharp.CodingAgent`
- Frontends: `PiSharp.Cli`, `PiSharp.Rpc`, `PiSharp.Tui`
- Extensions: `PiSharp.Extensions.Abstractions`, `PiSharp.Extensions.Runtime`, `PiSharp.Extensions.Sdk`, `PiSharp.ExtensionHost`, optional `PiSharp.Compatibility.Node`, `PiSharp.Tui.Abstractions`
- Compatibility specifications and manifests live under `compatibility/`; reference fixtures under `fixtures/`; architecture decisions under `docs/decisions/`
- Implementation paths and API signatures in these documents are proposals. They do not assert that those files or APIs already exist
