---
title: Tests
description: PiSharp's test suites, what CI runs, and how to run them yourself.
---

PiSharp's tests are console programs, not xUnit or NUnit projects. Each suite under [`tests/`](https://github.com/jkelly/PiSharp/tree/main/tests) is an executable that runs its cases, prints a JSON report and exits with 0 when no case failed and 1 otherwise. The suites are offline: they use fake HTTP handlers, scripted models and fixtures, and need no provider keys.

## Run a suite

Run a suite with `dotnet run` from the repository root. Pass the restore settings from [Build from source](/docs/contribute/build/#restore-and-lock-files) the first time, while your NuGet cache is still empty:

```powershell
dotnet run --project tests/PiSharp.InteractiveParity.Tests -c Release -p:RestoreSources=https://api.nuget.org/v3/index.json -p:RestoreLockedMode=false
```

The report goes to standard output. Most suites also take `--report <path>` after `--`, and write the same report to that file:

```powershell
dotnet run --project tests/PiSharp.Codemode.Tests -c Release -- --report artifacts/codemode.json
```

The report path must not exist yet: the suites create the file and fail if it is already there. Each report lists every case with its id and status, plus counts such as cases, failures and skips. Some suites add fields of their own, such as the upstream source commit their expectations come from.

Each case has a time limit, from 10 seconds in `AzureResponses` to 240 seconds in `ExtensionParity`. A case that hangs fails at its limit instead of stopping the run.

## Run one case

Every case has a stable id, such as `changelog.sync.generated-from-website` or `harness.callers.…`. How you select cases depends on the suite:

| Suites | How to select | Match |
| --- | --- | --- |
| `Codemode`, `McpParity`, `SessionParity`, `Durable`, `DurableCli`, `CustomAgents` | `-- --only <prefix>` | Case ids that start with the prefix. You can pass `--only` or `--report`, not both. |
| `InteractiveParity` | `INTERACTIVEPARITY_FILTER` environment variable | Case ids that contain the text. Separate several with `\|`. |
| `CliParity` | `CLIPARITY_FILTER` | Case ids that contain the text. |
| `ExtensionParity` | `EXTPARITY_FILTER` | Case ids that contain the text. |
| `ProviderApis` | `PROVIDERAPIS_FILTER` | Case ids that contain the text. |
| `Rpc`, `Sessions`, `Tools`, `ExtensionHost`, `Extensions.ContractTests`, `Tui` | `-- --filter <text>` | Case names that contain the text, ignoring case. |
| `Transport` | `-- --filter <prefix>` | Case names that start with the prefix. |
| `CodingAgent` | `-- --filter <text>` or `-- --exact-name <name>` | Contains, or one exact case. |

The remaining suites run all their cases. All matching is ordinal, so case matters except where the table says otherwise.

```powershell
dotnet run --project tests/PiSharp.Durable.Tests -c Release -- --only storage.sqlite
$env:INTERACTIVEPARITY_FILTER = 'changelog.'; dotnet run --project tests/PiSharp.InteractiveParity.Tests -c Release
```

## Result statuses

| Status | Meaning |
| --- | --- |
| `PASS_AUTHORED_NATIVE_ONLY` | The case passed. Its expectation was written by reading the pinned Pi source, not captured from an upstream run. Most suites report this. |
| `PASS_UPSTREAM_CAPTURE` | The case passed against output that upstream Pi produced. The `compat.*` cases of `Durable` report this. |
| `PASS`, `passed` | The case passed. `CustomAgents` uses `PASS`; the older suites use `passed`. |
| `FAIL`, `failed` | The case failed. The suite exits with 1. |
| `SKIPPED` | `InteractiveParity`: a Unix-only case on Windows. |
| `SKIPPED_PLATFORM` | `CliParity`: a case for another platform. |
| `SKIPPED_NO_UPSTREAM` | `Durable`: the case needs a Pi checkout that `PISHARP_DURABLE_UPSTREAM` doesn't name. |
| `SKIP_NO_NODE`, `SKIP_NO_NPM`, `SKIP_NO_GIT`, `SKIP_NO_PI_RUNTIME` | `ExtensionParity`: the case needs Node.js, npm, Git or the installed Pi packages, and they are missing. |
| `SKIP`, `skipped` | `CodingAgent`: a native extension case, and `artifacts/extensions/published-fixtures` doesn't exist. |
| `HUNG`, `hung` | `CodingAgent`: the case was still running after 10 minutes. The run stops there and exits with 1. |

A skip is not a failure. Check the skip counts when you expect a case to run.

## The suites

### Release suites

The release and the macOS workflow run these. Each covers one area of Pi, with expectations written from the pinned Pi source unless the row says otherwise.

| Suite | Covers |
| --- | --- |
| `PiSharp.AzureResponses.Tests` | The Azure OpenAI Responses provider (`azure`). |
| `PiSharp.MistralConversations.Tests` | The Mistral Conversations streaming transport, replayed from fixtures. |
| `PiSharp.AnthropicAuthSync.Tests` | Anthropic authentication from the Pi v1.1.0 sync: identity federation and token exchange, reverified at Pi v1.2.0. |
| `PiSharp.McpSync.Tests` | MCP server names, tool exposure and prompts, OAuth, and `pisharp mcp`. |
| `PiSharp.ProviderSync.Tests` | Provider wire behavior: inline tools, strict keywords, sampling parameters, retries, overflow, estimates and catalog shards. |
| `PiSharp.WireSync.Tests` | The wire, extension and tool rows of the Pi v1.1.0 sync. |
| `PiSharp.CliSync.Tests` | The CLI, hidden-tools prompt and TUI rows of the Pi v1.1.0 sync. |
| `PiSharp.ToolSearch.Tests` | The built-in `tool_search` tool and deferred MCP exposure. |
| `PiSharp.Classifiers.Tests` | The classifier and image generation APIs. |
| `PiSharp.Models.Tests` | Model catalogs and model selection. `--tarball <pi-ai package>` also compares the embedded catalog shards with a published `pi-ai` package, byte for byte. |
| `PiSharp.Codemode.Tests` | [Code mode](/docs/guides/codemode/) on Jint, with declaration goldens from the upstream packages. It is also the code mode worker process its cases start. |
| `PiSharp.ProviderApis.Tests` | Provider APIs and authentication: Bedrock, Codex, Copilot, Cloudflare and AWS signing. |
| `PiSharp.CliParity.Tests` | The CLI, configuration and resources, and session file goldens in `SessionFormat/*.scenario.json`, captured from Pi v1.1.0. |
| `PiSharp.McpParity.Tests` | MCP configuration and completion. |
| `PiSharp.SessionParity.Tests` | Events, session features, export and extension events. |
| `PiSharp.InteractiveParity.Tests` | Interactive mode, slash commands, and the `CHANGELOG.md` sync. |
| `PiSharp.ExtensionParity.Tests` | Extensions, the Node bridge and packages. It builds and loads the sample extensions, and needs Node.js 22.13 or later and npm. The first run installs the Pi 1.2.0 npm packages once. |
| `PiSharp.Durable.Tests` | The [durable](/docs/guides/durable/) port, with cases ported from upstream's tests. The `compat.*` cases read sessions upstream wrote. |
| `PiSharp.DurableCli.Tests` | `pisharp durable`. |

The macOS workflow runs all of them except `Durable` and `DurableCli`, which the durable workflow runs on every OS.

### Custom agents

| Project | Purpose |
| --- | --- |
| `PiSharp.CustomAgents.Tests` | Case tables for [custom agents](/docs/custom-agents/overview/) (experimental, unreleased). |
| `PiSharp.CustomAgents.PluginFixture.V1`, `.V2` | Two versions of a plugin the tests load, reload and unload. V2 answers differently and adds a tool. |
| `PiSharp.CustomAgents.ExtensionFixture` | A native extension the tests load as an agent plugin. |

### Core suites

Most of these test one library each. They are in `PiSharp.slnx`, but no workflow runs them. `tools/test-native.ps1` runs them with the companion suites, but it needs a prepared source manifest and build receipt, and checks that Node.js is absent. Run them one at a time with `dotnet run` instead.

| Suite | Covers |
| --- | --- |
| `PiSharp.Compatibility.Tests` | Provider requests and wire formats against the frozen fixtures. Takes `--repo`, `--report` and `--require-node-absent`. |
| `PiSharp.Agent.Tests` | The agent loop, tool calls, queues and cancellation. |
| `PiSharp.Transport.Tests` | The HTTP and SSE transports. Also takes `--require-node-absent` and `--strict-completions-lifecycle`, which can't be combined with `--filter`. |
| `PiSharp.Sessions.Tests` | Session files, branches, context and compaction. |
| `PiSharp.Tools.Tests` | The built-in tools. Its process and shell fixtures are Windows-only. |
| `PiSharp.CodingAgent.Tests` | The coding agent runtime. Requires `--dotnet-host <path> --cli <path>`, the built `src/PiSharp.Cli` DLL. Its native extension cases load the packages that `tools/publish-native-extension-fixtures.ps1` publishes to `artifacts/extensions/published-fixtures`. Without that folder they skip and give the reason. Once it exists, a missing package fails its cases. |
| `PiSharp.Rpc.Tests` | The JSONL RPC mode. |
| `PiSharp.Tui.Tests` | Terminal input, rendering and leases. Requires `--dotnet-host <path>`. |
| `PiSharp.Extensions.ContractTests` | The extension contracts. `Consumer/PublishedConsumer.csproj` is a native extension built against them, as a third-party extension would be. |
| `PiSharp.ExtensionHost.Tests` | The extension worker protocol. |

### Process tests

These start real child processes and need explicit paths:

| Suite | Arguments |
| --- | --- |
| `PiSharp.ExtensionHost.ProcessTests` | `--dotnet-host`, `--node`, `--repo`, optional `--report` |
| `PiSharp.Compatibility.Node.ProcessTests` | `--dotnet-host`, `--node`, `--repo`, `--oracle`, `--jiti`, `--reference-repo`, `--run-parent`, `--report`, all required |

### Companion suites

[`tools/native-companion-targets.json`](https://github.com/jkelly/PiSharp/blob/main/tools/native-companion-targets.json) lists 32 companion targets, each a narrow suite from the v0.99.1 port. Most read fixtures captured from Pi v0.99.1 in `fixtures/reference/`, and several also need a prepared receipt.

| Projects | Cover |
| --- | --- |
| `CompletionsAnchored`, `CompletionsGrammar`, `CompletionsSimple`, `CompletionsThinking` (each with `Controls` or `Agent` projects) | The OpenAI Completions provider: anchored tools, grammars, simple options and thinking. |
| `OpenRouterBinding`, `OpenRouterCacheProfile`, `OpenRouterReasoning` (each with `Controls`) | OpenRouter routing, caching and reasoning. |
| `AnthropicSimple`, `GoogleGenerativeAI`, `PiMessages`, `Authentication` | The Anthropic, Google and Pi messages providers, and credential handling. |
| `AzureResponses`, `MistralConversations` | The two release suites above, also listed as companions. |
| `Tui.LockModifiers`, `Tui.Keypad`, `Tui.ModifyOtherKeys`, `Tui.Focus`, `Tui.IndicConjunct`, `Tui.KittyAlternate`, `Tui.Keybindings`, `Tui.SelectList` | Terminal key protocols, focus, text shaping, keybindings and the select list. |
| `Terminal.SelectList.Integration`, `Terminal.Ownership` | The select dialog and terminal ownership in the full terminal. |
| `tools/NodeCommandInputBridge/NativeAdmissionTests` | Node extension loadout metadata. |

### Other projects

These projects are not in `PiSharp.slnx`, and no workflow runs them. They are controls and slices kept from the v0.99.1 port. Three Node package suites have their own scripts in `tools/`: `test-native-node-package.ps1`, `test-native-hello-package.ps1` and `test-native-command-input-package.ps1`.

| Projects | Purpose |
| --- | --- |
| `AnthropicProfile`, `AnthropicResolved`, `AzureSimple`, `VertexSimple` | Provider controls derived from the Pi v0.99.1 source. |
| `AnthropicSdkBeta` | Native cases paired with captured Anthropic SDK beta requests. |
| `MistralOriginalOptions` | Mistral option mapping. |
| `OpenAIResponsesHooks`, `OpenAIResponsesOriginalParity`, `ResponsesGrammarFallback`, `ResponsesOrphan`, `ResponsesPublicMetadata` | The OpenAI Responses provider: hooks, grammar fallback, orphaned tool results and metadata. |
| `ProviderOfflineDifferential` | Native observations against values derived from the v0.99.1 source. |
| `ProviderComparison.Tests` | A paired comparison with the original SDK, gated by `PISHARP_ROOT_ANTHROPIC_*` variables. |
| `PiMessagesAgentContinuation` | A library the Pi messages suites share. |
| `DurableMessages` | Coding agent session messages and the native initializer loader. |
| `NativeModelCatalog`, `NativeRegistrationInstallation`, `ProfileInitializer`, `PublishedFixture.ProfileInitializer`, `RegistrationModelFacade`, `TypedSdkTool`, `SdkEventBus`, `SdkAsyncEventBus`, `SdkPluginEcho` (with `PluginA`, `PluginB`, `Qualification`), `PublicSdkConsumer.R511` | Slices of the native extension SDK: model catalogs, registration, initializers, typed tools, event buses and plugins. |
| `McpAuthorizationCode`, `McpOAuthHost`, `McpOAuthOrchestration`, `DefaultOAuthHostQualification`, `McpTuiOriginalQualification` | MCP OAuth flows and their hosts. |
| `NodePackage`, `NodeHelloPackage`, `NodeCommandInputPackage`, `NodeLifecycleSourceIdentity`, `NodeUi`, `OriginalUiQualification` | Node bridge packages, commands and UI. |
| `OriginalGrammarQualification`, `OriginalProviderQualification.Shared` | Shared qualification harness code. |
| `Terminal.Default`, `Tui.Editor` | The default terminal and the editor component. |

[`tests/fixtures/extensions/native/`](https://github.com/jkelly/PiSharp/tree/main/tests/fixtures/extensions/native) holds native extensions the extension tests load, such as plugins that fail, import system assemblies or target a future ABI. [`tests/packaging/`](https://github.com/jkelly/PiSharp/tree/main/tests/packaging) and [`tests/release/`](https://github.com/jkelly/PiSharp/tree/main/tests/release) hold PowerShell tests of the packaging and legacy release scripts.

## Fixtures and captures

A test's expected values are either authored or captured, and the reports keep the two apart:

- An **authored** expectation is written by reading the pinned Pi source. Its case reports `PASS_AUTHORED_NATIVE_ONLY`.
- A **captured** expectation is output an upstream Pi run produced, kept as a file. Its case compares PiSharp's output with that file.

The captures live in several places:

| Location | Contents | Made by |
| --- | --- | --- |
| [`fixtures/pi-v0.99.1/`](https://github.com/jkelly/PiSharp/tree/main/fixtures/pi-v0.99.1) | The v0.99.1 fixtures, with a `manifest.json` that records each fixture's kind and source. | The v0.99.1 port. |
| [`fixtures/reference/`](https://github.com/jkelly/PiSharp/tree/main/fixtures/reference) | Provider request captures the companion suites read. | The v0.99.1 port. |
| `tests/PiSharp.Durable.Tests/Fixtures/` | Upstream durable captures (`*.upstream.json`) for Pi v1.1.0, and in `v1.2.0/` for Pi v1.2.0 with a `manifest.json` and `index.json`. | `tools/DurableFixtures/capture-all.mjs` |
| `tests/PiSharp.CliParity.Tests/SessionFormat/` | Session file scenarios captured from Pi v1.1.0. | Recorded runs of Pi. |
| `tests/PiSharp.Codemode.Tests/Declarations/` | Code mode declaration goldens. | `gen-declaration-goldens.mjs` beside them. |
| `tests/PiSharp.Tools.Tests/ToolValidation/` | Tool argument validation goldens. | `gen-tool-validation-goldens.mjs` beside them. |
| InteractiveParity's Mermaid goldens | Diagram rendering goldens from the original library. | `tools/GrokMermaid/gen-goldens.mjs` |

To capture the durable fixtures again, run `capture-all.mjs` against a clean Pi checkout at the pinned commit. It needs Node.js 22.5 or later and takes `--check`, which captures twice and fails if the runs differ. Its header comment explains how to set up the checkout.

```sh
node --no-warnings tools/DurableFixtures/capture-all.mjs <pi checkout> --commit <sha> [--out <directory>] [--check]
```

:::caution[Don't regenerate goldens to make a test pass]
Never regenerate goldens automatically, and never retarget the pinned upstream commit silently. A capture changes only when the baseline changes, as part of a sync. See [How the port tracks Pi](/docs/contribute/porting/).
:::

## Environment variables

| Variable | Suite | Effect |
| --- | --- | --- |
| `PISHARP_WRITE_CHANGELOG=1` | InteractiveParity | The case `changelog.sync.generated-from-website` rewrites `CHANGELOG.md` from the website instead of failing when it differs. See [Releasing](/docs/contribute/releasing/#regenerate-changelogmd). |
| `INTERACTIVEPARITY_FILTER`, `CLIPARITY_FILTER`, `EXTPARITY_FILTER`, `PROVIDERAPIS_FILTER` | As named | Run only the matching cases. See [Run one case](#run-one-case). |
| `PI_DURABLE_DOCUMENT_RETENTION_MS` | Durable | The default document retention of every session, in milliseconds. CI runs the suite a second time with `0`, as upstream's `npm test` does. |
| `PISHARP_DURABLE_UPSTREAM` | Durable | A Pi v1.2.0 checkout with its `node_modules`, and `node` on your `PATH`. Turns on the case where upstream reads sessions PiSharp wrote. Without it, the case reports `SKIPPED_NO_UPSTREAM`. |
| `PISHARP_DURABLE_UPSTREAM_V110` | DurableCli | A Pi v1.1.0 checkout with its `node_modules`, for the session interop case. Skipped when unset. |
| `PISHARP_PI_COMPARISON=1` | InteractiveParity | The streaming benchmark case also prints a timing record. |
| `SESSFMT_DIR` | CliParity | Replay the session format scenarios from another directory. |
| `SESSFMT_DUMP=<dir>` | CliParity | Write PiSharp's session files, `events.jsonl` and stderr for each scenario to the directory. |
| `PISHARP_TERMINAL_DEFAULT_REPLAY_REPORT` | CodingAgent | Where to write the terminal replay report. |

Other `PISHARP_*` variables you may see in the test code are plumbing between a suite and the child processes it starts. Don't set them yourself.

On macOS, CI resolves `TMPDIR` before it runs a suite, because `/var/folders` is a symlink to `/private/var/folders` and the tests compare resolved paths. Do the same on a Mac:

```sh
export TMPDIR="$(cd "$TMPDIR" && pwd -P)/"
```

## Node tests

The compatibility and inventory tools have their own `node:test` files, such as `tools/CompatibilityReport/*.test.mjs` and `tools/SurfaceInventory/*.test.mjs`. Run one with `node --test <file>`. No workflow runs them.

## Continuous integration

The workflows live in [`.github/workflows`](https://github.com/jkelly/PiSharp/tree/main/.github/workflows). The .NET jobs install the SDK from `global.json` and restore from nuget.org with lock-file checking off.

| Workflow | Triggers | Runs on | What it runs |
| --- | --- | --- | --- |
| [`macos.yml`](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/macos.yml) | Pull requests and pushes to `main`; manual | `macos-14` (Apple Silicon) | The 17 release suites other than `Durable` and `DurableCli`, one job each with Node.js 22. Then it packs every package, installs the `PiSharp.Cli` tool from them, and checks `pisharp --version` and `--help`. |
| [`durable.yml`](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/durable.yml) | Pushes and pull requests to `main` and `feature/pi-durable`; manual | Windows, Ubuntu and `macos-14` | `Durable`, `Durable` again with `PI_DURABLE_DOCUMENT_RETENTION_MS=0`, and `DurableCli`. A second job publishes `Durable.Tests` for `win-x64`, `linux-x64`, `linux-musl-x64` and `osx-arm64` and checks that each carries its SQLite native library, byte for byte. A third fetches Pi and checks that `compatibility/durable-port-map.json` matches it. |
| [`custom-agents.yml`](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/custom-agents.yml) | Pushes and pull requests to `main` and `feature/custom-agents`; manual | Windows, Ubuntu and `macos-14` | `CustomAgents`, then the research sample on scripted models, checking that it writes a report. |
| [`release.yml`](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/release.yml) | A pushed `v*` tag; manual dry run | Ubuntu | All 19 release suites, then packing and publishing. See [Releasing](/docs/contribute/releasing/#the-release-workflow). |
| [`website.yml`](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/website.yml) | Pushes to `main` that touch `website/**` or `compatibility/**`; manual | Ubuntu | Builds and deploys the site. See [The website](/docs/contribute/website/#deployment). |

The reports of the macOS, durable and custom agents runs are uploaded as workflow artifacts. Their matrices set `fail-fast: false`, so one failing OS or suite doesn't cancel the others.

:::note
No workflow runs the core suites or the companion suites, and only the durable and custom agents workflows run on Windows. If you change a library, run its core suite yourself.
:::

## Add a suite

A new suite follows the same shape:

- A console project under `tests/`, with its own committed `packages.lock.json`.
- Case ids that are unique and stable, a `--report <fresh path>` option, and an exit code of 1 when a case fails.
- An entry under `/tests/parity/` in `PiSharp.slnx`.
- To run it in CI, add it to the list in `release.yml` and to the matrix in `macos.yml`. The release runs only the suites its list names.
