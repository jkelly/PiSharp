# Final focused runtime coverage audit

Read-only audit of the 32-project `latest` cohort in `AI_DOCS/sync-1.5.2-validation.json` (generated `2026-11-11T04:59:05Z`), current source changes, test entrypoints and project links. No build/test was run and no product/test source was edited. Root's already-requested nine additions (AnthropicSimple, AnthropicProfile, AnthropicAuthSync, AnthropicSdkBeta, AzureResponses, AzureSimple, NativeModelCatalog, ClassifierConfig, Classifiers) are not repeated below.

## Highest-value missing standalones

| Priority | Project name under `tests/` | Why it adds coverage beyond the current cohort | Invocation / prerequisites |
| --- | --- | --- | --- |
| 1 | `PiSharp.Tui.Editor.Tests` | Direct regression for the changed paste-controller alias renumbering; Tui.Tests registers input/lease/renderer/text/Sync152Tui, not this editor suite. **Known stale assertion:** `Program.cs:537` still expects `[paste #98undefined][paste #1undefined]`, while production now deliberately removes `undefined`. | Required `--report <fresh absolute path>`. No live terminal/Node required for its authored cases; copied word-data assets are project content. Optional `--*-source-observations` arguments enable historical oracle comparisons, not necessary for the authored run. Do not silently treat old oracle disagreement as 1.5.2 failure. |
| 1 | `PiSharp.Rpc.Tests` | `RpcSessionDispatcher.ModelThinking.cs` changed cycle behavior when the current model is absent and when one eligible model remains. Its model/thinking durability, response-budget and idle-fence tests are not run by ModelRuntime/SessionParity/CliParity simply because they use the same assemblies. | No required args; optional `--report <path>` and `--filter <name substring>` can coexist. Narrow minimum: `--filter rpc.model-thinking.` executes both model controls and native-thinking integrations. Full suite is offline with fake HTTP/owned temp sessions. |
| 1 | `PiSharp.Agent.Tests` | `AgentOptions.NormalizeInput` and `AgentLoopCallbacks.NormalizeInput` changed input delivery before events/history/provider requests. Existing CodingAgent queued-image tests cover integration; this standalone additionally protects queue identity, continuation, barriers, cancellation and scheduler contracts. | No required args; optional `--report <path>`. It uses checked-in/copied frozen fixtures and fake transports; no live provider or external oracle invocation. No supported `--filter`. |
| 1 | `PiSharp.McpParity.Tests` | Changed MCP manager/session/auth composition affects background connects, startup waits, external sign-in/reconnect, provider-auth tokens, refresh locking, step-up, registration and close races. McpSync covers shared helpers/new split sign-in; Extensions.ContractTests covers wire transport. Neither includes this application integration runner. | No required args; `--report <fresh path>` OR `--only <case-prefix>`, not both. Fake MCP/auth/provider exchanges, no live credentials. |

The existing receipt-preserving wrapper can run each of these; for example from repository root:

```powershell
& ./artifacts/Invoke-Sync152Validation.ps1 -Project tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj -Label sync152-agent-audit
& ./artifacts/Invoke-Sync152Validation.ps1 -Project tests/PiSharp.Rpc.Tests/PiSharp.Rpc.Tests.csproj -Label sync152-rpc-audit
& ./artifacts/Invoke-Sync152Validation.ps1 -Project tests/PiSharp.McpParity.Tests/PiSharp.McpParity.Tests.csproj -Label sync152-mcpparity-audit
$editorReport = Join-Path $PWD ('artifacts/test-reports/sync152-editor-' + [Guid]::NewGuid().ToString('N') + '.json')
& ./artifacts/Invoke-Sync152Validation.ps1 -Project tests/PiSharp.Tui.Editor.Tests/PiSharp.Tui.Editor.Tests.csproj -Label sync152-editor-audit -TestArgs @('--report', $editorReport)
```

These are proposed commands, not execution receipts. The wrapper builds Release with the established SDK/cache and captures console evidence even where a suite's own report is optional. Ensure the editor report's parent directory exists; its entrypoint does not create that directory.

## Focused downstream integration additions

`PiSharp.McpTuiOriginalQualification.Tests` is another useful short addition: 21 original-ownership controls across dynamic roots, admitted HTTP authentication, OAuth discovery/refresh, component transport failures, and native component lifetime. Despite their source files living in CodingAgent.Tests, **CodingAgent Program does not register these five groups**. The only inspected entrypoint that does is `McpTuiQualificationEntry`. Required args are `--report <absolute fresh path>`; these use supplied in-memory exchanges/resources, not a live provider or a special Node checkout. Its assembly is intentionally named `PiSharp.CodingAgent.Tests.dll`. `Invoke-Sync152Validation.ps1` does not expose a TestAssembly option, so use the underlying receipt runner's explicit override:

```powershell
$mcpReport = Join-Path $PWD ('artifacts/test-reports/sync152-mcp-originals-' + [Guid]::NewGuid().ToString('N') + '.json')
& ./tools/GapValidation/Run-Tests.ps1 -Project tests/PiSharp.McpTuiOriginalQualification.Tests/PiSharp.McpTuiOriginalQualification.Tests.csproj -Configuration Release -Label sync152-mcp-originals-audit -TestAssembly PiSharp.CodingAgent.Tests.dll -TestArgs @('--report', $mcpReport) -BuildArguments @('-p:RestorePackagesPath=E:/AI/PiSharp/gap-implementation/artifacts/nuget-cache','-p:RestoreSources=https://api.nuget.org/v3/index.json')
```

`PiSharp.Durable.Hosting.Tests` is the next valuable addition: changed `Durable/Harness/Generation.cs` changes when finishing a generation reads the agent/compaction policy; changed built-in catalog and SQLite/JSONL code also feed hosting. Its own runner exercises actual device-host send/tool/recovery/automation/model settings paths using faux models and temporary stores. This is a real independent executable. `Durable.Tests` and `Chord.Durable.Integration.Tests` do not register its test groups. It has no required args; `--report <fresh path>` OR `--only <case-prefix>`. No SQL Server or live provider prerequisite. Recommended full run:

```powershell
& ./artifacts/Invoke-Sync152Validation.ps1 -Project tests/PiSharp.Durable.Hosting.Tests/PiSharp.Durable.Hosting.Tests.csproj -Label sync152-durablehosting-audit
```

`PiSharp.Durable.Server.Tests` adds actual loopback Kestrel plus client reduction/reconnect/stream/restart behavior over that same host, not another kernel-unit pass. Its csproj links only `FixtureHttp.cs` and `ProviderFixtures.cs` from Hosting.Tests: this does **not** execute Hosting.Tests cases. It requires local bind/child-process capability for its owned server fixtures, uses faux models and SQLite/temp roots, and needs no remote service. No required args; `--report <fresh path>` OR `--only <prefix>`. If limiting effort, `--only streams.` is the most relevant targeted check for generation completion and task output ordering; full runner also covers restart/metadata. Proposed wrapper:

```powershell
& ./artifacts/Invoke-Sync152Validation.ps1 -Project tests/PiSharp.Durable.Server.Tests/PiSharp.Durable.Server.Tests.csproj -Label sync152-durableserver-audit
```

## Low-cost supplementary / lower priority

- `PiSharp.DurableMessages.Tests` executes exactly eight linked cases: five `ContextDurableSessionMessageTests` plus three `NativeInitializerLoaderTests`. The current CodingAgent Program does **not** register those classes, despite their source location. This short standalone checks the changed extension context/message/loader composition. Optional `--report <fresh path>` only; no required args. Its assembly is intentionally named **PiSharp.CodingAgent.Tests.dll**, so invoke `tools/GapValidation/Run-Tests.ps1 -Project tests/PiSharp.DurableMessages.Tests/PiSharp.DurableMessages.Tests.csproj -Configuration Release -Label sync152-durablemessages-audit -TestAssembly PiSharp.CodingAgent.Tests.dll` with the same BuildArguments as above, or invoke that assembly in the DurableMessages output directory. The convenience Invoke-Sync152Validation wrapper does not forward a TestAssembly override.
- `PiSharp.DurableCli.Tests` covers the separate retained experimental durable CLI over the changed Durable library, including real child kill/resume and faux-provider flows. It is not included in CliParity. No required args; report/only are mutually exclusive. Native .NET child execution and temporary filesystem/loopback are required. One historical interop case intentionally skips unless `PISHARP_DURABLE_UPSTREAM_V110` names its **old v1.1.0** checkout with node_modules and Node is available; the v1.5.2 checkout is not that oracle. Lower priority than Hosting/Server because no `Cli/Durable` implementation changed in this update. A targeted `--only runtime.` run is adequate supplementary evidence before considering its whole retained-feature suite.
- `PiSharp.Sessions.Tests` is standalone, but `src/PiSharp.Sessions` has no runtime diff here and SessionParity/CodingAgent already exercise affected integration paths. Do not add it solely because the project exists. If later storage/session fixes touch that assembly, its runner supports `--report <path>` with `--filter <substring>` and uses pinned fixtures/temporary stores.

## Do not count these as missing executable suites

- `PiSharp.McpOAuthOrchestration.Tests`, `PiSharp.McpOAuthHost.Tests`, and `PiSharp.McpAuthorizationCode.Tests` are **libraries**, exposing `Cases()` controls with no Main. `dotnet run` cannot execute them. No project currently links/registers these controls in the inspected test entrypoints, so they are not implicitly covered either. The new OAuth runtime is actually exercised by `McpSync`'s registered `Sync152McpCases` and its existing OAuth controls; application regressions are the reason to add McpParity. Treat library compilation as compilation only, not test execution.
- `PiSharp.Tui.Focus.Tests` is a specialized projection/ownership oracle consumer, not a physical terminal test. Invocation is **two positional args**: `tests/PiSharp.Tui.Focus.Tests/fixtures/focus-source-observations.json <fresh-report>`. It reads the committed five-schedule older focus fixture; no external source path in its provenance metadata is opened by the runner. Focus-specific implementation did not change here, so this is lower-value than Editor and the already-run actual ConPTY checks in Tui/CodingAgent.
- `PiSharp.Tui.SelectList.Tests` is evidence-gated: six positional args (fresh report, admitted review root, source-manifest path/hash, build-receipt path/hash), with source-closure and SDK validation. There is no changed SelectList runtime path in this update; do not fabricate those admission artifacts or invoke it as an ordinary no-arg unit suite.

## Acceptance notes

The 32-project cohort is an inventory of latest attempts, not 32 accepted final passes: several entries were failed or source-changed at the audited timestamp. Rechecking those already-registered areas takes precedence over blanket expansion. Once their source fingerprints stabilize, the four priority standalones, the 21 MCP original-ownership controls, and Durable.Hosting are the highest-value additions; Durable.Server and the eight DurableMessages controls extend genuine independent integration coverage. Build/project references and copied helper files alone never imply a test entrypoint executed.
