# Explicit-key Azure Responses adapter

This isolated source slice is based on verified immutable main `4b63271a13e10505ab0e648592021050155454d1` (tree `993e39dbc5a502d670846edee0f9e55a849cfe63`). It adds an original P2-08 Azure Responses request/stream adapter, with an explicit API key and borrowed HTTP client. The adapter does not acquire identity. Authored expectations, native execution, genuine upstream captures and independent acceptance are separate evidence kinds. All native checks and source qualification for this new slice remain UNEXECUTED/OPEN.

## Source contract and implementation

The immutable baseline is [Pi v0.99.1 Azure API](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/api/azure-openai-responses.ts), with the Azure provider, shared Responses mapper, prompt-cache helper and provider error normalizer at that same commit. Local byte hashes and the already-restored OpenAI SDK 7.19.0 source are pinned in `tests/PiSharp.AzureResponses.Tests/source-inventory.json`. Neither a source oracle nor SDK code was executed for this slice.

| Pinned behavior | Native owner |
| --- | --- |
| Explicit direct-stream key; Azure `api-key` header; model headers then request headers | `AzureResponsesRequestFactory.CreateAsync` and header admission |
| Version option, injected configuration value, then `v1`; trimmed base option, injected base, resource-derived base, then model base | Factory constructor and `NormalizeBaseUrl` |
| Azure host suffixes `.openai.azure.com`, `.cognitiveservices.azure.com`, `.ai.azure.com`; root, `/openai` and `/openai/v1/responses` normalize to `/openai/v1` and clear query | `NormalizeBaseUrl` |
| Deployment option, deployment map, then canonical model ID; last admitted map entry wins | `DeploymentMap` and factory constructor |
| SDK 7.19.0 excludes `/responses` from its deployment-scoped endpoint set | Resolved base plus `/responses?api-version=...`; deployment is body `model` only |
| Canonical request model controls transcript replay and assistant identity | Existing `ResponsesTranscriptProjector` and `ResponsesTextToolTransport`, receiving the original Azure request |
| Stream/store, session key clamp to 64 Unicode code points, nonzero output cap floor 16, temperature, tool choice, standard declared tools | `ProjectPayload`; shared bounded transcript and tool projector |
| Reasoning map with nullish fallback; null `off` disables default reasoning; explicit summary includes encrypted reasoning | `ProjectPayload` |
| Model sampling fields override named fields; request sampling overrides model sampling | `MergeSampling`, called in source order |
| Await payload replacement, accepted-response callback before start, DTO callback before mapper processing | Factory payload hook and transport response/DTO hooks |
| Standard Responses text/tool/reasoning events, authoritative tool arguments, usage and completed/incomplete settlement | Existing `ResponsesTextToolTransport`, unchanged |
| Source HTTP error status, SDK error-object normalization, compact inner object and 4000-character body truncation | `ReadErrorAsync` |

The deployment-map edge case is deliberate: each whole entry is trimmed before splitting at `=`. A trailing whitespace-only value becomes empty and is skipped, preserving the previous mapping. A whitespace value before a second `=` survives the preliminary truthiness check, then becomes an empty mapped value; resolution falls back to the canonical model ID. The authored route cases cover both and the two-segment split behavior.

## Ownership and hooks

`AzureResponsesTransport` sends exactly once with `ResponseHeadersRead`. It immediately transfers the returned response into the existing `HttpSseTransport.OwnedResponse`, before checking cancellation or invoking a callback. Non-2xx responses are read through that same bounded body owner and emit no start. Accepted responses await the response hook before exposing the shared mapper's start. Response metadata is copied into an immutable status/header record.

The Azure enumeration owns the original mapped enumerator and physical response. DTO enumeration owns the original framing enumerator, with the body left open for the response owner. Cleanup joins these enumerators, awaits the owner's original asynchronous body disposal and response disposal, then disposes the request. The terminal is buffered until all those operations settle. Early disposal follows the same chain. Cleanup faults prevent successful settlement. An existing primary failure stays primary; the terminal records `azureCleanupFailed: true` separately. Caller cancellation during held successful cleanup becomes an Aborted error after cleanup, retaining the observed terminal content/usage. Caller client ownership remains borrowed.

Hooks receive owned immutable JSON/model values. A CLR null payload-hook return means no replacement; returning a JSON object replaces the payload. In-place mutation and non-object replacements are outside this profile. Callback exceptions use a fixed native message rather than exposing arbitrary callback text.

## Bounded admission and remaining gaps

Defaults bound payload bytes/characters to 1 MiB and depth to 32; configuration values to 65,536 characters; headers to 128 fields and 8192 ASCII characters per name/value; explicit keys to 4096 printable non-whitespace ASCII characters; HTTP error input to 65,536 bytes. Stream framing, data totals, event count, content slots and accumulated content retain explicit existing bounded limits. A configured positive timeout applies to the native attempt after payload preparation, including streamed input and cleanup settlement; timeout scheduling is not a genuine SDK timing equivalence claim.

Configuration injection admits only the four endpoint/resource/version/deployment-map keys. No process environment, credential resolver, Entra token provider or credential store is read. Resource-derived endpoints admit one ASCII DNS label. Header null removes a field; model/request `api-key` overrides follow SDK precedence. Removing the effective key, routing/framing headers and supplemental Authorization are explicitly rejected. SDK runtime telemetry and OS-derived Pi User-Agent are not reproduced; the default native User-Agent is `PiSharp`, with caller overrides admitted.

The caller must supply a client whose handler disables automatic redirects to reproduce SDK API-key `redirect: manual`. A borrowed `HttpClient` does not expose or permit this adapter to change that handler policy. The fake handler admits no redirects. Retry policy is absent: there is one attempt. Plain-text and JSON-object HTTP errors are covered; primitive error envelopes are rejected. Raw SDK exception text, Source emission snapshots and exact source failure snapshots remain unqualified. In particular, usage observed in a completed DTO followed by a later malformed DTO may not be available in the shared mapper's failed partial snapshot.

Strict UTF-8 and the existing Standard SSE framing profile are deliberate native admission bounds. OpenAI SDK replacement UTF-8, SDK pending-event dispatch and wider sentinel behavior remain OPEN. No new SDK framing profile is supplied. Additional tools, tool search, grammar tools, images, custom query-bearing bases and URI fragments remain unsupported/rejected. Transcript support is exactly the existing bounded Responses projector, including its argument-number and signature constraints. Caller rates come from explicit shared stream options; Azure catalog registration, catalog-derived pricing, automatic Simple context/thinking resolution, identity acquisition and complete provider parity remain OPEN. Existing OpenAI factories, shared owners, Agent, RPC, terminal and SDK files are unchanged.

## Authored checks and coordinator handoff

The standalone framework-only `tests/PiSharp.AzureResponses.Tests` project has 15 directly awaited case groups and 37 complete request-body comparisons. It references Agent only to exercise existing composition. Its project lock is copied byte-for-byte from the existing framework-only Anthropic Simple project; no dependency installation or restore ran. It is deliberately not added to the active companion registry or validation candidate.

Cases cover routes/version/deployment precedence, full option bodies and numeric floors, headers and immutable payload replacement, same-model tool replay, unchanged OpenAI Bearer auth, direct/ChatClient/Agent held cleanup, text/tool/reasoning mapper equivalence, callback order/failure, HTTP errors/truncation/caps, malformed JSON/EOF/sentinel/data limits, cancellation during input and successful held cleanup, early iterator return, primary plus secondary cleanup faults, and zero-send admission failures. Held schedules release gates and await the original operation/disposal in `finally`; assertion failures are retained separately from join failures in the three integration schedules.

Only the coordinator may allocate and execute native checks. Reviewable proposed commands from the isolated repository root are `dotnet build tests/PiSharp.AzureResponses.Tests/PiSharp.AzureResponses.Tests.csproj --no-restore` after its authorized preparation, then `dotnet run --project tests/PiSharp.AzureResponses.Tests/PiSharp.AzureResponses.Tests.csproj --no-build -- --report <fresh-path>`. The report uses `CreateNew`, labels observations `AUTHORED NATIVE; SOURCE QUALIFICATION OPEN`, and records zero genuine source captures. No executed success or independent acceptance is claimed here.

## Pi 1.1.0 provider rename and sampling levels

Pi 1.0.3 renamed the Azure provider id from `azure-openai-responses` to `azure` (source `abe508e1b89912adde45528136c3221eb69acdd7`, `packages/ai/src/api/azure-openai-responses.ts`, `api/azure-openai-config.ts`, `providers/azure.ts`, `env-api-keys.ts`). The api id `azure-openai-responses` and the `AZURE_OPENAI_*` variables are unchanged, and the endpoint/deployment resolution only moved to `azure-openai-config.ts`. The factory's tool-call provider set now names `azure`; a model that still declares the legacy provider id is neither rejected nor aliased, it projects foreign Responses tool calls with flattened call ids, as upstream does. Key discovery maps `AZURE_OPENAI_API_KEY` to `azure` only. Model `samplingParamsByThinkingLevel` (Pi 1.0.2) merges between model and request `samplingParams` for the clamped effective level (a summary without an effort selects `medium`; no effort selects `off`). The suite adds the rename and sampling-level cases. Foundry Chat Completions under the `azure` provider (`openai-completions`) is not composed natively yet.
