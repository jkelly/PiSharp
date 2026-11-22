# ai-providers: Pi 1.6.0 provider wire and behavior changes

Branch `sync/1.6.0-ai-providers`, from `8352a672`. Upstream range `v1.5.2..v1.6.0` (`b4afdddf..faac5858`) of `packages/ai/src/api`, `packages/ai/src/utils`, `packages/ai/src/providers/compat-schema.ts`, `packages/ai/src/index.ts`, `packages/ai/package.json` and `packages/ai/test`. No live provider call was made and no credential was used. Version constants, the changelog, the catalog data and the integrity table are untouched.

Every change in the range is ported with a regression case that ran, or is accounted for below. Root's three later additions (the PiSharp-only max token ceiling, the developer role default, the catalog flags) have their own section. Two things are not done and need a decision from root: the native transports that replace a connection failure with a fixed text are still not retried (section "Not done"), and `models.schema.json` still carries two 1.5.2 descriptions.

## Commits

| Commit | Subject |
| --- | --- |
| `7fb45ce2` | Retry socket error codes, closed connections and the Codex connection limit |
| `caea0f71` | Send strict tool schemas only where they are safe |
| `f7f407ac` | Default Anthropic message threads by endpoint and free them with the session |
| `953b05e5` | Use the developer role only for OpenAI's own Completions API |
| `4d7c0611` | Record reasoning items the Responses API withheld from the model |
| `1bf40d98` | Bring the Codex request up to Pi 1.6.0 |
| `81987074` | End a Google CONTINUATION finish as length |
| `0b9aba2a` | Add the Pi 1.6.0 provider regression cases |
| `9d2556c3` | Admit every catalog output token limit in the request factories |
| `3b5bf820` | Cover large output limits and the developer role default on every path |

## Upstream changes

Cases named `sync160.*` are in `tests/PiSharp.ProviderSync.Tests/Sync160ProviderCases.cs` (16 cases) unless marked (Apis), which are in `tests/PiSharp.ProviderApis.Tests/Sync160ProviderCases.cs` (7 cases). Both files are registered in their suite's `Program.cs`.

| Upstream file or changelog bullet | Status | C# | Cases |
| --- | --- | --- | --- |
| `utils/retry.ts`: bare Node socket codes (#12128), `Connection reset by peer`, `connection.?closed` (#12064), `websocket.?connection.?limit.?reached` (#12124) | ported | `src/PiSharp.CodingAgent/Configuration/AgentRetryPolicy.cs`, `src/PiSharp.Durable/Internal/PiAi/Retry.cs` (the two ports of the classifier) | `sync160.retry-node-socket-codes-connection-closed-and-codex-connection-limit`; `PiSharp.OpenAICompatibilityGap.Tests` `Retry()` compares the two ports on 27 new inputs |
| The same failures as System.Net reports them (PiSharp-only, asked for in the brief) | ported | same two files, one list: the Winsock and Unix texts of ECONNRESET, ECONNABORTED, ECONNREFUSED, ETIMEDOUT and EPIPE, `transport connection`, `error occurred while sending the request`, `response ended prematurely`, `without completing the close handshake` | `sync160.retry-dotnet-socket-and-connection-failure-texts` (authored texts and this platform's `SocketException` messages); (Apis) `sync160.retry-live-loopback-connection-failures-are-retryable` (real resets and drops on the Responses and Codex transports) |
| `api/constrained-sampling.ts`: a node without a type is not strict; enum-only and anyOf nodes stay strict (#12049) | ported | `ResponsesToolDeclarationProjector.cs` (Responses, Completions, Azure Responses, Codex, Mistral), `AnthropicMessagesRequestProjector.cs`, `GoogleRequestProjector.cs`, `ProviderTranscript.cs` (Bedrock) | `sync160.strict-untyped-schema-nodes-fall-back-or-fail-on-every-api`; `PiSharp.GoogleGenerativeAI.Tests` `google.strict-union-nullability-nested-schema` (one vector overridden, five added) |
| `api/mistral-conversations.ts`: strict tools only with `compat.supportsStrictMode` (#12148) | ported | `MistralTextOptions.SupportsStrictMode`, `MistralSimpleHttpSseTransport.cs`, `MistralTools.cs`, `NativeProviderFactory.Catalog.cs` | `sync160.mistral-strict-tools-are-opt-in` |
| `api/anthropic-messages.ts`: non-strict schema keeps the root `additionalProperties` (#12067) | ported | `AnthropicMessagesRequestProjector.cs` | `sync160.anthropic-non-strict-schema-keeps-root-additional-properties`; `PiSharp.Compatibility.Tests` `CompleteStaticRequest` (expectation changed) |
| `api/anthropic-messages.ts`: message threads default by endpoint (#11987) | ported | `AnthropicMessageThreads.cs` (`AnthropicThreadContext.Eligible`) | `sync160.anthropic-message-threads-default-by-endpoint-not-provider` |
| `session-resources.ts` and the `./session-resources` package export (#11676) | already-ported, one gap closed | `src/PiSharp.AI/SessionResources.cs` (`RegisterCleanup`, `Cleanup`) has existed since 1.5.x and `PersistentAgentSession` calls it on dispose. A .NET assembly has no subpath exports. Gap: the shared Anthropic thread state was not registered, so `Cleanup(sessionId)` left it; it is now | `sync160.session-resources-release-anthropic-thread-state`; existing `codex-ws.session-dispose-closes-the-session-connections` and `strict-session.*` |
| `api/openai-completions.ts` `detectCompat`: developer role only for `api.openai.com` (#10741) | ported | `CompletionsKeyAuthRequestFactory.cs` | `sync160.completions-developer-role-only-for-openai`, `sync160.completions-developer-role-default-on-the-route-and-simple-paths` |
| `api/openai-completions.ts` `parseChunkUsage`: top-level `reasoning_tokens` (#12110) | ported | `OpenAICompletionsWireSource.cs` | `sync160.completions-top-level-reasoning-tokens-and-copilot-effort` |
| GitHub Copilot Gemini and Kimi receive the thinking level (#12110) | already-ported in code; catalog flag pending | the completions factory has always sent `reasoning_effort` when `compat.supportsReasoningEffort` allows it; Pi 1.6.0 sets the flag in the catalog | same case, with the compat field in an authored row |
| GitHub Copilot Responses tools say `strict` (#12286); Copilot Claude sends strict tools | already-ported in code; catalog flags pending | `ResponsesToolDeclarationProjector` writes `strict` whenever `compat.supportsStrictMode` is set, `AnthropicCatalogCompatibility` reads `compat.supportsStrictTools`. Upstream changed only `catalog/providers/github-copilot.ts` | `sync160.copilot-responses-explicit-strict-and-claude-strict-tools` |
| `api/openai-responses-shared.ts` `appendReasoningRemovals`: `openai_input_transformations` diagnostics | ported | `ResponsesTextToolTransport.cs` (shared by OpenAI Responses, Azure Responses and Codex), `ResponsesTextToolOptions.Clock` | `sync160.responses-openai-input-transformations-diagnostic`, `sync160.responses-input-transformations-over-openai-and-azure-http`, (Apis) `sync160.codex-response-done-records-input-transformations` |
| `api/openai-responses-shared.ts`: `includeSystemPrompt` option removed | no-op, see notes | `ResponsesTranscriptProjectionOptions.IncludeInitialSystemPrompt` stays as a PiSharp-only option (public record member, two existing cases use it); Codex no longer sets it | existing cases unchanged |
| `api/openai-codex-responses.ts`: system prompt as the leading developer item, no `instructions` (#12273) | ported | `OpenAICodexResponsesTransport.cs` | (Apis) `sync160.codex-system-prompt-is-the-leading-developer-item-without-instructions`; `codex.full-request-body-and-headers` (expectation changed) |
| `api/openai-codex-responses.ts`: thinking level as `configuration_update` items, fixed `reasoning.effort`, off as effort none (#12269) | ported | `OpenAICodexResponsesTransport.cs` (`ReasoningEffort`, `ResolveReasoningEffort`, `ConfigurationUpdateEffort`), `.WebSocket.cs` (`ResponseItems`) | (Apis) `sync160.codex-thinking-level-as-configuration-update-items-with-a-fixed-effort`, `sync160.codex-ws-thinking-level-change-keeps-input-deltas` |
| `api/openai-codex-responses.ts`: `maxTokens` as `max_output_tokens`, at least 16, unless `compat.supportsMaxOutputTokens: false` | ported | `OpenAICodexResponsesOptions.MaxTokens`, `OpenAICodexResponsesTransport.cs`; caller `src/PiSharp.ModelRuntime/Commands/LiveProviderRoutes.cs` passes its limit | (Apis) `sync160.codex-max-tokens-as-max-output-tokens` |
| `api/openai-codex-responses.ts`: cached WebSocket not reused after 45 minutes; the 60-minute limit mid-stream is retried (#12124) | ported | `OpenAICodexResponsesTransport.WebSocket.cs` (`SessionMaxAge`), the retry pattern above. The failed call already closed its connection and dropped the continuation | (Apis) `sync160.codex-ws-connection-limit-after-stream-start-is-retried-on-a-fresh-connection`; `codex-ws.connect-and-idle-timeouts-and-connection-age-limit` (expectation changed) |
| `api/google-shared.ts`: `CONTINUATION` maps to `length`; `api/google-generative-ai.ts`, `api/google-vertex.ts`: `automaticContinuation: false` | ported | `GoogleEventMapper.cs`, `GoogleRequestProjector.cs` (the SDK parameters carry the member; the REST body converter leaves it out) | `sync160.google-continuation-finish-ends-the-turn-as-length`, `sync160.google-injected-http-handler-and-continuation-switch` |
| Google adapters: custom `fetch` (#12103) | already-ported | `GoogleGenerativeAIHttpTransport` and `GoogleVertexHttpTransport` have always sent through the `HttpClient` they are given and never refused one; `GoogleApplicationDefaultCredentials` takes its own `HttpMessageInvoker` for ADC token requests, as upstream keeps `google-auth-library`'s client | `sync160.google-injected-http-handler-and-continuation-switch` |
| `api/google-shared.ts`: `mapStopReasonString` export removed | no-op | never ported: the mapper has one finish reason mapping | |
| `@google/genai` 2.32.0 per-request dispatcher that disables undici timeouts unless `fetch` is passed | no-op | Node and undici specific | |
| `openai` 7.59.0 | no-op beyond the above | the bump brings the `input_transformations` types; the behavior is the diagnostic row above | |
| `providers/compat-schema.ts`: Mistral `supportsStrictMode` | ported as behavior | the Mistral transports read the flag; the published models schema already accepts `supportsStrictMode` in its merged compat object | `sync160.mistral-strict-tools-are-opt-in` |
| `providers/compat-schema.ts`: three description changes | deferred to the schema owner | see "For root and other packages" | |
| `src/index.ts`, `package.json` | no-op | barrel, export map and dependency versions | |
| `utils/version.ts` | not mine | owned by `meta` | |

### Upstream tests

| Upstream test | Where it went |
| --- | --- |
| `retry.test.ts` | retry cases above |
| `constrained-sampling.test.ts` | strict untyped case and the Google vectors |
| `mistral-tool-schema.test.ts` | Mistral case |
| `anthropic-strict-tool-schema.test.ts` | Anthropic schema case and the changed `CompleteStaticRequest` expectation |
| `anthropic-message-threads.test.ts` | thread default case |
| `openai-completions-tool-choice.test.ts` | developer role and usage cases. Its catalog assertions (Kimi K2.6, Qwen 3.7 Max, Ant Ling, Fireworks, Together rows no longer carry `supportsDeveloperRole`) belong to `ai-catalog` |
| `openai-responses-compat.test.ts`, `providers.test.ts` | Copilot strict case, with authored rows |
| `openai-responses-input-transformations.test.ts` | the three diagnostic cases |
| `openai-codex-stream.test.ts` | the five Codex cases and the two changed Codex expectations |
| `max-thinking.test.ts` (Codex max level) | inside the Codex configuration update case, with authored rows; the catalog part belongs to `ai-catalog` |
| `google-stop-reason-continuation.test.ts`, `fetch-option.test.ts` | the two Google cases |
| `google-vertex-api-key-resolution.test.ts` | no-op: it asserts the SDK constructor's `httpOptions.fetch` |
| `openai-reasoning-replay-e2e.test.ts` | not ported: a live conformance test against OpenAI and Codex. Its offline content (configuration updates, the diagnostic's shape) is in the cases above |
| `llama-cpp-classify.test.ts` | no-op here: only fixture URLs changed; the default port change is in coding-agent's llama extension (`cli-session`) |
| `cache-retention.test.ts`, `supports-xhigh.test.ts`, `qwen-token-plan-models.test.ts`, `together-models.test.ts`, `fireworks-models.test.ts`, `generate-models-strict.test.ts`, `model-data-validation.test.ts` | catalog data and generator tooling: `ai-catalog` |

## Root's three additions

1. **The max token ceiling: lifted to the 32-bit range.** `MaximumTokenMagnitude = 1_000_000` (`CompletionsKeyAuthRequestOptions`, which the Simple factory checks through `DirectOptions`, and `AnthropicMessagesKeyAuthRequestOptions`, which the authenticated factory and the payload hook admission also read) and `ResponsesKeyAuthRequestOptions.MaximumOutputTokens = 1_000_000` came with the first import ("Add bounded native implementation source", `55c6a6c6`, 2026-10-04) as one of that design's explicit input bounds. Nothing upstream corresponds to it, and it guards no arithmetic of its own: the factories write the number as given. What is real is that the routes carry token counts as `int` (`LiveSessionSelection.MaximumOutputTokens`, `NativeThinkingProfile` reads `maxTokens` with `GetInt32`, the Anthropic thinking budgets). So the three defaults are now `PiRequestBudget.OutputTokens` (`int.MaxValue`), every catalog value passes and is sent as it is, and the options stay for a caller that wants a smaller bound. I did not remove the options: they are public record parameters and four existing cases configure or validate them. The Responses factory had the same ceiling and got the same change. Three places pinned the old default with a limit of 1,000,001: `PiSharp.Compatibility.Tests` `RejectedReplacementAndPriorAdmission` (now 2,147,483,648) and `PiSharp.ProviderOfflineDifferential.Tests` `responses-default-max-output-support` and the sampling case next to it (now with the 1,000,000 bound configured). Case: `sync160.max-tokens-above-one-million-builds-on-every-request-path` builds and sends a request with `maxTokens` 1,048,576 through the Completions key factory (with and without the model row), the catalog Completions route at two thinking levels, the Completions Simple factory (two context windows), the Anthropic key factory and catalog route and the Responses factory, for `fireworks`, `together` and `opencode-go` rows, and checks that a configured bound and a count above the 32-bit range are still refused. Two neighbours outside my paths still name the old number: `tests/PiSharp.CliParity.Tests/Sync150AttributionCases.cs` line 51 skips models above 1,000,000 with a comment that cites the ceiling (it can now include them), and `src/PiSharp.Cli/Commands/OfflineSessionProfile.cs` line 998 refuses a scripted summary budget above 1,000,000 (the offline profile, not the live route).
2. **The developer role default: one decision point, covered on every path.** The default is `Flag("supportsDeveloperRole", isOpenAI)` in `CompletionsKeyAuthRequestFactory.BindModelMetadata`. Every path that sends a request for a catalog or custom model binds the model row and therefore goes through it: `CreateCatalogCompletions`, `CreateAzureCompletions`, the provider routes, `LiveSessionSelection`'s fixed OpenAI/OpenRouter route (it passes `ModelMetadata`), the Durable route and `CompletionsSimpleRequestFactory` (it builds a key factory with the row). No second place decides the role; `CreateCompletions` without a row is only reachable for provider `openai` (developer) and `openrouter` (system). `sync160.completions-developer-role-only-for-openai` now lists rows without the flag for baseten, fireworks, github-copilot, huggingface, kimi-coding, the four qwen-token-plan providers and twelve more, and `sync160.completions-developer-role-default-on-the-route-and-simple-paths` sends through the catalog route (no level, off, high, and the summary binding) and the Simple factory for kimi-coding, fireworks, huggingface and baseten.
3. **Catalog flags.** `supportsMidConvoEffort` on the Codex rows is read by `OpenAICodexResponsesTransport`; the existing Codex cases use `gpt-5.6-sol` and `gpt-5.6-luna`, which stay unmanaged, so their expectations hold after the merge. `supportsMaxOutputTokens` defaults to true in the Codex transport. For Copilot Responses rows the code writes `strict: false` (or `true` for a constrained tool) as soon as the row says `supportsStrictMode`; `sync160.copilot-responses-explicit-strict-and-claude-strict-tools` asserts the two full tool objects.

## Existing expectations changed because upstream changed

1. `PiSharp.Compatibility.Tests` `CompleteStaticRequest`: asserted that a declared root `additionalProperties` was dropped from a non-strict Anthropic schema; it is kept now.
2. `PiSharp.ProviderApis.Tests` `codex.full-request-body-and-headers`: no `instructions`; the prompt is the developer item after the tool declarations; `max_output_tokens` is present; thinking off is `{"effort":"none","summary":"auto"}` (was `{"effort":"none"}`); a request without a system prompt has no default instructions.
3. `PiSharp.ProviderApis.Tests` `codex-ws.connect-and-idle-timeouts-and-connection-age-limit`: 44 and 45 minutes for 54 and 55.
4. `PiSharp.GoogleGenerativeAI.Tests` `google.strict-union-nullability-nested-schema`: the authored vector `supported-optional-null-const` (an optional bare `const: null`) counted as strict; a bare const has no type and is not strict in 1.6.0. `strict-schema-cases.json` is hash-pinned in `tools/native-companion-targets.json`, so it stays byte-identical and the case overrides that one vector in code.
5. The two ceiling cases named under "Root's three additions" changed because a PiSharp-only default changed, not upstream.

No case was deleted or weakened.

## Suites

All runs are on the final tree (`3b5bf820`), Release, with the pinned SDK commands of the plan. "Base" is the same suite on `8352a672` before any change; Agent, CliSync, Durable, CodingAgent, WebAgent and AnthropicProfile had no base run.

| Suite | Pass | Fail | Skip | Notes |
| --- | --- | --- | --- | --- |
| PiSharp.ProviderSync.Tests | 113 | 0 | 0 | base 97; 16 new cases |
| PiSharp.ProviderApis.Tests | 67 | 0 | 0 | base 60; 7 new cases |
| PiSharp.OpenAICompatibilityGap.Tests | 696 assertions | 0 | 0 | base 588 assertions |
| PiSharp.AnthropicThreads.Tests | 4 groups | 0 | 0 | |
| PiSharp.AnthropicResolved.Tests | 38 | 0 | 0 | |
| PiSharp.AnthropicSimple.Tests | 13 | 0 | 0 | |
| PiSharp.AnthropicProfile.Tests | 6 groups | 0 | 0 | |
| PiSharp.AnthropicAuthSync.Tests | 30 | 0 | 0 | |
| PiSharp.AzureResponses.Tests | 17 | 0 | 0 | |
| PiSharp.AzureSimple.Tests | 20 | 0 | 0 | |
| PiSharp.GoogleGenerativeAI.Tests | 36 | 1 | 0 | `google.native-diagnostics-preterminal-real-consumer-limit` ("The JSON value of length 536870889 is too large") fails on base too |
| PiSharp.VertexSimple.Tests | 17 | 0 | 0 | |
| PiSharp.MistralConversations.Tests | 66 | 0 | 0 | |
| PiSharp.MistralOriginalOptions.Tests | 8 | 0 | 0 | |
| PiSharp.ResponsesGrammarFallback.Tests | 6 groups | 0 | 0 | |
| PiSharp.ResponsesOrphan.Tests | 13 | 0 | 0 | |
| PiSharp.ResponsesPublicMetadata.Tests | 4 | 0 | 0 | |
| PiSharp.ResponsesTranscriptTools.Tests | 108 assertions | 0 | 0 | base 105 assertions (the Codex input now has the prompt item) |
| PiSharp.OpenAIResponsesHooks.Tests | 21 | 0 | 0 | |
| PiSharp.OpenAIResponsesOriginalParity.Tests | exit 1 | | | exits 1 on base too; its output is identical to base (70 records passed, 6 not) |
| PiSharp.Transport.Tests | 245 | 0 | 0 | |
| PiSharp.WireSync.Tests | 20 | 0 | 0 | |
| PiSharp.Compatibility.Tests | 235 | 1 | 0 | `artifact.catalog.released-npm-42-provider-shards` needs the ignored `artifacts/released-npm-ai/pi-ai-0.99.1.tgz`; fails on base too |
| PiSharp.ProviderOfflineDifferential.Tests | 66 | 0 | 0 | |
| PiSharp.SimpleOutputBudget.Tests | 131 cases | 0 | 0 | |
| PiSharp.ToolSearch.Tests | 25 | 0 | 0 | |
| PiSharp.Models.Tests | 124 | 0 | 0 | |
| PiSharp.NativeModelCatalog.Tests | 27 | 0 | 0 | |
| PiSharp.Classifiers.Tests | 63 | 0 | 0 | |
| PiSharp.Agent.Tests | 237 | 1 | 0 | `pi-messages-agent.released-radius-all-28-row-routing`, the known missing tarball case |
| PiSharp.CliSync.Tests | 68 | 0 | 0 | |
| PiSharp.Durable.Tests | 4695 | 0 | 4 | |
| PiSharp.CodingAgent.Tests | 865 | 0 | 50 | with `--dotnet-host` and `--cli`; the 50 skips are the published-fixture cases |
| PiSharp.WebAgent.Tests | 543 | 0 | 0 | |

Did not run: the fixture suites listed under "Not done", the live suites and `PiSharp.CliParity.Tests`.

## Not done

1. **Connection failures on four native transports are still not retried.** A probe with a loopback listener that resets or drops the connection (before the response and mid-stream) gave these error texts:

   | Transport | Error text | Retried |
   | --- | --- | --- |
   | OpenAI Responses, Codex (SSE) | `An error occurred while sending the request.` / `The response ended prematurely. (ResponseEnded)` | yes, since this branch |
   | OpenAI Completions | `Completions stream did not complete.` | no |
   | Anthropic Messages | `Anthropic stream did not complete.` | no |
   | Google Generative AI | `Google provider operation failed.` | no |
   | Mistral | `Mistral source operation failed.` | no |

   Upstream shows `Connection error.` or `fetch failed` on those providers and retries them. The native transports replace the exception text with a fixed text on purpose (the 1.5.2 discovery report records it as an approved sanitization policy), and the same fixed texts also cover hook failures and resource limits, so I did not add them to the classifier. This gap predates 1.6.0. The clean fix is to have those transports report a fixed `Connection error.` when the failure is an `HttpRequestException` without a status, an `IOException` or a `SocketException`; it touches four transports, their sanitization cases and `docs/compatibility/completions-lifecycle-next-design.md`, and needs the owner's decision. Azure Responses (`Azure Responses stream did not complete.`) was not probed and likely behaves the same.
2. **Suites that replay captured Source fixtures did not run**: `PiSharp.CompletionsSimple.Tests`, `CompletionsAnchored`, `CompletionsGrammar`, `CompletionsThinking`, `OpenRouterBinding`, `OpenRouterCacheProfile`, `OpenRouterReasoning`, `PiMessages`, `AnthropicSdkBeta`. Each needs an immutable fixture file that is not in the worktree. Their fixtures were captured from older Source versions, so rows that send a reasoning model's system prompt to a host other than OpenAI, or a strict tool with an untyped node, may now differ from the fixture.
3. `openai-reasoning-replay-e2e.test.ts` is a live test and was not run.

## For root and other packages

- **ai-catalog** (catalog flags my code reads; this branch still carries the 1.5.2 data, and root reports that the merged 1.6.0 catalog sets the Codex and Copilot ones):
  - `openai-codex`: `compat.supportsMidConvoEffort: true` on `gpt-6-astra`, `gpt-6-sol`, `gpt-6-luna`, `gpt-6.1-sol`, `gpt-6.1-terra`, `gpt-6.2-astra`, `gpt-6.2-sol`. Without it those models keep sending the level as `reasoning.effort`.
  - `github-copilot`: `compat.supportsStrictMode: true` on its `openai-responses` models, `compat.supportsStrictTools: true` on its `anthropic-messages` models, and `compat.supportsReasoningEffort` from the source's effort options on its completions models (true for `gemini-4-argon`, `gemini-4-carbon`, `gemini-3.8-flash`, `kimi-k3`).
  - `supportsDeveloperRole: false` is removed from every provider's rows in 1.6.0. Either state gives `system` with the new default.
  - Mistral rows carry no `supportsStrictMode`, so every Mistral model is non-strict.
  - After the catalog lands, `codex.full-request-body-and-headers` and the Codex WebSocket cases keep using `gpt-5.6-sol`, which stays unmanaged.
- **durable-core**: upstream `packages/durable/src/harness/harness.ts` `beforeClose` now calls `cleanupSessionResources(sessionId)` for every provider session id its tasks used, after the task invocations join, and reports a failure as a `host_error` (`callback: "models"`) without preventing storage from closing; `provider.ts` and `scheduler.ts` record the ids. The entry point is `PiSharp.AI.SessionResources.Cleanup(sessionId)`. It throws an `AggregateException` ("Failed to cleanup session resources") after running every cleanup. It closes the session's Codex WebSockets, forgets its strict tool limit state and now its Anthropic thread state. I changed `src/PiSharp.Durable/Internal/PiAi/Retry.cs` (the retry classifier is a port of `packages/ai/src/utils/retry.ts`); nothing else in Durable.
- **cli-session** (coding-agent):
  - `agent-session.ts` already called `cleanupSessionResources(this.sessionId)` at 1.5.2 and `PersistentAgentSession` already does. Nothing new is needed; the cleanup now also frees Anthropic thread state.
  - I changed `src/PiSharp.CodingAgent/Configuration/AgentRetryPolicy.cs` (same reason as the Durable file) and one line of `src/PiSharp.ModelRuntime/Commands/LiveProviderRoutes.cs` (`MaxTokens = maximum` for the Codex route).
  - Upstream's coding-agent regression `12124-websocket-connection-limit-retry.test.ts` (a tool call, then partial thinking with the limit error, then recovery; the tool runs once and the failed message is not sent on retry) is yours; the classifier it relies on is in place.
- **cli-ui / cli-session, the transcript notice**: upstream `interactive-mode.ts` now reads two diagnostics through one table: `anthropic_input_transformations` (dropped type `thinking_dropped`, "thinking block(s)") and `openai_input_transformations` (dropped type `reasoning`, "reasoning item(s)"). The message carries `diagnostics: [{ "type": "openai_input_transformations", "timestamp": <ms>, "details": { "transformations": [{ "type": "reasoning", "id": "rs_...", "action": "removed", "reason": "prefix_mismatch" }] } }]` in its extra properties, written to the session with the message. The notice is `"<Provider> dropped <n> <noun>: <reasons joined by ", "> (details in session)"`, shown only when `showCacheMissNotices` is on and the count is above the previous assistant message's count for the same diagnostic type (a removal is reported again on every later request that replays the item).
- **Schema owner** (`src/PiSharp.CodingAgent/Schemas/models.schema.json`, pinned by SHA in `tests/PiSharp.Models.Tests/ConfigSchemaCases.cs`): upstream's published schema changed two descriptions. `supportsMidConvoEffort`: "... Used by the anthropic-messages, openai-responses, and openai-codex-responses APIs." `supportsMessageThreads`: "... Default: true for a baseUrl on api.anthropic.com, false otherwise. ..." I did not edit the file because of the pin.
- **WebAgent owner**: `src/PiSharp.WebAgent/Runs/RunModelOptions.cs` refuses max tokens on Codex ("the Codex backend takes no output cap") and `RunEngineCases` asserts it. Since 1.6.0 the Codex transport sends `max_output_tokens`, so the refusal can be lifted. `src/PiSharp.WebAgent/Bench/BenchStrict.cs` has its own strict schema walk, which I did not check against the untyped node rule.
- **Root**: `tests/PiSharp.GoogleGenerativeAI.Tests/GoogleStrictSchemaCases.cs` and the other changed test files are in the integrity table and need the re-seal. `google.native-diagnostics-preterminal-real-consumer-limit` and the missing `pi-ai-0.99.1.tgz` case fail on the base commit too.

## Notes on choices

- **Thread default by host, not by substring.** Upstream tests `model.baseUrl.includes("api.anthropic.com")`. The native check stays an exact host comparison, as it was at 1.5.2, so `https://api.anthropic.com.gateway.example` does not get threads.
- **`IncludeInitialSystemPrompt` kept.** Upstream removed `includeSystemPrompt` because Codex was its only user. Removing a public record parameter would break the two cases that exercise it and any SDK user; nothing sets it to false any more.
- **Codex follows `streamSimple`.** The native transport has always been the `streamSimple` shape, so it takes over its two 1.6.0 consequences: `max_output_tokens` is sent on every request (the model's `maxTokens` fitted into the context window when no limit is given), and a request without a thinking level sends effort none with the summary setting.
- **`automaticContinuation` is not a feature here.** A payload hook may hand the member back with any value; no continuation request is ever sent. Upstream has the SDK feature turned off.
- **Explicit Completions profiles.** The new developer role default applies where a model row is bound, which is every catalog and custom provider route. `CompletionsTranscriptProjectionOptions.SupportsDeveloperRole` still defaults to true for a caller that builds a projection by hand without a row.
- **Headers.** The ported files I changed carry the v1.6.0 commit in their header; five that had none got one (`AnthropicMessageThreads.cs`, `MistralTools.cs`, `ResponsesToolDeclarationProjector.cs`, `ResponsesTextToolTransport.cs`, `OpenAICompletionsWireSource.cs`). `MistralTextOptions.cs` and `NativeProviderFactory.Catalog.cs` keep theirs: I added one option and one compat read.
