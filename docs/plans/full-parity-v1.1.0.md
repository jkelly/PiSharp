# Plan: full feature parity with Pi v1.1.0

Owner request (2026-10-08): implement every Pi v1.1.0 feature PiSharp is missing. The
baseline stays Pi v1.1.0 (`abe508e1b89912adde45528136c3221eb69acdd7`), so the result ships
as C#-only patch releases (1.1.0.x). Out of scope, unchanged from
[decision 0002](../decisions/0002-pi-1.1.0-sync.md): `pi-durable`, `pi-env`, the
experimental server/client modes and Nix packaging.

The gaps come from the [Parity page](../../website/src/data/parity.json) assessment of
2026-10-08, the v1.1.0 surface inventories
(`compatibility/coding-agent-command-rpc-inventory.v1.1.0*.json`,
`compatibility/extensions/event-catalog.v1.1.0*.json`) and
`compatibility/sync-1.1.0-applicability.json`. Each work package below also starts with its
own inventory of every upstream surface in its area, so nothing the page missed is dropped.

Progress is tracked in [full-parity-progress.json](full-parity-progress.json).

## Implementation rules (every package)

- **Upstream is the specification.** Read the read-only clone with
  `git -C <clone> show v1.1.0:<path>`; never modify it. Behaviour, texts, defaults and file
  locations follow upstream v1.1.0 exactly unless a deviation is recorded with a reason.
- **Headers.** Ported files start with
  `// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): <upstream path>.`
- **Tests.** Authored console suites that print a JSON report, never labelled as upstream
  captures, with fake HTTP/servers and pinned full request bodies where a wire is involved.
  New suites go under `/tests/parity/` in `PiSharp.slnx` and commit their own
  `packages.lock.json` (as the sync suites do).
- **Build.** `TreatWarningsAsErrors`; dense code in the surrounding style. Build and run from
  the folder pinning SDK 10.0.400 with
  `-p:RestoreLockedMode=false -p:RestoreSources=https://api.nuget.org/v3/index.json`.
  Delete untracked lock files a restore creates; revert tracked ones it rewrites.
- **Dependencies.** No new NuGet packages except Jint (codemode, decision 0003). If a feature
  cannot be done without one, stop and report it as an owner decision.
- **Protected files** (the coordinator updates them at integration): `compatibility/*`,
  `PUBLIC-DERIVATIVE-INTEGRITY.json`, `tools/PublicDerivativeIntegrity.mjs`,
  `tools/native-companion-*`, `.github/workflows/*`, `fixtures/`, `website/`, every
  `source-inventory.json`.
- **Security posture.** Follow upstream where the owner has decided so (MCP servers in
  `mcp.json` are trusted). Any other relaxation of PiSharp's file, bash or extension
  policies is an owner decision: implement behind the existing policy and report it.
- **Regression gate.** The release suites (AzureResponses, MistralConversations,
  AnthropicAuthSync, McpSync, ProviderSync, WireSync, CliSync, ToolSearch) plus Rpc.Tests and
  Sessions.Tests must stay green. Record before/after for any other suite you touch.
- **Report.** Upstream evidence (paths, lines, blob SHAs), changes, tests and results,
  deviations, anything unfinished, worktree/branch/commits.

## Waves

Packages in one wave run in parallel in separate worktrees off `parity/full`; a wave is
merged and gated before the next starts, because later waves build on earlier ones and
share CLI files.

| Wave | Packages |
|---|---|
| 1 | IMPL-A1 provider APIs and auth, IMPL-A2 catalogs and model selection, IMPL-B classifiers and images, IMPL-C codemode, IMPL-D tools |
| 2 | IMPL-F CLI, configuration and resources, IMPL-G events, session features and extension events, IMPL-H MCP completion |
| 3 | IMPL-I interactive mode and slash commands, IMPL-E extensions, Node bridge and packages |
| 4 | Integration: full gate, parity re-assessment, docs, website, releases (owner confirms each tag) |

---

## IMPL-A1: Provider APIs and authentication

**Objective.** Every chat API in `packages/ai/src/api` streams natively.

**Upstream.** `api/bedrock-converse-stream.ts`, `api/openai-codex-responses.ts`,
`api/github-copilot-headers.ts`, `api/cloudflare*.ts`, `api/pi-messages.ts` (check the
existing port), `providers/amazon-bedrock.ts`, `providers/openai-codex.ts`,
`providers/github-copilot.ts`, `providers/cloudflare-*.ts`, `providers/radius*.ts`,
`providers/opencode*.ts`, `src/auth/**` (OAuth flows for Codex/ChatGPT, GitHub Copilot device
login and any other provider login), `env-api-keys.ts`.

**Steps.**
1. Inventory every API and provider-specific request/auth behaviour against PiSharp.
2. Bedrock Converse stream: AWS SigV4 signing implemented natively, the AWS credential chain
   upstream uses (env, shared profile/credentials files, SSO token cache if upstream reads
   it), AWS event-stream binary framing, tool use, thinking, caching, errors and retries.
3. OpenAI Codex Responses with ChatGPT OAuth (login, refresh, account header).
4. GitHub Copilot headers and device-code login; Cloudflare Workers AI / AI Gateway; any
   other provider-specific header, attribution or auth logic.
5. `/login` providers beyond Anthropic, sharing `auth.json` as upstream does.
6. Expose each through `NativeProviderFactory` and the resolved/authenticated factories, and
   register each API with the CLI live route (`LiveSessionSelection`) for its providers.

**Validation.** New suite `tests/PiSharp.ProviderApis.Tests`: SigV4 against AWS's published
test vectors, event-stream decoding (split frames, CRC failures), full Bedrock/Codex/Copilot
request bodies and headers, login and refresh flows on fake servers, live-route selection.

## IMPL-A2: Model catalogs and model selection

**Objective.** Every provider and model upstream ships can be selected and configured.

**Upstream.** `providers/*.models.ts` and `providers/*.ts` (all ~40 providers),
`providers/all.ts`, `models.ts`, `model-catalog.ts`, `core/model-registry.ts`,
`core/model-resolver.ts`, `core/model-config.ts`, `core/models-store.ts`,
`core/virtual-models.ts`, `core/remote-catalog-provider.ts`,
`modes/interactive/model-catalog-refresh.ts`, `cli/list-models.ts`, `core/resolve-config-value.ts`.

**Steps.**
1. Copy every provider catalog shard byte-for-byte from the pi-ai 1.1.0 package (same
   provenance rule as the existing shards: record the tarball SHA256, never hand-edit).
2. Environment API key map for every provider; `models.json` custom providers and models
   with upstream's validation and merge rules; `!command` and env-reference config values as
   upstream resolves them (report if this conflicts with PiSharp's refusal of stored
   `!command` keys; that is an owner decision).
3. Model resolution: `--model` patterns (`provider/id`, fuzzy, `:thinking` suffix),
   `--models` scoped lists, `enabledModels`, default model selection and fallback.
4. `--list-models` output exactly as upstream; remote catalog refresh if upstream enables it
   by default.
5. CLI live routes for every provider whose API exists (openai-completions-compatible ones,
   Google, Vertex, Pi Messages); A1 adds its own APIs.

**Validation.** New suite `tests/PiSharp.Models.Tests`: catalog shard hashes, resolver cases
ported from upstream tests, `models.json` merge, `--list-models` text, live-route selection
for one model per provider with fake HTTP.

## IMPL-B: Classifiers and image generation

**Objective.** Port `gap.classifiers-images`.

**Upstream.** `api/openai-decisions.ts`, `api/classifier-shared.ts`,
`api/system-one-shared.ts`, `api/typesafe-system-one.ts`,
`api/cloudflare-workers-ai-system-one.ts`, `api/llama-cpp-classify.ts`,
`api/openrouter-images.ts`, `providers/images/**`, the classifier/image parts of `types.ts`,
`models.ts`, `core/model-registry.ts` (`classify`, `generateImages`) and the extension API.

**Steps.** Port each API as a plain HTTP client in `PiSharp.AI`; `ClassifierContext`/
`ClassifierResult` and image contracts; the registry facade and extension contract types;
catalog entries become callable.

**Validation.** New suite `tests/PiSharp.Classifiers.Tests` with pinned request bodies and
response mapping for every API, including image input and log-probability classification.

## IMPL-C: Codemode on Jint

**Objective.** Port `gap.codemode` as planned in `post-port-pi-v1-upgrade.md` Phase 4c.

**Upstream.** `packages/codemode/src/**`, `packages/coding-agent/src/extensions/codemode/**`,
codemode parts of `extensions/mcp/index.ts`, `agent-session.ts` and settings; tests
`packages/codemode/test/**`, `test/suite/agent-session-codemode.test.ts`.

**Steps.** New project `src/PiSharp.Codemode` (Jint, pinned exactly, admitted in
`THIRD-PARTY-NOTICES.md` and the license evidence by the coordinator; report what is needed):
fresh engine per script on its own thread with memory, recursion, deadline and cancellation
limits and no CLR access; embedded prelude with lockdown; declarations, `// @options:`,
output framing and limits, `image()`, store persisted as a session custom entry, `models.*`
bridge to IMPL-B, nested tool calls through the shared invoker; the `codemode` tool with
`codemode.mode`, `inlineBudget`, auto-enable for codemode MCP servers, loadout rewriting,
grammar sampling and a renderer; MCP `codemode` and `codemode-deferred` servers connect.

**Validation.** New suite `tests/PiSharp.Codemode.Tests` porting the relevant
`sandbox.test.ts` and session cases: limit boundaries and one past each, infinite print
cancellation, caught limit errors, image validation, store branching, MCP codemode servers.

## IMPL-D: Built-in tools

**Objective.** The seven built-in tools behave as upstream.

**Upstream.** `packages/coding-agent/src/core/tools/**`, `core/bash-executor.ts`,
`core/exec.ts`, `core/output-guard.ts`, the tool parts of `settings-manager.ts`.

**Steps.** `read` images (detection, size limits, resizing as upstream; report if resizing
needs a dependency), binary handling and `structuredContent`; `bash` default shell
discovery per platform, `shellCommandPrefix`, `user_bash` extension interception, spill and
truncation parity; `edit`/`write`/`grep`/`find`/`ls` audit against upstream; tool settings.

**Validation.** Extend `tests/PiSharp.Tools.Tests` (or a new `tests/PiSharp.ToolParity.Tests`)
with upstream's tool test cases.

## IMPL-F: CLI, configuration and resources

**Objective.** `pisharp` accepts Pi's command line and finds Pi's files on its own.

**Upstream.** `cli/args.ts`, `cli/*.ts`, `main.ts`, `modes/print-mode.ts`,
`modes/json-event.ts`, `core/settings-manager.ts`, `core/resource-loader.ts`,
`core/system-prompt.ts`, `core/skills.ts`, `core/prompt-templates.ts`,
`core/session-manager.ts`, `core/session-cwd.ts`, `core/project-trust.ts`,
`core/trust-manager.ts`, `cli/file-processor.ts`, `cli/initial-message.ts`.

**Steps.** Every flag in `args.ts` (including `-p`, `--mode`, `--version`, `--api-key`,
`--system-prompt`, `--append-system-prompt`, `--continue`, `--resume`, `--session`,
`--fork`, `--session-id`, `--session-dir`, `--no-session`, `--thinking`, `@file` arguments);
plain `pisharp` starts interactive mode with upstream's default session directory; global
and project `settings.json` discovery and merge; `AGENTS.md`/`CLAUDE.md` context files,
`SYSTEM.md`/`APPEND_SYSTEM.md`, skills and prompt-template folder discovery; project trust;
the existing `session <verb>` commands keep working.

**Validation.** New suite `tests/PiSharp.CliParity.Tests`: argument parsing cases ported
from upstream tests, print and JSON mode output byte-for-byte, resource discovery and
precedence, settings merge, session directory layout.

## IMPL-G: Events, session features and extension events

**Objective.** The event stream, session features and native extension events match.

**Upstream.** `core/agent-session.ts`, `core/session-manager.ts`, `core/compaction/**`,
`core/session-export.ts`, `core/export-html/**`, `modes/interactive/session-share.ts`,
`core/usage-totals.ts`, `core/cache-stats.ts`, `core/extensions/**`, `modes/rpc/**`.

**Steps.** `entry_appended`, `thinking_level_changed`, `extension_error`,
`summarization_retry_*`; auto-retry with `willRetry`; compaction retry; SIGTERM/SIGHUP;
session naming; HTML export and gist sharing; usage totals; dispatch every event in the
v1.1.0 event catalog to native extensions (29 not yet dispatched).

**Validation.** Extend WireSync/Rpc/Sessions or add `tests/PiSharp.SessionParity.Tests`;
every catalog event has a native dispatch test.

## IMPL-H: MCP completion

**Objective.** Close the remaining `mcp.exposure-and-prompt` and `gap.tool-search`
deviations.

**Steps.** Project `.pi/mcp.json` with upstream's trust flow; 10 s cap for direct servers;
resource tools for background servers; `mcp_servers` section on the live routes;
`tool_search` waits for background servers and is always registered (inactive) as
upstream; the `/mcp` manager command.

**Validation.** Extend McpSync and ToolSearch.

## IMPL-I: Interactive mode and slash commands

**Objective.** The terminal UI and every slash command behave as upstream.

**Upstream.** `modes/interactive/**`, `core/slash-commands.ts`, `core/keybindings.ts`,
`core/footer-data-provider.ts`, `core/bug-report*.ts`.

**Steps.** All slash commands (`/model`, `/scoped-models`, `/thinking`, `/settings`,
`/export`, `/share`, `/copy`, `/name`, `/session`, `/hotkeys`, `/resume`, `/fork`,
`/reload`, `/changelog`, `/mcp` and the rest); themes (built-in dark/light, custom theme
files, `--theme`); fullscreen `tuiMode`, `quietStartup`, `outputPad`; footer, selectors,
external editor, image display, first-time setup.

**Validation.** Extend the Tui and CliSync suites with terminal snapshot cases.

## IMPL-E: Extensions, Node bridge and packages

**Objective.** Pi's TypeScript extensions and packages load as upstream.

**Upstream.** `core/extensions/**`, `core/package-manager.ts`, `core/pi-manifest.ts`,
`core/resource-loader.ts`, `cli/args.ts` (`--extension`, `--no-extensions`), `pi install`.

**Steps.** Extension discovery (global and project folders, `--extension`), the Node bridge
switched on from the CLI for arbitrary extensions (not only the pinned examples), every
`ExtensionAPI` member bridged, `pi install|remove|update|list` for npm and git sources with
upstream's trust prompts.

**Validation.** Extend the Node bridge suites so they run without the pinned oracle where
possible, plus install/remove cases against a local registry fixture.

## Integration (wave 4)

Merge each wave into `parity/full`; refresh the applicability matrix, integrity pins and
companion pins; run every release suite plus the new ones; Linux release dry run; re-assess
the Parity page; update docs and website; propose release tags to the owner.
