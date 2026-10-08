# Explicit live terminal composition

This source-only milestone connects the existing Windows terminal frontend and durable RPC host to selected live provider adapters. It is based on `d761b863ba90164cf60c9d79fcef65c83a18183f`. No build, test, network request, authenticated launch or live-provider qualification was performed in this lane. Independent composition review and candidate-pinned offline execution remain required before launch handoff.

## User entry and lifecycle

The explicit entry is:

```text
PiSharp.Cli session terminal --live --provider <openai|openrouter|anthropic> --model <pinned chat model id> --workspace <absolute existing directory> --session <absolute JSONL> --session-mode new-lazy
```

An existing matching session can use the existing open mode. Optional `--max-output-tokens` is 1..8192, defaults to 1024 and may not exceed the selected catalog model's maximum. This is a token bound, not a dollar-spend guarantee. Existing exact `--allow-read`, `--allow-write` and explicitly configured Bash/extension grants retain their existing policy and containment behavior. No tool target is authorized merely by selecting live mode. Terminal requires explicit `--live` or the existing `--terminal-preview` entry. Redirected/non-console admission and original terminal restoration continue through Program's existing host. The offline script commands and preview flag remain available; mixed live/offline provider flags are rejected.

The path is Program -> TerminalSessionCommand -> existing RPC connection/frontend -> RpcSessionCommand -> existing profile's tool/session registry -> PersistentAgentSession -> Agent -> existing provider HTTP/SSE adapter -> prepared tools/mandatory final policy -> RPC events -> existing terminal view. The internal OfflineSessionProfile name is retained to avoid refactoring unrelated caller paths; its optional live connection selects actual model metadata/HTTP while preserving the same prepared tools, session owner, nested-call binding and shutdown. Native terminal input/view code is not changed by this milestone.

Provider/model selection parses a byte-pinned embedded released catalog without reading keys or sending. At user-launched runtime, connection admission reads only the selected existing ENV key: OPENAI_API_KEY, OPENROUTER_API_KEY or ANTHROPIC_API_KEY. The credential stays in the provider connection's private in-memory binding; it is not a public record property, model wire value, transcript, logged request or persistent credential setting. There is no CLI key-value flag, provisioning, automatic prompt, automatic startup HTTP send or retry added by this composition. Authenticated testing/launch remains a separate user handoff; none was performed here.

The live profile owns NativeHttpModelProvider instances through the original session/profile shutdown. Each provider owns its HttpClient and default handler; injected handlers remain caller-owned. Existing dispatcher admission fencing, abort, original Agent/session joins, terminal-stopped acknowledgment, writer/input settlement and console restoration remain in place. Admission failure attempts owned live-provider and extension cleanup before propagating the original failure. Existing final file target policy, canonical containment, reserved session/script/extension paths and explicit Bash grants are unchanged. Summary generation uses the selected connection rather than the offline inert key, after the existing summary request validation; its provider factory mapping remains part of provider review.

## Provider and test seams

`LiveSessionSelection.Parse(provider, model, maximumTokens)` is pure catalog/argument admission. Only supported chat API mappings are accepted: openai/openai-responses, openrouter/openai-completions and anthropic/anthropic-messages. API variants, catalog entries and metadata do not claim current service availability or SDK parity. Unknown or unsupported selections are rejected without using the user's value as an error message.

`LiveSessionSelection.Connect(runtime)` uses `LiveSessionRuntime(ReadEnvironment, CreateHttpHandler)` to admit the selected existing credential and an optional caller-owned HTTP handler. A null handler delegates default client/handler creation to NativeProviderFactory. `LiveSessionConnection.CreateTransport(outputTokens, summary)` delegates to the reviewed NativeProviderFactory CreateResponses/CreateCompletions/CreateAnthropic methods and retains returned provider owners until original session settlement. No provider transport implementation was changed. The provider factory dependency is worker commit 4c6fe6798e92f2b0901df5ba0b0a5431afd13e84; its resolved Completions wire options are retained.

The optional `liveRuntime` seam in `Program.RunTerminalHostAsync`, `TerminalSessionCommand.RunObservedAsync` and `TerminalSessionCommand.RunWithLiveRuntimeAsync` permits an inert key and fake HTTP handler with the owned test console. The existing InternalsVisibleTo for PiSharp.CodingAgent.Tests exposes these seams. The E2E worker supplied five cases at 8ab05aa096bca95415beb8e0cc6e4386ce6d3de2. Composition supplies a test-local adapter, runner registration and a pinned OpenRouter model/endpoint fixture alignment to exercise the actual CLI host, native session/Agent/provider factory, real prepared tool effect, displayed response, durable transcript, cancel/failure and original HTTP/terminal/session joins. This milestone does not substitute a synthetic normalized transport for those tests and records those cases as authored and unexecuted. The adapter forwards the existing awaited RPC observer through RunObservedAsync(..., liveRuntime: runtime).

## Embedded catalog provenance

The three resources are copied byte-for-byte from `package/dist/providers/data/{openai,openrouter,anthropic}.json` in the existing admitted `pi-ai-0.99.1.tgz`, package SHA256 `f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3`. Extraction was read-only archive data processing, without importing or executing JavaScript/TypeScript, acquiring another package or making a source capture. Runtime admission verifies each complete shard hash before FrozenModelCatalog parsing:

| Resource | SHA256 |
| --- | --- |
| anthropic.json | 3ff00b68990382e42626ebd1b65dcc28ae3cad9e6faa8920331ba92f067f1b3a |
| openai.json | ca5ec1028efc512502591bf2562a2a6dc330f26a5f4c4a08c8a149a43c5bd7da |
| openrouter.json | b49405ced089750d475945738a91ac669fa00e04d574da7720935bcfe16ba272 |

The retained Pi MIT notice applies; see THIRD-PARTY-NOTICES.md. These pinned catalog declarations retain source metadata and are not live model recommendations, up-to-date pricing claims or service capability qualification. No model is selected by default. Actual provider API/compatibility behavior and the final launch command need the provider lane's review and separate evidence.

## Ownership and remaining review

The composition lane owns Program.cs, RpcSessionCommand.cs, TerminalSessionCommand.cs and OfflineSessionProfile.cs. Provider work owns LiveSessionSelection.cs and the new catalog resource binding; TUI work owns view/input usability; E2E work owns tests and registration. This is an isolated source milestone for those lanes, not a main merge. All historical identity/source parity, SDK/provider/phase/release/package qualification gates remain unchanged. The earlier accepted original PiMessages /3 criterion is unrelated to this live CLI flow and is not evidence of authenticated usability.
## Recovery and fixture ownership correction

`get_state.data.pisharpRunOwnerSettled` is a read-only Boolean taken under the dispatcher's existing gate: true means its original `_run` owner is absent at the instant of observation. `agent_settled` publication itself precedes that owner clear. The added field does not grant input authority, alter admission or guarantee acceptance of a future prompt. Recovery fixtures query the existing `/state` command after the settlement event and wait for the original run owner to clear before submitting the next prompt. Existing policy and operation admission remain authoritative.

The test adapter uses RunObservedConfiguredAsync with in-memory default win32 keybindings and a fixture-local path; the readText callback returns null, so user keybindings/settings are not read. Fixture disposal cancels and then awaits the original terminal host task without a timeout proxy before disposing the borrowed HTTP handler. Scenario deadlines still bound observations; an expired deadline does not detach physical cleanup. Provider error acceptance checks assistant role, error stopReason and nonempty errorMessage instead of searching an arbitrary event string.