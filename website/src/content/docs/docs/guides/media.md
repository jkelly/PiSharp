---
title: Media tools
description: Generate images, video, speech and music, embed and rerank text, and ask decision models from pisharp durable. Experimental and unreleased.
sidebar:
  badge:
    text: Unreleased
    variant: caution
---

:::caution[Experimental and unreleased]
The media tools are on PiSharp's `main` branch but not in any release or NuGet package yet. To use them, build PiSharp from source and reference the projects directly (see [Build from source](/docs/contribute/build/)). Their API is marked `[Experimental("PISHARP_MEDIA001")]`, so suppress `PISHARP_MEDIA001` in your project to compile it, and it may change or be removed without notice.
:::

[`pisharp durable`](/docs/guides/durable/) can give the model ten media tools: image, video, speech and music generation, transcription, embeddings, reranking, decision models, and two tools to list models and set defaults. They are not in any release of the CLI yet. To try them, run a CLI built from source (see [Run the CLI from source](/docs/contribute/build/#run-the-cli-from-source)). Pi has no media tools; this is a PiSharp addition ([decision 0007](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0007-media-generation.md)). To use the same tools in your own app, see [Media library](/docs/embed/media/).

## Turn them on

`pisharp durable` installs the media tools when both hold:

- The `media.enabled` setting is not `false`. It is on unless you turn it off.
- At least one provider that has a media model has credentials.

Credentials come from the same places as for chat models: `auth.json` (OAuth logins are refreshed), API keys and headers in `models.json`, and environment variables. A provider's base URL from `models.json` applies too. One exception: an OpenAI login with a ChatGPT subscription only reaches the Codex chat route, so the media tools treat OpenAI as not configured then. Use an OpenAI API key for OpenAI media models.

| Provider | Key variable | Media it serves |
| --- | --- | --- |
| `openai` | `OPENAI_API_KEY` | Images, speech, transcription, embeddings |
| `google` | `GEMINI_API_KEY` | Images, video, embeddings |
| `openrouter` | `OPENROUTER_API_KEY` | Every kind: images, video, speech, transcription, music, embeddings, rerank, decisions |
| `mistral` | `MISTRAL_API_KEY` | Embeddings |

Other providers with an OpenAI-compatible embeddings endpoint can serve embeddings when you name a model as `provider/model`. With nothing configured, the tools stay out of the prompt entirely.

## The tools

| Tool | What it does | Offered |
| --- | --- | --- |
| [`generate_image`](#generate_image) | Generates or edits images and saves them. | Always |
| [`generate_video`](#generate_video) | Generates a video clip and saves it. Takes minutes. | Always |
| [`embed`](#embed) | Embeds texts, or ranks them by similarity to a query. | Always |
| [`list_media_models`](#list_media_models) | Lists the configured media models and the defaults. | Always |
| [`set_media_defaults`](#set_media_defaults) | Sets the default model and arguments of a kind, and saves them. | Always |
| [`generate_speech`](#generate_speech) | Text to speech, saved to an audio file. | Deferred |
| [`transcribe_audio`](#transcribe_audio) | Speech to text. | Deferred |
| [`generate_music`](#generate_music) | Music or other audio from a prompt. | Deferred |
| [`rerank`](#rerank) | Ranks documents by relevance to a query. | Deferred |
| [`decide`](#decide) | Asks a decision model calibrated questions about some content. | Deferred |

Deferred tools are kept out of every request to save tokens. The model finds them with `tool_search`, which `pisharp durable` offers whenever a tool is deferred, and loads the matches; a loaded tool stays offered for the rest of the conversation. Set `media.deferSpecializedTools` to `false` to offer all ten from the start.

Subagents get the media tools too, because a child conversation starts with its parent's tools.

Every media tool is replay-safe. See [Crash safety](#crash-safety) for what a rerun after a crash does.

### Choosing a model

Each call that takes a `model` argument resolves it in this order:

1. The call's `model`.
2. The kind's default model, from `set_media_defaults` or the `media.<kind>.model` setting.
3. The first configured model of the kind, in the built-in list's order (OpenAI and Google before OpenRouter).

A model is named as `provider/model`. The provider is the first segment, so `openrouter/openai/text-embedding-3-small` is OpenRouter's. A bare id works when exactly one listed model of the kind has it. You can name any model of a configured provider, listed or not; an unlisted one uses that provider's API for the kind. With no model at all, the call fails with "No *kind* model is configured".

The built-in models:

| Kind | Models |
| --- | --- |
| Image | `openai/gpt-image-1.5`, `openai/gpt-image-2`, `openai/gpt-image-1`, `openai/gpt-image-1-mini`, `google/gemini-2.5-flash-image`, `google/imagen-4.0-generate-001` (no reference images), and OpenRouter's image models from the model catalog |
| Video | `google/veo-3.0-generate-001`, `google/veo-3.0-fast-generate-001`, `google/veo-3.1-generate-preview`, `openrouter/google/veo-3.1-fast`, `openrouter/google/veo-3.1-lite`, `openrouter/google/veo-3.1`, `openrouter/kwaivgi/kling-v3.0-std`, `openrouter/bytedance/seedance-2.0-fast`, `openrouter/runway/gen-4.5` |
| Speech | `openai/gpt-4o-mini-tts`, `openai/tts-1`, `openrouter/hexgrad/kokoro-82m`, `openrouter/google/gemini-3.1-flash-tts-preview`, `openrouter/elevenlabs/eleven-flash-v2.5`, `openrouter/elevenlabs/eleven-v3` |
| Transcription | `openai/gpt-4o-mini-transcribe`, `openai/gpt-4o-transcribe`, `openai/whisper-1`, `openrouter/openai/whisper-large-v3-turbo`, `openrouter/openai/gpt-4o-mini-transcribe`, `openrouter/elevenlabs/scribe-v2` |
| Music | `openrouter/google/lyria-3-clip-preview` (30-second clips), `openrouter/google/lyria-3-pro-preview` (full songs) |
| Embedding | `openai/text-embedding-3-small` (1536 dimensions), `openai/text-embedding-3-large` (3072), `google/gemini-embedding-001` (3072), `openrouter/openai/text-embedding-3-small`, `openrouter/openai/text-embedding-3-large`, `mistral/mistral-embed` (1024) |
| Rerank | `openrouter/cohere/rerank-4-fast`, `openrouter/cohere/rerank-4-pro`, `openrouter/voyageai/rerank-2.5-lite` |
| Decision | `openrouter/typesafe/jev-1.14`, `openrouter/perplexity/pplx-decider-flash-v1.2` |

OpenAI has no video API. `list_media_models` with `all` lists every OpenRouter model of a kind, beyond this list.

Every tool that takes `options` sends its members into the provider request as they are, for provider-specific fields such as OpenRouter's `provider` routing. Paths in arguments resolve against the conversation's working directory.

## generate_image

Generates or edits images with an image model and saves them.

| Parameter | Meaning |
| --- | --- |
| `prompt` | Required. What to draw: subject, style, composition, text to render. |
| `model` | `provider/model`, for example `openai/gpt-image-1.5`, `google/gemini-2.5-flash-image` or `openrouter/<id>`. |
| `reference_images` | Paths of images to edit or use as references, for models that accept images. A single string is accepted. |
| `mask` | OpenAI edits: a PNG the size of the first reference image whose fully transparent pixels mark the area to change. |
| `count` | Number of images. Default 1. |
| `size` | `auto`, `WIDTHxHEIGHT` (OpenAI: 1024x1024, 1536x1024, 1024x1536; newer models any multiple of 16 up to 3840x2160), or a tier such as `2K` (OpenRouter). |
| `aspect_ratio` | Google and OpenRouter: for example `1:1`, `16:9`, `9:16`, `4:3`, `21:9` or `auto`. |
| `resolution` | Google and OpenRouter: `512`, `1K`, `2K` or `4K`. |
| `quality` | `low`, `medium`, `high` or `auto` (OpenAI and OpenRouter). |
| `background` | `transparent`, `opaque` or `auto` (OpenAI and OpenRouter). Transparent needs PNG or WebP output. |
| `output_format` | `png`, `jpeg` or `webp`; OpenRouter vectorization models also `svg`. |
| `output_compression` | 0 to 100, for JPEG or WebP output. |
| `moderation` | OpenAI: `auto` or `low`. |
| `input_fidelity` | OpenAI edits: `low` or `high`. High keeps faces and details of the reference images. |
| `seed` | Seed for repeatable output, where the model supports it (OpenRouter). |
| `options` | Extra provider fields, for example `{"provider":{"order":["openai"]}}` on OpenRouter or `{"steps":30}` for FLUX. |
| `output_path` | File or directory to save to. Default: a new file in the media output directory. |

The result lists the saved files. If the conversation's model accepts images, the result also shows it up to four of them, those within the `read` tool's size limits. Token usage is reported with the call.

## generate_video

Submits a video job, waits for it, and saves the clip. It polls every 5 seconds and gives up after 20 minutes, with a message that the job may still complete on the provider. While it waits, the call's details report the job's state and progress.

| Parameter | Meaning |
| --- | --- |
| `prompt` | The shot: subject, action, camera, style, and any dialogue or sound. Optional for models that animate an image alone. |
| `model` | For example `openrouter/google/veo-3.1-fast`, `openrouter/kwaivgi/kling-v3.0-std` or `google/veo-3.0-generate-001`. |
| `image` | Path of an image to use as the first frame. |
| `last_frame` | OpenRouter: path of an image to use as the last frame. |
| `references` | OpenRouter: paths or `https` URLs of reference images, audio or video (audio and video only for models that take them, such as Seedance 2). |
| `duration_seconds` | Clip length in seconds, model-specific (Veo: 4, 6 or 8). |
| `aspect_ratio` | `16:9`, `9:16`, `1:1`, `4:3`, `3:4`, `3:2`, `2:3`, `21:9` or `9:21`. |
| `resolution` | `360p`, `480p`, `720p`, `768p`, `1080p`, `1K`, `2K` or `4K`. |
| `size` | OpenRouter: exact pixels `WIDTHxHEIGHT`, instead of `resolution` and `aspect_ratio`. |
| `generate_audio` | OpenRouter: generate a soundtrack, where the model can. |
| `seed` | Seed for repeatable output, where supported. |
| `negative_prompt` | Google: what to avoid. |
| `previous_job_id` | OpenRouter: the `jobId` of a finished video to edit or extend, for models that support continuation. |
| `options` | Extra provider fields, for example OpenRouter `{"provider":{"options":{"<slug>":{...}}}}`. |
| `output_path` | File or directory to save to. |

The result includes the provider's `jobId`, which `previous_job_id` takes.

## embed

Creates text embeddings. With `query`, it also ranks the inputs by cosine similarity to the query.

| Parameter | Meaning |
| --- | --- |
| `input` | Required. The texts to embed, each a non-empty string. A single string is accepted. |
| `query` | Also rank the inputs by cosine similarity to this text. The result lists them best first. |
| `model` | For example `openai/text-embedding-3-small`, `google/gemini-embedding-001` or `openrouter/<id>`. |
| `dimensions` | Shorter vectors, for models that support it. |
| `task_type` | Gemini task type, for example `RETRIEVAL_DOCUMENT`, `RETRIEVAL_QUERY` or `SEMANTIC_SIMILARITY`. |
| `options` | Extra provider fields, for example `{"input_type":"search_query"}`. |
| `output_path` | Write the vectors to this JSON file: `{"model", "dimensions", "items": [{"text", "embedding"}]}`. |
| `include_vectors` | Also print the vectors in the result text. They are large. |

## generate_speech

Turns text into spoken audio: narration, voice-overs, multi-speaker dialogue, or a cloned voice.

| Parameter | Meaning |
| --- | --- |
| `text` | Text to speak. `text` or `turns` is required. |
| `turns` | Multi-speaker models: an array of `{text, voice, instructions}`, instead of `text`. |
| `model` | For example `openai/gpt-4o-mini-tts`, `openrouter/elevenlabs/eleven-v3` or `openrouter/hexgrad/kokoro-82m`. |
| `voice` | Provider voice id, for example OpenAI's `alloy`, `ash`, `coral`, `nova`, `sage` or `shimmer`. |
| `instructions` | Tone, pacing or emotion (OpenAI `gpt-4o-mini-tts`, Gemini TTS, Fish Audio). |
| `format` | `mp3` (default) or `pcm` on OpenRouter; OpenAI also `opus`, `aac`, `flac` and `wav`. |
| `speed` | Playback speed multiplier, where supported (0.25 to 4 on OpenAI). |
| `voice_samples` | OpenRouter voice cloning: paths of one to three audio clips of the voice, or an image for voice design. |
| `options` | Extra provider fields. |
| `output_path` | File or directory to save to. |

## transcribe_audio

Transcribes an audio file, optionally with timestamps and speaker labels.

| Parameter | Meaning |
| --- | --- |
| `path` | Required. The audio file: mp3, wav, m4a, ogg, flac or webm, up to 25 MB. |
| `model` | For example `openai/gpt-4o-mini-transcribe`, `openai/whisper-1` or `openrouter/elevenlabs/scribe-v2`. |
| `language` | ISO-639-1 code such as `en` or `ja`. Detected when omitted. |
| `prompt` | OpenAI: text that guides spelling or style, such as names and jargon. |
| `temperature` | Sampling temperature. |
| `timestamps` | Include segment and word timestamps (OpenAI only on `whisper-1`). |
| `diarize` | OpenRouter: label each word with its speaker. Implies `timestamps`. |
| `keyterms` | OpenRouter: names or terms to bias recognition toward, on some models. |
| `options` | Extra provider fields, sent as form parts. |
| `output_path` | Also write the transcript here. A `.json` path gets the full result with timestamps; any other path gets the text. |

## generate_music

Generates music, a clip or a full song, or other audio from a prompt.

| Parameter | Meaning |
| --- | --- |
| `prompt` | Required. Genre, mood, instruments, tempo, structure, and lyrics if wanted. |
| `model` | `openrouter/google/lyria-3-clip-preview` (30-second clip) or `openrouter/google/lyria-3-pro-preview` (full song). |
| `images` | Paths of images to set the mood, for models that accept images. |
| `seed` | Seed for repeatable output, where supported. |
| `options` | Extra request fields, for example `{"audio":{"voice":"alloy","format":"wav"}}` for gpt-audio. |
| `output_path` | File or directory to save to. |

If the model also returns text, such as lyrics, the result includes it.

## rerank

Ranks documents by relevance to a query with a rerank model. It is more precise than embeddings for ordering search results.

| Parameter | Meaning |
| --- | --- |
| `query` | Required. What to rank the documents against. |
| `documents` | Required. The texts to rank, each a non-empty string. |
| `top_n` | Return only the best N. |
| `model` | For example `openrouter/cohere/rerank-4-fast` or `openrouter/voyageai/rerank-2.5-lite`. |
| `options` | Extra provider fields, for example `{"provider":{"order":["cohere"]}}`. |

## decide

Asks a decision (classifier) model calibrated questions about some content. Decision models are cheap and fast, and suit routing, moderation and grading.

| Parameter | Meaning |
| --- | --- |
| `state` | Required. The content to judge: a string, or a JSON object or array of related context. |
| `questions` | Required. Named questions, each one of `{"type":"noul","instructions":...}` (a yes/no probability), `{"type":"choice","instructions":...,"criteria":{"label":"meaning",...}}`, `{"type":"score","instructions":...,"criteria":["lowest",...,"highest"]}` or `{"type":"box","instructions":...}` (an image bounding box). |
| `model` | For example `openrouter/typesafe/jev-1.14` or `openrouter/perplexity/pplx-decider-flash-v1.2`. |
| `options` | Extra provider fields. |

## list_media_models

Lists the media models whose providers are configured, kind by kind, with the default marked `[default]`, image models that take references marked `[reference images]`, video models that take a first frame marked `[first frame]`, embedding sizes, and the default options.

| Parameter | Meaning |
| --- | --- |
| `kind` | `image`, `video`, `speech`, `transcription`, `music`, `embedding`, `rerank` or `decision`. Omit for all. |
| `all` | Also list every model OpenRouter offers for the kind. |

## set_media_defaults

Sets the default model and default arguments of a kind and saves them in settings. A call's own arguments still win over any default. The model uses it when you ask to make something the default.

| Parameter | Meaning |
| --- | --- |
| `kind` | Required. `image`, `video`, `speech`, `transcription`, `music`, `embedding`, `rerank` or `decision`. |
| `model` | `provider/model` to use when a call names none. It must resolve. An empty string removes the default model. |
| `options` | Default arguments of the kind's tool, by argument name, merged into the current ones. A `null` value removes one. For example image `{"background":"transparent","output_format":"png"}` or speech `{"voice":"alloy"}`. |
| `replace` | Replace the default options instead of merging. |
| `clear` | Remove every default of the kind. |
| `scope` | `global` (default; every project) or `project` (this project's `.pi/settings.json`). |

## Settings

The `media` object in `settings.json`, global or project:

```json
{
  "media": {
    "enabled": true,
    "outputDir": ".pi/media",
    "deferSpecializedTools": true,
    "image": { "model": "openai/gpt-image-1.5", "options": { "quality": "high", "output_format": "webp" } },
    "speech": { "options": { "voice": "nova" } }
  }
}
```

| Key | Default | Meaning |
| --- | --- | --- |
| `media.enabled` | `true` | `false` keeps the media tools out of `pisharp durable`. |
| `media.outputDir` | `.pi/media` | Where files go when a call names no `output_path`. Relative to the conversation's working directory. |
| `media.deferSpecializedTools` | `true` | Defer `generate_speech`, `transcribe_audio`, `generate_music`, `rerank` and `decide`. |
| `media.<kind>.model` | none | The kind's default model, as `provider/model`. |
| `media.<kind>.options` | none | Default arguments of the kind's tool, by argument name. |

`<kind>` is `image`, `video`, `speech`, `transcription`, `music`, `embedding`, `rerank` or `decision`. Settings are read when the session opens; `set_media_defaults` changes them for the running session and writes them to the file. An empty `media` object is removed from the file.

## Output files

A call without `output_path` saves to the media output directory, under a name made of the kind, the UTC time, a random id and the first words of the prompt:

```text
.pi/media/image-20261017-142530-3f9a2c-a-red-fox.png
```

The random id keeps parallel calls with the same prompt from overwriting each other. A video without a prompt uses `clip` as its words, and speech uses its text or its first turn.

An `output_path` that ends in `/` or `\`, or has no extension, is a directory: files go there under the generated name. A file path is used as given for the first file; more images get `-2`, `-3` and so on before the extension. Several images saved to a directory get `-1`, `-2` and so on. The file extension follows the returned type: `.png`, `.jpg`, `.webp`, `.gif`, `.svg`, `.mp4`, `.webm`, `.mov`, `.mp3`, `.wav`, `.ogg`, `.flac`, `.aac`, `.m4a` or `.pcm`.

`embed` and `transcribe_audio` write `output_path` exactly as given.

## Structured output

`generate_image`, `generate_video`, `generate_speech` and `generate_music` return the same structure:

```json
{
  "model": "openai/gpt-image-1.5",
  "files": [{ "path": ".pi/media/image-20261017-142530-3f9a2c-a-red-fox.png", "mimeType": "image/png", "bytes": 1543210 }],
  "text": "a note from the model, when it sent one",
  "jobId": "the provider's job id, for video",
  "cost": 0.04
}
```

| Tool | Structure |
| --- | --- |
| `embed` | `{model, dimensions, vectors, ranking?: [{index, score}], path?, inputTokens?}` |
| `transcribe_audio` | `{model, text, details?, path?, cost?}`. `details` holds the provider's full result, with timestamps when asked. |
| `rerank` | `{model, ranking: [{index, score}], cost?}` |
| `decide` | `{model, answers, cost?}` |

`list_media_models` and `set_media_defaults` return text only. Programs get the structure when they call a tool, for example from [code mode](/docs/guides/codemode/) or a nested call in your own tool.

`cost` is the price in US dollars, when the provider reports one. `embed` reports no cost; its `inputTokens` is the provider's token count, when it sends one.

## Crash safety

All ten tools are replay-safe, so a call that a crash interrupts runs again when the session reopens. What the rerun does depends on the tool:

- **`generate_image`, `generate_speech`, `generate_music`.** The tool records that the paid request is going out before it sends it, and saves its result once the files are written. A rerun that finds the result returns it without calling the provider. A rerun that finds the request sent but no result fails with: "The *kind* request was sent before the process stopped, and its result was not saved. It may have been charged, so it was not sent again". Check the output directory for partly saved files, or call the tool again.
- **`generate_video`.** The tool saves the provider's job id as soon as the job is submitted. A rerun with a saved id resumes polling the same job instead of submitting another. A crash between sending and saving the id fails the rerun as above.
- **`embed`, `transcribe_audio`, `rerank`, `decide`.** Nothing is saved for them, so a rerun sends the request again and may be charged again.
- **`list_media_models`, `set_media_defaults`.** They make no paid requests.

## Service tiers

The media APIs have no tier field. One exception uses the conversation's [service tier](/docs/guides/service-tiers/) anyway: `generate_image` on OpenRouter sets OpenRouter's provider routing from it, unless the call's `options` already set `provider`.

| Tier | OpenRouter `provider.sort` |
| --- | --- |
| `economy` | `price` |
| `priority` | `throughput` |
| `fast`, `ultrafast` | `latency` |
| `auto`, `normal`, none | not set |
