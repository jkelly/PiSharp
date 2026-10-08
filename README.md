# PiSharp

PiSharp is a native .NET 10 / C# port of the [Pi coding agent](https://github.com/earendil-works/pi), in early implementation.

The public version baseline is **0.99.1**, matching the pinned Pi **v0.99.1** release. This identifies the porting baseline, not complete behavioral parity or an already published binary. Fresh version-specific artifact qualification is required; earlier private packages retain their original versions. See the [0.99.1 release notes](docs/release-notes/0.99.1.md).

The development build includes a native offline CLI, durable sessions and branch/history views, session replacement, native C# extension loading, and bounded optional Node bridge workflows. Accepted session lifecycle and history milestones include fresh context replacement, old-context write rejection and joined cleanup. Provider and interoperability capabilities retain their individually tested scopes. All eight original phase gates remain open. See [implementation status](IMPLEMENTATION_STATUS.md) for current evidence and gaps.

## Native validation tooling

Use the .NET SDK version pinned in `global.json`. The retained [native test runner](tools/test-native.ps1) consumes separately prepared, exactly pinned source and build receipts through `SourceManifest`, `SourceManifestSha256`, `PermittedBuildReceipt`, and `PermittedBuildReceiptSha256`. It validates and runs those existing products; it does not restore, rebuild, or publish them. The [preparation tool](tools/prepare-native-products.ps1) separately requires an explicit source manifest, runtime allocation, and finite execution budget.

These historical preparation and recapture tools intentionally fail closed when their exact inputs are unavailable. Private qualification receipts are not transferable authority for this public checkout. The export includes source and portable fixture integrity checks, but does not claim that this public tree or the complete native gate was executed. A direct SDK project build is a separate operation and has not been certified by export metadata checks. See [public snapshot provenance](PUBLIC-SNAPSHOT-PROVENANCE.md) for the source basis, derivative fixture rules, and bounded historical results.

Provider fixtures distinguish authored contract cases from captured observations. Path-normalized public derivatives are labeled as such; they must not be treated as original raw wire evidence or whole-provider equivalence.

## Planned direction

- A native C# core that runs without Node.js.
- Native C# plugins through a dedicated plugin SDK.
- An optional Node.js bridge for interoperability; Node.js will only be needed when using that bridge.

These capabilities are being implemented and qualified against the complete original plan. Read the [architecture and implementation strategy](docs/architecture.md) and the [planning index](docs/plans/README.md) for the remaining phases.

## Explicit session imports and exports

The CLI accepts explicit source and new destination paths. `session migrate` converts supported legacy records using an explicit v1 ID plan; `session copy --format native-exact` preserves valid current source bytes; `--format current-jsonl` emits current records with transformation receipts. `--format native-archive-exact` preserves a complete future or damaged source as inert archival data. Existing destinations are never overwritten. See the [command contract](docs/contracts/session-copy-command.md) for complete arguments and the [P4-05 milestone](docs/compatibility/p4-05-interoperability-milestone.md) for qualification status.

## Upstream baseline and attribution

The canonical upstream is [earendil-works/pi](https://github.com/earendil-works/pi). Planning is pinned to **v0.99.1**, commit [`d86654abb8862e201933517d6f1fce9f88dd117f`](https://github.com/earendil-works/pi/tree/d86654abb8862e201933517d6f1fce9f88dd117f).

Upstream Pi is MIT-licensed. Its [license at the planning baseline](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/LICENSE) identifies **Copyright (c) 2025 Mario Zechner**. Public upstream contracts and behavior inform this implementation; applicable attribution is retained in [third-party notices](THIRD-PARTY-NOTICES.md). No private project code or context is used.

PiSharp is an independent project; no upstream endorsement is claimed.

## Contributing

Planning feedback and documentation improvements are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes.

## License

PiSharp's original contributions are available under the [MIT License](LICENSE).
