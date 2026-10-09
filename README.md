# PiSharp

PiSharp is a native .NET 10 / C# port of the [Pi coding agent](https://github.com/earendil-works/pi). Its version names the Pi release it matches: the current baseline is Pi **v1.1.0** (commit `abe508e1b89912adde45528136c3221eb69acdd7`), and a fourth number marks a C#-only patch. The version names the porting baseline; the [Parity page](https://pisharp.ai/parity/) and [sync-1.1.0-applicability.json](compatibility/sync-1.1.0-applicability.json) record what is ported and what still differs.

Install the CLI with `dotnet tool install -g PiSharp.Cli` and run `pisharp` as you would run `pi`. It takes Pi's command line and reads Pi's files in `~/.pi/agent` and `.pi`: settings, credentials, models, MCP servers, context files, skills, prompt templates, themes, extensions and sessions. It has every Pi v1.1.0 chat API and provider catalog, the built-in tools (with images), codemode and `tool_search`, Pi's interactive mode and slash commands, print, JSON and RPC modes, and Pi's TypeScript extensions and packages through a Node bridge that installs Pi's own npm packages. Native C# extensions load from the same folders. The next release, 1.1.0.2, is described in the [release notes](docs/release-notes/1.1.0.2.md).

Documentation is at [pisharp.ai](https://pisharp.ai/docs/). The full-parity work is tracked in [full-parity-v1.1.0.md](docs/plans/full-parity-v1.1.0.md) and [full-parity-progress.json](docs/plans/full-parity-progress.json).

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

The canonical upstream is [earendil-works/pi](https://github.com/earendil-works/pi). The port is pinned to **v1.1.0**, commit [`abe508e1b89912adde45528136c3221eb69acdd7`](https://github.com/earendil-works/pi/tree/abe508e1b89912adde45528136c3221eb69acdd7); planning started from v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`.

Upstream Pi is MIT-licensed. Its [license at the planning baseline](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/LICENSE) identifies **Copyright (c) 2025 Mario Zechner**. Public upstream contracts and behavior inform this implementation; applicable attribution is retained in [third-party notices](THIRD-PARTY-NOTICES.md). No private project code or context is used.

PiSharp is an independent project; no upstream endorsement is claimed.

## Contributing

Planning feedback and documentation improvements are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes.

## License

PiSharp's original contributions are available under the [MIT License](LICENSE).
