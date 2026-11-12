# PiSharp

**PiSharp [1.5.2](https://github.com/jkelly/PiSharp/releases/tag/v1.5.2) targets Pi v1.5.2, including v1.5.1.** The [release notes](docs/release-notes/1.5.2.md) cover the AI, CLI, Durable, Chord, terminal and extension-runtime updates and migration requirements. Source validation, package qualification and publication evidence are recorded separately.

The package set contains 28 libraries plus the CLI tool (29 packages), including the experimental `PiSharp.Chord`, `PiSharp.Chord.Hosting`, `PiSharp.Durable.Store`, `PiSharp.Env.Protocol` and `PiSharp.Env` libraries introduced in 1.5.0.1. See [package admission](docs/contracts/gap-package-admission.md) for dependencies, opt-in diagnostics and the external package-consumer gate. Native daemon ZIPs from the previous 1.5.0.1 release retain their own [release assets and qualification records](https://github.com/jkelly/PiSharp/releases/tag/v1.5.0.1); they are separate from the 1.5.2 NuGet packages. Windows PTY creation remains explicitly unsupported; live provider accounts, real SSH hosts and physical macOS key behavior retain separate qualification boundaries.

PiSharp is a native .NET 10 / C# port of the [Pi coding agent](https://github.com/earendil-works/pi), pinned to commit `b4afdddf44e059c67ccf5097e5317d913521b470`. The [accepted source validation](compatibility/sync-1.5.2-acceptance.json), [source applicability map](compatibility/sync-1.5.2-applicability.json) and [decision 0014](docs/decisions/0014-pi-1.5.2-sync.md) record scope and limitations. The earlier **1.5.0.1** release remains on Pi **v1.5.0**; its [historical applicability](compatibility/sync-1.5.0-applicability.json) and the [Parity page](https://pisharp.ai/parity/) retain their evidence. A fourth version number marks a C#-only patch.

Install the CLI with `dotnet tool install -g PiSharp.Cli --version 1.5.2` and run `pisharp` as you would run `pi`. It takes Pi's command line and reads Pi's files in `~/.pi/agent` and `.pi`: settings, credentials, models, MCP servers, context files, skills, prompt templates, themes, extensions and sessions. It provides Pi v1.5.2 chat APIs and provider catalogs, the built-in tools (with images), codemode and `tool_search`, interactive mode and slash commands, and print, JSON and RPC modes, subject to the documented compatibility differences. Native C# extensions load from the same folders. PiSharp 1.5.2 installs the pinned Pi 1.5.2 packages for TypeScript extensions under the reviewed [runtime attribution policy](website/src/content/docs/docs/extensions/node-bridge.md). The historical [1.5.0 release notes](docs/release-notes/1.5.0.md) retain that release's package inventory and limitations.

`pisharp durable` and the `PiSharp.Durable`, `PiSharp.Durable.Tools` and `PiSharp.Durable.Sqlite` packages ship as **experimental**: a port of Pi's durable agent harness whose sessions survive a crash, following `pi-durable` v1.5.2. The command, its session format and the libraries' API may change in any release, and PiSharp 1.2.0.x can't continue a session that 1.5.0 has opened; see the [durable guide](https://pisharp.ai/docs/guides/durable/). `PiSharp.Telemetry`, new in 1.5.0, is experimental too: a port of Pi's telemetry contract that records only what a host asks for and sends nothing. PiSharp's own additions also ship as experimental from 1.2.0.2: the [media](https://pisharp.ai/docs/guides/media/) library and tools, [service tiers](https://pisharp.ai/docs/guides/service-tiers/) (`--tier`, `/tier`), questions and plan mode in `pisharp durable`, and `pisharp durable serve`, which runs a durable device.

`PiSharp.CustomAgents`, with `PiSharp.CustomAgents.JavaScript` and `PiSharp.CustomAgents.Scripting`, is an experimental library on that harness, packaged from 1.2.0.2:
- custom agents in C#, with tools written in C#, JavaScript, C# scripts or plugin DLLs, which can change while the agents run;
- a ready-made `web_search` tool over Brave Search, Tavily, Firecrawl, or the hosted search of OpenAI, Anthropic, xAI or OpenRouter, and X (Twitter) search through xAI;
- workflows of typed actions over one shared Data object: code, model calls with structured output, or agent runs;
- subagents that message the orchestrator and each other.

Runs resume after a crash. [Decision 0012](docs/decisions/0012-custom-agents.md) describes the design.

Two samples run on scripted models or a real provider:
- [CustomAgents.Examples](samples/CustomAgents.Examples) runs the library's features one by one.
- [CustomAgents.Research](samples/CustomAgents.Research) is a research team: a lead agent splits a topic between researcher subagents, which search X and the web in parallel, and merges their findings into one Markdown report.

Documentation is at [pisharp.ai](https://pisharp.ai/docs/). The full-parity work is tracked in [full-parity-v1.1.0.md](docs/plans/full-parity-v1.1.0.md) and [full-parity-progress.json](docs/plans/full-parity-progress.json), the Pi 1.2.0 sync in [decision 0006](docs/decisions/0006-pi-1.2.0-sync.md) and the Pi 1.5.0 sync in [decision 0013](docs/decisions/0013-pi-1.5.0-sync.md).

Explore [62 runnable agent samples](samples/agents/README.md): practical workflows, fixture demos, live model integration, and automated model/pricing and NuGet vulnerability maintainers. Each sample includes inputs, documentation, and focused verification.

## Native validation tooling

Use the .NET SDK version pinned in `global.json`. The retained [native test runner](tools/test-native.ps1) consumes separately prepared, exactly pinned source and build receipts through `SourceManifest`, `SourceManifestSha256`, `PermittedBuildReceipt`, and `PermittedBuildReceiptSha256`. It validates and runs those existing products; it does not restore, rebuild, or publish them. The [preparation tool](tools/prepare-native-products.ps1) separately requires an explicit source manifest, runtime allocation, and finite execution budget.

These historical preparation and recapture tools intentionally fail closed when their exact inputs are unavailable. Private qualification receipts are not transferable authority for this public checkout. The export includes source and portable fixture integrity checks, but does not claim that this public tree or the complete native gate was executed. A direct SDK project build is a separate operation and has not been certified by export metadata checks. See [public snapshot provenance](PUBLIC-SNAPSHOT-PROVENANCE.md) for the source basis, derivative fixture rules, and bounded historical results.

Provider fixtures distinguish authored contract cases from captured observations. Path-normalized public derivatives are labeled as such; they must not be treated as original raw wire evidence or whole-provider equivalence.

## Design

- A native C# core that runs without Node.js.
- Native C# extensions through a dedicated extension SDK.
- A Node.js bridge for Pi's TypeScript extensions; Node.js is only needed when an extension uses it.

Read the [architecture and implementation strategy](docs/architecture.md) and the [planning index](docs/plans/README.md) for the phases.

## Explicit session imports and exports

The CLI accepts explicit source and new destination paths. `session migrate` converts supported legacy records using an explicit v1 ID plan; `session copy --format native-exact` preserves valid current source bytes; `--format current-jsonl` emits current records with transformation receipts. `--format native-archive-exact` preserves a complete future or damaged source as inert archival data. Existing destinations are never overwritten. See the [command contract](docs/contracts/session-copy-command.md) for complete arguments and the [P4-05 milestone](docs/compatibility/p4-05-interoperability-milestone.md) for qualification status.

## Upstream baseline and attribution

The canonical upstream is [earendil-works/pi](https://github.com/earendil-works/pi). The current release baseline is **v1.5.2**, commit [`b4afdddf44e059c67ccf5097e5317d913521b470`](https://github.com/earendil-works/pi/tree/b4afdddf44e059c67ccf5097e5317d913521b470). The published 1.5.0.1 release retains its **v1.5.0** pin in [the preserved lock](compatibility/target.v1.5.0.lock.json); planning started from v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`.

Upstream Pi is MIT-licensed. Its [license at the planning baseline](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/LICENSE) identifies **Copyright (c) 2025 Mario Zechner**. Public upstream contracts and behavior inform this implementation; applicable attribution is retained in [third-party notices](THIRD-PARTY-NOTICES.md). No private project code or context is used.

PiSharp is an independent project; no upstream endorsement is claimed.

## Contributing

Planning feedback and documentation improvements are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes.

## License

PiSharp's original contributions are available under the [MIT License](LICENSE).
