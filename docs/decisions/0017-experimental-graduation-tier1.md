# Decision 0017 — Tier-1 `[Experimental]` API Graduation

Status: Accepted (post-1.6.0 improvement plan, Wave 6; effective in `1.6.0.1`).

## Context

Across PiSharp `1.2.0.2` through `1.6.0`, several core API surfaces shipped behind `[Experimental("PISHARP_*")]` diagnostic IDs while their wire contracts, upstream Pi parity suites, and edge cases settled:

- `PISHARP_SCHEMA001` — `PiSharp.Contracts.Schema` (`JsonSchemaBuilder`, `JsonSchemaValidator`, `CoercingJsonSchemaValidator`, `JsonSchemaCompileOptions`, `JsonSchemaFormats`), the `pi-schema` port backing tool argument validation across `PiSharp.Agent`, `PiSharp.Durable`, and `PiSharp.Chord`.
- `PISHARP_IDENTITY001` — `ClientIdentity`, `ClientUserAgent`, `ClientAttribution`, `ClientIdentities`, and the `Identity` options on `ChatRequest`, provider options, `AgentConfiguration`, `TurnRunner`, and `ModelRequestOptions`.
- `PISHARP_TIERS001` — `ServiceTiers` and `ServiceTier` on `ChatRequest`, provider options, `AgentConfiguration`, `TurnRunner`, and `SessionRuntimeRegistryOptions`.
- `PISHARP_AI001` — streaming tool-call argument parsing in `PiSharp.AI` (`StreamingJsonParser`, `JsonChange`, `JsonSetChange`, `JsonAppendChange`, `JsonPathSegment`, `ToolCallArgumentsStream`, `ToolCallArgumentsResult`, `StreamingJson.ParseToolCallArguments`, `AssistantStreamReducer.GetToolArgumentsPreview`, `AssistantStreamReducer.FinishToolArguments`).
- `PISHARP_AUTH001` — device-code sign-in hints (`AuthEvent.OpenBrowserOnConfirm`, `AuthEvent.CopyCodeOnConfirm`).
- `PISHARP_EXEC001` — process output capture limits (`MaxBuffer`, `Truncated` in `PiSharp.Extensions.Abstractions`; `CaptureLimitBytes`, `CaptureLimitReached`, `SeparatedProcessCapture` in `PiSharp.Tools`).
- `PISHARP_TOOLSEARCH001` — `ToolSearchDocument.NamespaceText` in `PiSharp.Extensions.Abstractions`.

All seven surfaces have been stable across multiple releases (`1.2.0.2` / `1.5.0` / `1.5.2` / `1.6.0`), carry comprehensive regression suites, and are consumed directly by PiSharp's own stable packages (`PiSharp.AI`, `PiSharp.Agent`, `PiSharp.Tools`, `PiSharp.CodingAgent`, `PiSharp.Cli`). Requiring every embedding application to suppress seven distinct diagnostic IDs to use standard core capabilities creates unnecessary friction without protecting active churn.

## Decision

1. **Graduate all seven Tier-1 diagnostic IDs** (`PISHARP_SCHEMA001`, `PISHARP_IDENTITY001`, `PISHARP_TIERS001`, `PISHARP_AI001`, `PISHARP_AUTH001`, `PISHARP_EXEC001`, `PISHARP_TOOLSEARCH001`) by removing their `[Experimental]` attributes from `src/`.
2. **Keep Tier-2 experimental packages and surfaces gated** under their existing `[Experimental]` diagnostic IDs until their respective follow-up graduation decisions:
   - `PISHARP_DURABLE001` (`PiSharp.Durable`, `PiSharp.Durable.Tools`, `PiSharp.Durable.Sqlite`, `PiSharp.Durable.Store`)
   - `PISHARP_AGENTS001` (`PiSharp.CustomAgents*`)
   - `PISHARP_TELEMETRY001` (`PiSharp.Telemetry`)
   - `PISHARP_CHORD001` (`PiSharp.Chord`, `PiSharp.Chord.Hosting`)
   - `PISHARP_ENV001` (`PiSharp.Env`, `PiSharp.Env.Protocol`)
   - `PISHARP_MEDIA001` (`PiSharp.AI.Media`)
   - `PISHARP_TUI001` (`PiSharp.Tui` experimental surfaces)
3. **Preserve source compatibility** for downstream consumers who already have `<NoWarn>...;PISHARP_SCHEMA001;...</NoWarn>` in their project files: C# `<NoWarn>` ignores unused diagnostic IDs cleanly without warnings. Remove the obsolete internal suppressions from PiSharp's own `.csproj` files, `#pragma` directives, `tests/packaging/GapConsumer/GapConsumer.csproj.in`, `compatibility/public-release.json` (current release section only; historical `previousReleases` entries remain immutable), and documentation.

## Consequences

- Embedding applications using `PiSharp.Contracts.Schema`, `ClientIdentity`, `ServiceTiers`, `StreamingJsonParser`, `AuthEvent` confirm hints, `pi.exec()` buffer limits, or `ToolSearchDocument.NamespaceText` compile cleanly with zero `<NoWarn>` boilerplate.
- New ecosystem packages (`PiSharp.Extensions.AI`) and `dotnet new` templates (`PiSharp.Templates`) can use `ServiceTier`, `ClientIdentity`, `JsonSchemaValidator`, and `StreamingJsonParser` directly as stable APIs.
