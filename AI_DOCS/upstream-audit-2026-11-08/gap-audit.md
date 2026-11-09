# PiSharp code audit and gap register — 2026-11-08

## Outcome

The released 1.5.0 baseline is substantially implemented, but it has concrete correctness gaps, incomplete integrations, and verification gaps. Newer upstream work adds further useful contracts. This is an audit and implementation plan; no product source was changed.

Reviewed PiSharp main `14dd34b369fba6d236684da6dfc8a39a03263321`, not the active, older `fix/codemode-worker-hang` checkout. The read-only audit snapshot is `E:/AI/PiSharp/audit-2026-11-08/source`. All C# paths/line numbers below refer to that snapshot. Do not apply findings to old-branch line numbers.

Upstream release baseline: `f9a26365568127461c47a07a37dcb42c140a3ac5` (Pi 1.5.0). Candidate: `7d073328e44337d3292a7db9b0f0308f679c5e97` (Nov 8). The candidate is **unreleased**. PiSharp 1.5.0 was published Nov 5; no newer Pi stable release was found.

## Evidence and limits

- Four independent reviews covered providers/schema, Durable/storage, CLI/tools/MCP/session lifecycle, and baseline/CI evidence. Main reviewed findings and ran focused offline probes.
- Full paginated comparison: **326 unique commits**. Comparing both complete, untruncated Git trees found **2,935 changed blob paths**. Much of this is Chord restructuring and apps; it is not 2,935 missing features. See [machine-readable inventory](evidence/upstream-inventory.json).
- The GitHub compare API's changed-file list stops at 300. This audit used full trees instead of treating that list as complete.
- Focused source review is not an exhaustive proof of all runtime behavior in every changed file. Unreviewed candidates remain explicit below.
- No live model calls, credentials, production databases, deployment, package upgrade, or full cross-platform test suite was used.
- Existing schema suite: 23 grouped cases passed. These are authored native expectations, not 23 captured upstream equivalence tests. Focused probes independently reproduce gaps despite that pass; see [verification](verification.md).
- Prior historical captures and source pins were not rewritten.

Priority: **P1** = address first (data integrity, valid tool rejection, unbounded input allocation, credential lifecycle); **P2** = functional correctness or important compatibility; **P3** = smaller usability/performance gaps. Priority is engineering order, not a security severity rating. “Reproduced” means an isolated offline probe ran; “source” means traced code and callers, without the affected live provider/platform.

## Correctness and integration gaps

| ID | Priority / evidence | Finding, trigger and impact | Exact C# evidence | Upstream / baseline |
|---|---|---|---|---|
| C01 | P1, reproduced | Relative root schema `$id: schemas/tool.json` is applied twice. Valid local references fail, including tool argument validation. | `Contracts/Schema/SchemaResolver.cs:46,64`; `SchemaCompiler.cs:97,110`; `Agent/ToolArgumentValidation.cs:95` (all under src/PiSharp.) | [2bd2c2d0](https://github.com/earendil-works/pi/commit/2bd2c2d0a6a13e9a2d760073bd6e67b662964a60) |
| C02 | P1, reproduced | Non-strict Anthropic tool projection drops root `$defs`/`definitions`, leaving dangling references. Strict-prefer fallback and inline tool additions share the path. | `src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesRequestProjector.cs:562-564` | [4a6a58eb](https://github.com/earendil-works/pi/commit/4a6a58eb5978ebc46df5a2590e65959d7fe1cbf5) |
| C03 | P2, reproduced; corrupt-data trigger | A corrupted/incompatible stored document shape (valid JSON `{}`) is caught as semantic batch refusal. Earlier batches commit and receive receipts instead of the storage call rejecting/rolling back. Ordinary writes do not create this invalid shape; SQLite rejects invalid JSON syntax. | `src/PiSharp.Durable.Sqlite/Storage.cs:99-126,642`; `src/PiSharp.Durable/TypesStorage.cs:299-305` | C# storage-contract defect. [fabf916f](https://github.com/earendil-works/pi/commit/fabf916fcf63a886c0974370d6b4dc34e4167e5b) separates upstream decoding/refusal, but its `{}` behavior is not claimed equivalent. |
| C04 | P2, source | Hosted-session and agent-team settings wrappers drop `AfterFailedRun` and `RestartLimit`. Explicit Withdraw/Continue or limits 1/infinity become Keep/5. | `src/PiSharp.Durable.Hosting/SessionCompaction.cs:100-114`; `src/PiSharp.CustomAgents/Teams/AgentTeam.cs:125-137`; `src/PiSharp.Durable/Harness/Agent.cs:68-69` | PiSharp-specific 1.5.0 integration bug; violates `DurableDeviceOptions.cs:37-39` |
| C05 | P1, reproduced | Editing one word normalizes untouched mixed CRLF/LF/lone-CR endings across the file. Existing tests explicitly expect the old behavior. | `src/PiSharp.Tools/Files/EditPlan.cs:47,72`; `tests/PiSharp.Tools.Tests/EditToolTests.cs:131` | [d448c51e](https://github.com/earendil-works/pi/commit/d448c51e2b895bf5dee92b69e33a0554648ee5f5) |
| C06 | P1, source | Grep context reads each entire matched file without an input cap, retaining byte, decoded and line copies before output truncation. Small requested output can exhaust memory. UTF-16 context is decoded as UTF-8. | `src/PiSharp.Tools/Files/PiGrepTool.cs:114-129,195,199` | [8da141c2](https://github.com/earendil-works/pi/commit/8da141c2ad8f9e9b5e9d3e42d78c4ed90c4fe125) |
| C07 | P1 validation/fix lane, source | RPC discards the background catalogue-refresh task. It can rotate OAuth credentials, but process shutdown has no identified ownership/join for that task. Token-loss consequence requires the fake-refresh lifecycle test before a fix is accepted. | `src/PiSharp.Cli/Pi/PiCommand.cs:578,586-588,604-614`; `src/PiSharp.Cli/Models/ModelRegistry.cs:199,250-258` | [b2cb6292](https://github.com/earendil-works/pi/commit/b2cb629237496524e745ff668ec5bd5d47130164) |
| C08 | P2, reproduced | Prompt arguments lose apostrophes, combine words, discard unmatched quotes and lack escaped-quote handling. `doesn't work` becomes one argument `doesnt work`. | `src/PiSharp.CodingAgent/Resources/PromptTemplateExpander.cs:19-38` | [96667f34](https://github.com/earendil-works/pi/commit/96667f342a8da98ec7969caee7f920ace104436f) |
| C09 | P2, reproduced search / source summaries | Tool search omits descriptions/property names behind local references; deferred tools are not discovered. Codemode output summaries also omit referenced fields. | `src/PiSharp.Extensions.Abstractions/Mcp/Discovery/ToolSearchRanker.cs:125-144`; `src/PiSharp.Durable.Tools/ToolSearch/ToolSearch.cs:86-109`; `CodemodeToolDefinition.cs:160-195` | [6d3e342b](https://github.com/earendil-works/pi/commit/6d3e342b25db295bb266e28bdb1ebf09bdbae58d) |
| C10 | P2, source | MCP SSE parser recognizes LF and CRLF, not CR-only framing; a valid open response can wait without dispatching. | `src/PiSharp.Extensions.Runtime/Mcp/Transport/McpStreamableHttpTransport.cs:199-237` | [b64e6bad](https://github.com/earendil-works/pi/commit/b64e6badd6ddefda17fc7f6286c013467186d8a1) |
| C11 | P2, source | If the old MCP callback port is occupied, sign-in clears registered client/tokens before attempting refresh with the original redirect URI. This can force needless re-registration/browser sign-in. | `src/PiSharp.Cli/Mcp/Authentication/McpSignIn.cs:130-148` | [797e122c](https://github.com/earendil-works/pi/commit/797e122cb1181931e6ec1fe2369c7c17dbf1fc9e) |
| C12 | P2, source | Extension `ctx.shutdown()` while RPC is idle only sets a flag; no input/run settlement occurs to consume it, so another command is needed. | `PiExtensionHost.Requests.cs:289`; `OfflineSessionProfile.LifecycleHandoffs.cs:58-68`; `RpcSessionCommand.cs:405-406` | [1708b478](https://github.com/earendil-works/pi/commit/1708b47889664ec9838f6734ff57df58b079a50a) |
| C13 | P2, source | Tree navigation keeps live thinking level but the next prompt does not record it on the new branch; reopening can restore an old level. | `PersistentAgentSession.Navigation.cs:117-121,210`; `PersistentAgentSession.cs:1000-1002`; `PersistentAgentSession.Activation.cs:99-136` | [7860ff52](https://github.com/earendil-works/pi/commit/7860ff52892c1a9f0b017be78fc75dd30b5d5c98) |
| C14 | P2, source | UTF-16 slicing can split non-BMP characters at grep, summary or codemode truncation boundaries. | `PiGrepTool.cs:174`; `SessionSummaryRequestBuilder.cs:71`; `CodemodeExecutor.cs:61,394` | [9e870f6f](https://github.com/earendil-works/pi/commit/9e870f6f002ca6c21ce010fdb99122afe3738b28) |
| C15 | P2, source | Grep globs are evaluated from startup cwd instead of the supplied search directory; relative and leading-`./` patterns can miss files. | `src/PiSharp.Tools/Files/PiGrepTool.cs:134-163` | [6bf2ab9b](https://github.com/earendil-works/pi/commit/6bf2ab9bbb64fd14793e506a5404078441219b95) |
| C16 | P2, source | Zero find limit means unlimited to fd; zero/negative ls can report empty; exact result counts incorrectly report truncation. | `PiFindTool.cs:80,101,113,126`; `LsTool.cs:128`; `PiGrepTool.cs:162-163` | [676af1d7](https://github.com/earendil-works/pi/commit/676af1d777ef6f04b6bc8daf4cf3b9b1799cff4d) |
| C17 | P2, source | Removing all tools after prior tool use still sends `tools: []`; newer vLLM rejects it. No compatibility override exists for old gateways needing the field. | `src/PiSharp.AI/Protocols/OpenAICompletions/CompletionsKeyAuthRequestFactory.cs:251,749` | [7a6bd5a9](https://github.com/earendil-works/pi/commit/7a6bd5a97cb64766c9ebbf30e9c0ad7c88a00c47) |
| C18 | P2, source | Fixed 4,096-token output margin fails to accommodate input token-count differences, especially OpenRouter, causing premature context errors/compaction. Formula is duplicated across simple providers. | `CompletionsSimpleRequestFactory.cs:103`, Anthropic `:75`, Azure `:82`, Google `:83`, Vertex `:87`, Mistral `:72`, Bedrock `:120` | [0a4fad54](https://github.com/earendil-works/pi/commit/0a4fad5489f303fe023e3948d473731d7b2f8acb) |
| C19 | P2, source metadata | Azure's five GPT-6 variants retain 272K rather than upstream-corrected 1.05M context; OpenCode Claude rows lack per-message effort; Sonnet 5 rows lack temperature exclusion. Runtime capabilities largely exist but catalogue does not select them. | `src/PiSharp.Cli/Models/{azure,opencode,opencode-go,anthropic}.json:1`; `catalog-maintained/models.overlay.json:3` | [Azure](https://github.com/earendil-works/pi/commit/b317575fd109a39b16f35161c4090ac2ecaa83f7), [Claude binding](https://github.com/earendil-works/pi/commit/9ee7586a95f5e653ae279c3b8f20938faf3639a1), [temperature](https://github.com/earendil-works/pi/commit/4b97777378a7c2190bfecea0581fb525dfdb5427) |
| C20 | P2, source | Responses session-affinity header is `session_id`; reverse proxies may drop underscore headers. | `src/PiSharp.AI/Protocols/OpenAIResponses/ResponsesKeyAuthRequestFactory.cs:314` | [c5d39783](https://github.com/earendil-works/pi/commit/c5d3978350c7d32db5d8eb28400d4fa851624703). Keep Completions spelling unchanged. |
| C21 | P2, source | The specific new “stream ... was cut short after the reply had begun” failure is absent from both retry classifiers. | `src/PiSharp.CodingAgent/Configuration/AgentRetryPolicy.cs:45`; `src/PiSharp.Durable/Internal/PiAi/Retry.cs:53` | [56634404](https://github.com/earendil-works/pi/commit/5663440468b1a465a445756db69a863c0d8e8372) |
| C22 | P3, source | Package update prints success even when offline/nothing changed; no version/commit result summary. | `PiPackageManager.cs:281`; `PiPackageCommands.cs:410-411` | [342514bd](https://github.com/earendil-works/pi/commit/342514bdf87b9fcc094b49820e2d636ec95b0a8f), [806cdd68](https://github.com/earendil-works/pi/commit/806cdd68e312f8cb80efe367c661bea0bd3ee537) |
| C23 | P2, source, disclosed baseline gap | SQLite prepared-command cache retains every distinct SQL text until close; varying SQL on long-lived connections can grow without a bound. | `src/PiSharp.Durable.Sqlite/ConnectionDatabase.cs:162-187` | Existing `chord.sqlite-statement-cache` deferral; can ship independently of full Chord host. |

Some table cells use unique filenames for compactness. The machine-readable register and source snapshot are the authoritative lookup; all links in the implementation plan use the same frozen revisions.

## New capabilities to implement separately

These are confirmed additions after the baseline, not proof that existing supported behavior is broken.

| ID | Gap | Contract and acceptance focus |
|---|---|---|
| F01 | Durable per-conversation compaction | Add stored agent policy, clear/replace semantics and inheritance. Conversation model override → harness model override → conversation fields → harness defaults; explicit conversation disabled defeats harness enabled. Threshold/background/overflow and reopen must agree. [922c3ff2](https://github.com/earendil-works/pi/commit/922c3ff2bdea786b16c9b26680f22c2272251ffc) |
| F02 | Public transaction submission lookup | Concrete transaction already has `GetSubmissionAsync`, public `ITx` lacks it. Expose committed reads with read-after-write rules; do not call queued harness reads inside a held transaction. [9ca15920](https://github.com/earendil-works/pi/commit/9ca159208a329b10271fc1c392189b1005e09c83) |
| F03 | Durable thinking budgets and WebSocket handshake timeout | Thread options through public/internal types, provider mapping and persisted prepared requests. Adding properties alone is insufficient. [dbc61d64](https://github.com/earendil-works/pi/commit/dbc61d647b118631b3bbfa3532ba8e9387329ea3) |
| F04 | Bulk SQLite group-commit optimization | Current code already has transactions and semantic-prefix receipts. Reduce repeated checks/statements without changing those semantics. No C# speedup has been measured. Fix C03 first. [fabf916f](https://github.com/earendil-works/pi/commit/fabf916fcf63a886c0974370d6b4dc34e4167e5b) |
| F05 | Independent proxy compatibility controls | Add `requiresNonNullAssistantContent`, `promptCacheKeyMode=auto/enabled/disabled`; preserve caller override precedence and distinguish from synthetic assistant messages. [14b804d8](https://github.com/earendil-works/pi/commit/14b804d8096255aa700f7468d08b8a0ef332ec47), [0f4d5e96](https://github.com/earendil-works/pi/commit/0f4d5e960a03b43708129ba89c182dfd0b51110b) |
| F06 | Streaming large-file read | Current native 64 MiB bound is intentional, but it prevents a small slice of a larger file. Add streaming selected-line reads with a bounded fallback for existing adapters. Do not remove resource limits indiscriminately. [3a7bb6f4](https://github.com/earendil-works/pi/commit/3a7bb6f48cc64986608ffa1b0cb52c3ff938ef2a) |
| F07 | Shell auth and accurate MCP guidance | Add `auth login/logout` by composing existing flows. Provider-auth servers already send tokens; only guidance/login routing needs correction there. [9b9cb30a](https://github.com/earendil-works/pi/commit/9b9cb30ae16e5802ba556b282b38fc41117ee5e1) |
| F08 | Anthropic message threads and shared cache-warm policy | Optional larger feature. Full-history requests work today. Require bounded per-session state, correct change detection/full fallback, `disableContinuation`, cache-warm isolation and deterministic cleanup. [a758f3ae](https://github.com/earendil-works/pi/commit/a758f3ae4cdb193a6949c81318fe1b3caa4814a1) |
| F09 | Schema API and model/provider expansion | New validator/builder exports, TypeBox-specific values, Qwen/Copilot additions and new defaults need explicit C# applicability decisions. Do not translate JS BigInt/undefined or refresh pinned catalogues without provenance review. |

## Existing 1.5.0 completion debt

The baseline inventory is internally consistent: **326 rows / 477 distinct changelog bullets**, of which 283 rows belong to the released scope: **232 ported, 19 ported-with-deferral, 9 already-ported, 23 no-op**. Remaining 43 rows are assigned to later 1.5.0.x work; 37 still require a port (**10 env, 26 Chord, 1 eval**). These counts are unrelated to the coincidentally 326 new upstream commits.

Durable has 289 port-map records: 252 ported, 9 PiSharp-only, 8 excluded, 6 removed, 5 skipped, 3 in-progress, 3 needs-sync, 3 planned. The nine unfinished records align with disclosed deferrals. Old source citations (338 still naming 1.2.0) are not independently missing-port findings.

| Group | Remaining scope and decision |
|---|---|
| B01 Provider/core behavior | Classifier provider overrides; `additional_tools`/search for Responses/Codex/Azure; incremental tool argument work; native exec maxBuffer beyond the current 1 MiB/pipe bound. Work from exact inventory row contracts. |
| B02 Identity and hooks | Models-level identity default; non-chat Durable classify/image/deferred fetch/cancel identity; scoped account-route `BeforeProviderRequest` hook. Preserve intentional no-attribution default. |
| B03 Bridge and platform | Node overlay handle/function visibility; Unicode segmentation; macOS physical Shift+Enter; installed extension runtime intentionally stays on Pi 1.2.0. A bridge upgrade is a separate compatibility project, not a package version edit. |
| B04 Chord host/store/registries | Implement actual host/facet lifetime and staging, replace hand-built host fixtures, finish real-host registry/store tests, examples 35–37, schema fingerprint gates, then consider package admission. Chord and Durable.Store are intentionally unpacked. |
| B05 Chord jobs/links + pi-env | Job/host/remote protocol dependencies precede remaining worker/reload integrations. Decide native daemon versus verified upstream binary for env; test cancellation/startup/watch/Windows contracts. |
| B06 pi-evals | Repository-only tool after APIs stabilize, synthetic/offline checks by default; live evals only explicitly invoked. |

Native session-write failure currently stops later writes rather than attempting rollback. This is an intentional safer behavior pending a separate owner decision; preserve it by default. JS revoked-proxy access, microtask counts, Cloudflare and browser backends, packaging for npm/Nix, and removed upstream surfaces are not automatically missing C# features.

## Verification gaps

| ID | Finding | Required correction |
|---|---|---|
| V01 | Ten core console suites are not invoked by any hosted workflow, despite existing local native runner. | Add real prerequisite-aware CI lanes. A solution build or `dotnet test` does not execute these console suites. |
| V02 | Release publish depends only on its build job and 22 selected suites; broader Durable OS matrix, zero-retention, SQL Server and hosting checks run elsewhere but are not coupled to the exact release SHA. | Reusable required jobs or exact-SHA status validation before publication. Do not indiscriminately gate narrow core work on unrelated web UI suites. |
| V03 | Applicability/worklist structural checks are not all in CI; upstream-aware check skips without its checkout. | Always run offline check, then exact-object upstream-aware check with skip treated explicitly. |
| V04 | No v1.5.0-written session has a new byte-for-byte upstream differential capture. Historical fixtures and authored projections are useful but different evidence. | Add a separate approved corpus for critical storage/lifecycle/provider contracts; never silently recapture historical fixtures. |
| V05 | Historical IMPLEMENTATION_STATUS entries and preparation receipts are not a current-head validation ledger. | Publish exact SHA/platform/suite/case/prerequisite/pass/failure/skip receipts. Fix minor “20” versus “22” release-suite documentation. |

Evidence: `.github/workflows/release.yml:61,106`, `durable.yml:34,51,62,79-118,183-214,349-362`, `tools/test-native.ps1:49-77`, `tools/CompatibilityReport/sync-applicability.test.mjs:59-98`, `website/src/content/docs/docs/contribute/testing.md:108,204-205,221,232,238`, release notes `:427,480-532`.

## Excluded or unconfirmed candidates

Already implemented / not mechanically applicable: MCP JSON/event size caps; malformed-percent reference fallback in codemode declarations; byte-safe MCP output truncation; provider-token transmission; Node image-worker shutdown workaround; diff-before-write safety; C# cancellation-aware Codex delay. Existing Durable read caches and retained event projection are intentional, not newly missing functionality. Reverted OpenRouter tool-ID shortening must not be ported.

Still needs a focused applicability review: queued images; multiple overlay lifecycle/blocked status; model cycling; prerelease changelog; tool-result disposal; reload/navigation concurrency; exact background-process bash timeout diagnostics. They are candidates, not established defects. F09 also requires API/product design. The plan includes a bounded disposition pass so these do not disappear from tracking.

See [implementation plan](implementation-plan.md) and [contract notes](contracts.md) for the actual sequence.
