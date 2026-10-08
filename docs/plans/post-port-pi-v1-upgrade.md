# PiSharp post-port upstream upgrade: v0.99.1 to v1.0.2

**Status: PLANNED / DEFERRED until the current v0.99.1 port is complete.**

This document saves the future upgrade backlog. It does not change PiSharp's current target, authorize implementation, assert that any listed behavior is absent from PiSharp, or claim upgrade acceptance. Audit every item against the completed port before deciding whether it needs a change, an additional test, or an explicit scope exclusion. No upgrades are implemented by this plan.

## Frozen comparison and execution gate

The current baseline is Pi **v0.99.1**, released September 29, 2026, commit [`d86654abb8862e201933517d6f1fce9f88dd117f`](https://github.com/earendil-works/pi/commit/d86654abb8862e201933517d6f1fce9f88dd117f). The candidate future target is **v1.0.2**, released October 4, 2026 at 00:56:36 UTC, commit [`cd32f7725fdbddbaecdff5b1e68491563394e0ca`](https://github.com/earendil-works/pi/commit/cd32f7725fdbddbaecdff5b1e68491563394e0ca).

The recorded comparison review covers **123 commits and 759 changed paths**, with much of the path churn in experimental durable work. These counts describe the compared refs, not PiSharp's required workload. Recheck the latest upstream release at execution time, choose the approved target explicitly, and freeze its exact commit, source hashes, licenses and reference inputs before implementation. Never silently retarget the current baseline or replace its evidence.

Official release sequence: [v0.99.2](https://github.com/earendil-works/pi/releases/tag/v0.99.2), [v1.0.0](https://github.com/earendil-works/pi/releases/tag/v1.0.0), [v1.0.1](https://github.com/earendil-works/pi/releases/tag/v1.0.1), and [v1.0.2](https://github.com/earendil-works/pi/releases/tag/v1.0.2). Review the [official comparison](https://github.com/earendil-works/pi/compare/v0.99.1...v1.0.2), pinned [coding-agent changelog](https://github.com/earendil-works/pi/blob/cd32f7725fdbddbaecdff5b1e68491563394e0ca/packages/coding-agent/CHANGELOG.md), and pinned [AI changelog](https://github.com/earendil-works/pi/blob/cd32f7725fdbddbaecdff5b1e68491563394e0ca/packages/ai/CHANGELOG.md).

For each applicable checklist, record the upstream source pin, existing PiSharp behavior, required delta, owner, test inputs and acceptance evidence. Authored tests and observed upstream behavior remain distinct. A checked box requires reviewable evidence; this saved backlog starts entirely unchecked.

## 1. Authentication and credential boundaries — highest priority

- [ ] Audit MCP issuer validation, callback state/error handling, metadata overrides, registration options and preservation of granted scopes during step-up sign-in. Cover empty optional fields without weakening issuer checks.
- [ ] Audit credential identity by server name plus URL, one-time migration from URL-only storage, and project overrides. Preserve the global/extension-only boundary for provider-token authentication; project settings must not gain that authority. Preserve HTTPS requirements with the documented loopback exception.
- [ ] Audit Anthropic workload identity federation and credential precedence, copy-code login, and ChatGPT callback-port conflicts. Keep these changes within an explicitly approved authentication slice.
- [ ] Acceptance: use fake authorization/token servers and isolated stores to reject a mismatched issuer before exchange, detect callback conflicts, preserve scope unions, separate same-URL server accounts, migrate once deterministically, reject unauthorized project overrides, and exercise federation/token/key precedence and failed or cancelled login cleanup. No live credentials are needed for this audit.

Sources: [MCP OAuth and project overrides](https://github.com/earendil-works/pi/blob/cd32f7725fdbddbaecdff5b1e68491563394e0ca/packages/coding-agent/docs/mcp.md), [provider authentication](https://github.com/earendil-works/pi/blob/cd32f7725fdbddbaecdff5b1e68491563394e0ca/packages/coding-agent/docs/providers.md).

## 2. Provider wire behavior and retry classification

- [ ] Audit Anthropic inline mid-conversation tool definitions/redefinitions and strict-schema compatibility; retain exact request/replay boundaries rather than copying an SDK upgrade into the native adapter.
- [ ] Audit thinking-level sampling for OpenAI Completions, OpenAI Responses and Azure Responses, grammar-tool replay IDs, malformed retry dates, capacity retries, Z.AI overflow classification, and Bedrock stale thinking signatures.
- [ ] Acceptance: pin full request bodies and replay transitions for tool changes, rejected strict keywords, sampling override precedence, cross-provider grammar calls and stale signatures. Test retry timing with controlled clocks, capacity versus permanent failures, malformed `Retry-After`, cancellation during backoff, and overflow normalization. Preserve cleanup and tool-execution ordering regressions.

Sources: [AI change history](https://github.com/earendil-works/pi/blob/cd32f7725fdbddbaecdff5b1e68491563394e0ca/packages/ai/CHANGELOG.md), [thinking-level sampling change](https://github.com/earendil-works/pi/pull/9776).

## 3. MCP names, discovery and namespace guidance

- [ ] Audit hyphen-to-underscore normalization, collision hash suffixes, conflicting server-name rejection, accepted namespace aliases and indirect/lazy tool discovery.
- [ ] Audit namespace descriptions/instructions and direct versus deferred exposure. Avoid unnecessary prompt changes when background servers connect; resolve indirect tools only when needed.
- [ ] Acceptance: fixtures with `read-file` and `read_file` must dispatch distinct tools; colliding server names must fail predictably. Test namespace aliases, stable suffixes, discovery ranking, late connection, changed instructions, and first-prompt behavior with unavailable indirect servers.

Source: [MCP discovery and naming changes](https://github.com/earendil-works/pi/releases/tag/v0.99.2).

## 4. Tool restoration, reload and model lookup

- [ ] Audit late MCP tool restoration after resume, newly added `defaultTools` on reload, explicit deactivation and command-line override precedence.
- [ ] Audit extension-provider default model restoration, per-session model resolution and remote model-catalog merge complexity.
- [ ] Acceptance: resume before and after server connection; discovered tools remain available without resurrecting deliberately disabled tools. Test additions/removals, reload and CLI overrides. Instrument lookups across long transcripts and larger catalogs to demonstrate bounded work and unchanged selection.

Source: [reload and lookup fixes](https://github.com/earendil-works/pi/releases/tag/v0.99.2).

## 5. Extension APIs, renderers and bridge exports

- [ ] Audit namespace instructions and prompt summaries, renderer resolver chains for unregistered tools, resumed-session versus HTML rendering consistency, command name/handler validation, and applicable optional bridge exports.
- [ ] Acceptance: overlapping renderer registrations have deterministic resolution; late MCP registration does not change collapsed output unexpectedly; terminal and HTML results share the intended policy. Invalid commands fail during extension loading. Native and optional bridge contract tests cover only the exports admitted to scope.

Sources: [extension reference](https://github.com/earendil-works/pi/blob/cd32f7725fdbddbaecdff5b1e68491563394e0ca/packages/coding-agent/docs/extensions.md), [renderer changes](https://github.com/earendil-works/pi/releases/tag/v1.0.1).

## 6. Codemode diagnostics, images and output limits

- [ ] Audit missing-member errors, recovery suggestions, prompt helpers and namespace access; distinguish membership checks from reads that now raise errors.
- [ ] Audit image base64/type/signature validation before persistence, and output limits of **16 Mi characters** and **100,000 items**. Ensure script code cannot catch a limit failure and continue emitting unbounded output.
- [ ] Acceptance: unknown tools/models produce bounded useful diagnostics; malformed images never enter session state. Test exact limits and one-over boundaries, infinite-print cancellation, a script that catches ordinary exceptions, and cleanup without subsequent admission or retained workers.

Sources: [codemode reference](https://github.com/earendil-works/pi/blob/cd32f7725fdbddbaecdff5b1e68491563394e0ca/packages/coding-agent/docs/codemode.md), [output-limit fix](https://github.com/earendil-works/pi/issues/10283).

## 7. CLI, login and terminal behavior

- [ ] Audit fullscreen defaults and regular-mode override, `quietStartup: "header"`, provider selection requiring a model, empty/trailing model-list entries, login menus and cancellation.
- [ ] Audit image formats and scrolling, Unicode layout, long-line previews and transcript memory retention across supported terminal profiles.
- [ ] Acceptance: exact CLI exits/messages and startup visibility for each setting; empty lists must not invent a model. Test return-to-menu behavior, terminal snapshots with wide characters, image-format matrices, scroll/resize stability and measured long-session memory bounds.

Sources: [settings reference](https://github.com/earendil-works/pi/blob/cd32f7725fdbddbaecdff5b1e68491563394e0ca/packages/coding-agent/docs/settings.md), [v1.0.0 release](https://github.com/earendil-works/pi/releases/tag/v1.0.0).

## 8. Catalog identities, capabilities and pricing

- [ ] Audit Cloudflare Claude dashed IDs, Together DeepSeek naming/thinking controls, NVIDIA Nemotron defaults, Cloudflare Clef classifiers and Bedrock pricing tiers.
- [ ] Acceptance: pin catalog bytes and lookup behavior, capabilities and defaults; test renamed IDs, model-type separation, missing models and tier boundaries with synthetic usage. Catalog updates alone do not qualify live provider access or pricing accuracy beyond the frozen source.

Sources: [model reference](https://github.com/earendil-works/pi/blob/cd32f7725fdbddbaecdff5b1e68491563394e0ca/packages/coding-agent/docs/models.md), [catalog and pricing fixes](https://github.com/earendil-works/pi/releases/tag/v1.0.1).

## 9. Optional Node dependencies and distribution

- [ ] Audit optional SDK/dependency changes, including `brace-expansion` **5.0.12**, and the changed npm versus managed-installer reproducibility policy. Review exact archives, transitive dependencies and notices before optional acquisition or redistribution.
- [ ] Acceptance: an explicitly admitted installed-codemode smoke verifies worker startup and cleanup. The native distribution still builds and runs without Node. Separate reproducibility checks for each supported installation route; retain failed evidence and pin actual installed payloads.

Source: [dependency and installation-policy changes](https://github.com/earendil-works/pi/releases/tag/v1.0.1).

## Preserved scope and execution order

The recorded source comparison found the stock Agent loop, session JSONL, compaction and RPC unchanged at these refs. Do not invent a migration for them; reverify the applicable source hashes when refreezing and preserve their current regression coverage. Experimental `pi-durable` is outside the approved port scope despite its large contribution to the comparison. Upstream image generation versus PiSharp Extras requires a future explicit scope decision; neither inclusion nor equivalence is implied here. Nix support is optional and needs its own applicability decision.

Execute only after the current baseline is complete:

1. Finish and accept the v0.99.1 port; preserve its evidence.
2. Recheck latest upstream, approve and refreeze the chosen target.
3. Build the source-linked applicability matrix for all nine checklists.
4. Review authentication and security boundaries first.
5. Implement and qualify applicable provider changes.
6. Qualify MCP and extension SDK changes, including optional bridges only if admitted.
7. Qualify CLI/UI and catalog changes.
8. Review packaging, optional dependencies and the full applicable regression gate.

A future upgrade is complete only with reviewed implementation and fresh acceptance on its frozen target. This document is a saved plan, not a release, migration, publication instruction or evidence of completed work.
