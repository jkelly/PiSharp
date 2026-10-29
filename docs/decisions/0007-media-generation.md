# Media generation, embeddings and model operations (PiSharp-only)

Status: requested by the project owner on 2026-10-17, on branch `feature/media-generation`. Experimental and unreleased.

## Context

Pi has image generation through OpenRouter (`packages/ai/src/api/openrouter-images.ts`, ported as `ModelOperations/OpenRouterImages.cs`) and classifiers. It has nothing for embeddings, video, speech, transcription, music or reranking.

The owner wants every generation type that OpenRouter offers, plus embeddings for the providers that have them. They must be tools that agents, subagents and workflows can call. Each kind gets a default model and default options, which the user can set and a call can override.

OpenRouter's non-chat generation endpoints (OpenAPI spec, 2026-10-17):

- `POST /images`, `POST /embeddings` and `POST /videos` (submit, poll, download).
- `POST /audio/speech` and `POST /audio/transcriptions`.
- `POST /rerank`.
- `POST /api/alpha/decisions`.
- Audio output from chat completions (`modalities: ["text", "audio"]`), which is how Lyria music and gpt-audio are served.

It offers no 3D generation.

## Decision

1. **Scope.** This is a PiSharp-only addition, not a port. Upstream-ported types and the frozen catalog shards stay unchanged: `ModelType`, `OperationModel`, the model-operation registry and the hash-pinned `src/PiSharp.Cli/Models/*.json`.
2. **Library (`PiSharp.AI/Media`).** Every public type carries `[Experimental("PISHARP_MEDIA001")]`.
   - **Registry:** `MediaRegistry` resolves `provider/id` specs, provider auth (the model-operation `ProviderAuthResolver` contract) and base URLs, and dispatches by kind: embedding, image, video, speech, transcription, music, rerank and decision. `DiscoverAsync` lists every OpenRouter model of a kind from `GET /models` by output modality.
   - **Embeddings:**
     - `openai-embeddings`: the OpenAI-compatible `/embeddings` (OpenAI, OpenRouter, Mistral and other compatible servers), in batches of 2048.
     - `google-embeddings`: Gemini `batchEmbedContents`, in batches of 100.
   - **Images:** each implements the upstream `IImagesApi` contract.
     - `openai-images`: GPT image models, generations and multipart edits. It sends every documented option: `size`, `quality`, `background` (transparency), `output_format`, `output_compression`, `moderation` and `safety_identifier`, plus `input_fidelity` and `mask` for edits. DALL·E is retired and not supported.
     - `openrouter-image-api`: OpenRouter's `POST /images`. OpenRouter's catalog image models use it instead of the ported chat-completions route, which has no image options.
     - `google-images`: Gemini `generateContent` and Imagen `predict`.
   - **Video:**
     - `openrouter-videos`: `POST /videos` with first and last frames, image, audio and video references, `generate_audio`, `seed`, `size` and `previous_job_id`.
     - `google-videos`: Veo long-running operations.
     - Both run as asynchronous jobs; `VideoJobHooks` makes a job resumable.
     - OpenAI has no video API: it shut down the Videos API and every Sora 2 model on 2026-09-24, and `/v1/videos` answers 404.
   - **Speech:** `openai-speech`, the OpenAI-compatible `/audio/speech` (OpenAI and OpenRouter). It takes a voice, instructions, format and speed, OpenRouter's multi-speaker turns, and voice-cloning `input_references`. OpenRouter speech defaults to MP3, because some models (Kokoro) otherwise answer raw PCM.
   - **Transcription:** `openai-transcription`, `/audio/transcriptions`. It sends multipart to OpenAI and OpenRouter's documented JSON body (`input_audio` base64) to OpenRouter, whose multipart parser rejects .NET's form encoding. It supports timestamps, diarization and key terms.
   - **Music:** `openrouter-chat-audio`, chat completions with audio output. OpenRouter requires `stream: true`, so the audio deltas are assembled from the event stream.
   - **Rerank and decisions:** `openrouter-rerank` (`/rerank`) and `openrouter-decisions` (`/api/alpha/decisions`, at the API's origin rather than under `/api/v1`).
   - **Pass-through options:** every request type has an `Options` JSON object that is merged into the request as it is, for example OpenRouter `provider` routing or model-specific fields.
   - **Catalog:** `MediaCatalog.Builtin` lists the preferred models per kind. An unlisted `provider/id` uses the provider's default API.
3. **Tools (`PiSharp.Durable.Tools/Media`).** The `media` extension provides:
   - `generate_image`, `generate_video` and `embed`.
   - `generate_speech`, `transcribe_audio`, `generate_music`, `rerank` and `decide`. These are deferred by default, so `tool_search` finds them and they do not grow every prompt.
   - `list_media_models` (with `all` for OpenRouter's full list) and `set_media_defaults`.
   - Generated files are saved, by default under `.pi/media`, and the tools return their paths. Paid generations are memoized: image, speech and music results, and a video's job id as soon as the job is submitted. A rerun after a crash therefore never pays twice.
   - Every tool declares structured output, so programs and nested tool calls (workflow steps) get paths, vectors, rankings and answers.
4. **Defaults.**
   - Each kind's defaults are a model and default tool arguments, merged under a call's own arguments; the call wins.
   - They live in `settings.json` as `"media": { "<kind>": { "model": "provider/id", "options": { ... } } }`.
   - `set_media_defaults` changes them in the global or project settings file, through the settings lock, and they take effect at once.
   - Without a default, a kind uses its first available catalog model.
5. **Durable only.** On the owner's choice, only `pisharp durable` installs the extension, and only when a provider with a media model is configured. Subagents start as copies of their parent's agent, so they get the tools. Auth resolves through the CLI model registry: auth.json, models.json and the environment. An OpenAI ChatGPT-subscription token is not used for media.
6. **Settings.** `"media": { "enabled", "outputDir", "deferSpecializedTools", "<kind>": { "model", "options" } }`.

## Service tiers

The owner also wants to switch priority tiers for chat requests: normal, priority, fast and super speed. That covers the regular `pisharp` agent, `pisharp durable` (including per subagent), the OpenAI, Anthropic, Codex and OpenRouter APIs, and the media tools where a tier exists.

1. **Tiers:**
   - `auto`, `normal`, `economy`, `priority`, `fast` and `ultrafast` ("super speed").
   - Aliases: `default`/`standard`, `flex`, `super`/`turbo`.
   - `ChatRequest.ServiceTier` carries the tier per request. Each protocol maps it to its own field (`PiSharp.AI.ServiceTiers`).
   - Protocols without tiers ignore it (Mistral, Bedrock, Azure, Pi messages).
2. **Mapping:**
   - **OpenAI and OpenRouter** (`service_tier` on Responses and Chat Completions): `auto`, `default`, `flex`, `priority`, `fast`, `ultrafast`. Only the `openai` and `openrouter` providers get the field; other providers on the OpenAI APIs (Groq, DeepSeek, xAI, local servers) use other values or reject it.
   - **Anthropic Messages:**
     - `normal` and `economy` send `service_tier: standard_only`.
     - `auto` and `priority` send `service_tier: auto`, which uses priority capacity where the organization has a commitment.
     - `fast` and `ultrafast` send `speed: "fast"` with the `fast-mode-2026-02-01` beta, but only for the models that serve fast mode (Opus 5.5, Opus 5, Opus 4.8). Other models reject `speed`, so they get `service_tier: auto` instead.
     - Only the `anthropic` and `openrouter` providers get these fields; Anthropic-compatible providers (MiniMax, Kimi Coding, GitHub Copilot) get none.
   - **OpenAI Codex:** `economy` sends `flex`; `priority`, `fast` and `ultrafast` send `priority`, because the backend rejects `fast`.
   - **Google:** `standard`, `flex` or `priority`.
3. **Per-request plumbing:**
   - The request factories already scoped themselves per request session id (`ScopedTo`). They now also scope by the request's tier, so one cached transport serves every tier.
   - The Responses transport accepts a request tier that differs from its configured default.
4. **Regular `pisharp`:**
   - The tier starts from `--tier <tier>`, else the `serviceTier` setting.
   - `/tier <tier>` changes it for the next request; `/tier <tier> default` also saves it.
   - The footer shows it.
   - It reaches every request through `AgentConfiguration.ServiceTier` (read per request by `TurnRunner`).
5. **`pisharp durable`:**
   - The tier is a `pi.agent` field (`AgentChange.ServiceTier`). It is pinned in the request checkpoint only when set, so upstream-shaped records are unchanged, and sent as `DurableStreamOptions.ServiceTier`.
   - Child conversations inherit it, and the `subagent` tool takes an optional `tier`.
   - `--tier` sets the main conversation's tier for the run. A new session starts from the `serviceTier` setting.
   - `/tier <tier>` changes the shown conversation's tier, and the footer shows it.
6. **Media:**
   - OpenRouter's media endpoints have no tiers. For OpenRouter images the conversation's tier picks `provider.sort` instead: economy → `price`, priority → `throughput`, fast and ultrafast → `latency`.
   - A call's own `provider` options win.
7. **Experimental.** On the owner's choice (2026-10-28), the tier API is experimental like the media API:
   - `[Experimental("PISHARP_TIERS001")]` marks `ServiceTiers`, `ChatRequest.ServiceTier`, `AgentConfiguration.ServiceTier`, `TurnRunner.ServiceTier` and `SessionRuntimeRegistryOptions.ServiceTier`.
   - It also marks the new request-factory options: `ServiceTier`, `Speed` and `PiSharpTier` for Anthropic Messages, and `ServiceTier` for Google and Chat Completions.
   - `PISHARP_DURABLE001` already covers the durable members.
   - The `ServiceTier` options that the Responses and Codex transports had before this change stay as they were.
   - The projects in this repository that pass tiers along suppress the diagnostic in their project files.
8. **Intentional differences from Pi.** Pi has no service tiers, so these exist only in PiSharp:
   - `--tier` (listed in `pisharp --help`).
   - The `/tier` command.
   - The tier in the footer.
   - The `serviceTier` setting.

   Without a tier, requests and the footer are unchanged: no tier field is sent and nothing is added to the footer. The website's parity list (`website/src/data/parity.json`) gets the entry when the feature is released, because merging website data publishes the site at once.

## Verification

The `media.*` cases in `tests/PiSharp.Durable.Tests/MediaCases.cs` use fake HTTP handlers. They pin:

- Each API's request shape, retries, errors and redirect credential stripping.
- The tools' files, memo replay and job resume.
- The defaults merge and persistence, and tool deferral.
- One run through the Harness that includes a nested `embed` call.

The `tiers.*` cases in `tests/PiSharp.ProviderSync.Tests/ServiceTierCases.cs` pin each protocol's tier fields. The `service-tier.*` durable case covers a conversation tier reaching and leaving requests.

Live provider requests are not part of the suite. On 2026-10-17, live checks passed:

- **OpenAI:**
  - Embeddings, image generation and edits.
  - Transparency, WebP compression, and a masked edit with `input_fidelity` that changed only the masked area.
  - `gpt-4o-mini-tts` speech and `gpt-4o-mini-transcribe`.
- **OpenRouter:**
  - Embeddings with `dimensions`, and Image API generation and edits (Gemini, FLUX with `seed`, transparency through `openai/gpt-image-1`).
  - Kokoro speech.
  - Whisper transcription with segment and word timestamps.
  - Cohere rerank and TypeSafe JEV decisions.
  - A 30-second Lyria clip.
  - Model discovery for every kind.
- **Service tiers:**
  - OpenAI Responses reported `default` and `flex`. `fast` was accepted and reported as `priority`, then `default` when downgraded.
  - OpenRouter Chat Completions reported `flex` and `priority`. `ultrafast` fell back to `default` on a model without an ultrafast endpoint.
  - Anthropic Opus 5.5 through OpenRouter's Messages API served `speed: fast` (`usage.speed: fast`), and Haiku 5.5 honored `standard_only`.

OpenRouter video and every Google request (Gemini, Imagen and Veo directly) follow the published API references and have not been exercised live.
