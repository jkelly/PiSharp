---
title: Providers and models
description: Sign in, choose a model, and add your own endpoints.
---

PiSharp has every provider and chat API that Pi v1.2.0 has, with Pi's model catalogs. Pi's [Providers](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/providers.md) and [Models](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/models.md) pages apply.

## Sign in

Type `/login` in the terminal UI and pick a provider. Every built-in provider except `openai-codex` takes a key there: an API key or token, AWS credentials or a bearer token for Bedrock, and Google Cloud credentials for Vertex. These also offer a browser or device sign-in: Anthropic, OpenAI (ChatGPT), OpenAI Codex, GitHub Copilot, OpenRouter, xAI, Kimi, Meta and Radius. `openai-codex` has only the sign-in. `/logout` removes a sign-in. `/login <provider>` takes a provider id or name and skips the provider list.

Sign-ins are saved in `~/.pi/agent/auth.json`, which Pi uses too.

You can also set the provider's environment variable, such as `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` or `GEMINI_API_KEY`; the [table below](#built-in-providers) has them all. `--api-key` sets a key for one run. When several are set, `--api-key` wins, then `auth.json`, then an `apiKey` in `models.json`, then the environment.

Some providers need more than a key:

- **Amazon Bedrock** uses the AWS credential chain: `AWS_PROFILE`, access keys, SSO, `credential_process` or a Bedrock API key in `AWS_BEARER_TOKEN_BEDROCK`. Set `AWS_REGION`.
- **Google Vertex** takes an API key or Application Default Credentials (user, service account, impersonated service account and workload identity federation).
- **Cloudflare** needs `CLOUDFLARE_ACCOUNT_ID`, and `CLOUDFLARE_GATEWAY_ID` for AI Gateway.
- **Azure OpenAI** reads the `AZURE_OPENAI_*` variables. `AZURE_OPENAI_BASE_URL` wins; otherwise `AZURE_OPENAI_RESOURCE_NAME` becomes `https://<name>.openai.azure.com/openai/v1`. As in Pi, the name is taken as given and read as a URL, so names with dots or upper case work.
- **Anthropic** also works without a key through workload identity federation.
- **llama.cpp** connects to a local router server. See [Local models with llama.cpp](/docs/guides/llama-cpp/).

[Environment variables](/docs/reference/environment/#provider-keys) lists every variable, Bedrock's and Vertex's included. For scripts, `pisharp auth print-api-key`, `print-bearer-token` and `check` resolve credentials as a session would; see [auth](/docs/reference/cli/#auth).

## Built-in providers

| Provider id | Name | APIs | Key variable | `/login` sign-in |
| --- | --- | --- | --- | --- |
| `amazon-bedrock` | Amazon Bedrock | `bedrock-converse-stream` | AWS credential chain, or `AWS_BEARER_TOKEN_BEDROCK` | |
| `ant-ling` | Ant Ling | `openai-completions` | `ANT_LING_API_KEY` | |
| `anthropic` | Anthropic | `anthropic-messages` | `ANTHROPIC_API_KEY` (or `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_OAUTH_TOKEN`) | Anthropic (Claude Pro/Max) |
| `azure` | Azure | `azure-openai-responses`, `openai-completions` | `AZURE_OPENAI_API_KEY` | |
| `baseten` | Baseten | `openai-completions` | `BASETEN_API_KEY` | |
| `cerebras` | Cerebras | `openai-completions` | `CEREBRAS_API_KEY` | |
| `cloudflare-ai-gateway` | Cloudflare AI Gateway | `anthropic-messages`, `openai-completions`, `openai-responses` | `CLOUDFLARE_API_KEY`, with `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_GATEWAY_ID` | |
| `cloudflare-workers-ai` | Cloudflare Workers AI | `openai-completions`, `cloudflare-workers-ai-system-one` | `CLOUDFLARE_API_KEY`, with `CLOUDFLARE_ACCOUNT_ID` | |
| `deepseek` | DeepSeek | `openai-completions` | `DEEPSEEK_API_KEY` | |
| `fireworks` | Fireworks | `anthropic-messages`, `openai-completions` | `FIREWORKS_API_KEY` | |
| `github-copilot` | GitHub Copilot | `anthropic-messages`, `openai-completions`, `openai-responses` | `COPILOT_GITHUB_TOKEN` | GitHub Copilot |
| `google` | Google | `google-generative-ai` | `GEMINI_API_KEY` | |
| `google-vertex` | Google Vertex AI | `google-vertex` | `GOOGLE_CLOUD_API_KEY`, or Application Default Credentials | |
| `groq` | Groq | `openai-completions` | `GROQ_API_KEY` | |
| `huggingface` | Hugging Face | `openai-completions` | `HF_TOKEN` | |
| `kimi-coding` | Kimi For Coding | `anthropic-messages` | `KIMI_API_KEY` | Kimi Code (subscription) |
| `meta` | Meta | `openai-responses` | `META_API_KEY` | Meta (Muse subscription) |
| `minimax` | MiniMax | `anthropic-messages` | `MINIMAX_API_KEY` | |
| `minimax-cn` | MiniMax CN | `anthropic-messages` | `MINIMAX_CN_API_KEY` | |
| `mistral` | Mistral | `mistral-conversations` | `MISTRAL_API_KEY` | |
| `moonshotai` | Moonshot AI | `openai-completions` | `MOONSHOT_API_KEY` | |
| `moonshotai-cn` | Moonshot AI CN | `openai-completions` | `MOONSHOT_API_KEY` | |
| `nvidia` | NVIDIA | `openai-completions` | `NVIDIA_API_KEY` | |
| `openai` | OpenAI | `openai-responses`, `openai-decisions` | `OPENAI_API_KEY` | OpenAI (ChatGPT subscription) |
| `openai-codex` | OpenAI Codex (legacy) | `openai-codex-responses` | none | OpenAI (ChatGPT Plus/Pro), the only method |
| `opencode` | OpenCode Zen | `anthropic-messages`, `google-generative-ai`, `openai-completions`, `openai-responses`, `typesafe-system-one` | `OPENCODE_API_KEY` | |
| `opencode-go` | OpenCode Go | `anthropic-messages`, `openai-completions`, `openai-responses` | `OPENCODE_API_KEY` | |
| `openrouter` | OpenRouter | `anthropic-messages`, `openai-completions`, `openrouter-images`, `typesafe-system-one` | `OPENROUTER_API_KEY` | OpenRouter OAuth |
| `qwen-token-plan` | Qwen Token Plan | `openai-completions` | `QWEN_TOKEN_PLAN_API_KEY` | |
| `qwen-token-plan-cn` | Qwen Token Plan CN | `openai-completions` | `QWEN_TOKEN_PLAN_CN_API_KEY` | |
| `qwen-token-plan-individual` | Qwen Token Plan Individual | `openai-completions` | `QWEN_TOKEN_PLAN_API_KEY` | |
| `radius` | Radius | `pi-messages` | `RADIUS_API_KEY` | Radius |
| `together` | Together | `openai-completions` | `TOGETHER_API_KEY` | |
| `typesafe` | TypeSafe | `typesafe-system-one` | `TYPESAFE_API_KEY` | |
| `vercel-ai-gateway` | Vercel AI Gateway | `anthropic-messages`, `typesafe-system-one` | `AI_GATEWAY_API_KEY` | |
| `xai` | xAI | `openai-responses` | `XAI_API_KEY` | xAI (Grok/X subscription) |
| `xiaomi` | Xiaomi | `openai-completions` | `XIAOMI_API_KEY` | |
| `xiaomi-token-plan-ams` | Xiaomi Token Plan AMS | `openai-completions` | `XIAOMI_TOKEN_PLAN_AMS_API_KEY` | |
| `xiaomi-token-plan-cn` | Xiaomi Token Plan CN | `openai-completions` | `XIAOMI_TOKEN_PLAN_CN_API_KEY` | |
| `xiaomi-token-plan-sgp` | Xiaomi Token Plan SGP | `openai-completions` | `XIAOMI_TOKEN_PLAN_SGP_API_KEY` | |
| `zai` | Z.AI | `openai-completions` | `ZAI_API_KEY` | |
| `zai-coding-cn` | Z.AI Coding CN | `openai-completions` | `ZAI_CODING_CN_API_KEY` | |

The `llama.cpp` provider comes from a [built-in extension](/docs/reference/configuration/#built-in-extensions), not this list. Extensions and `models.json` can add more providers.

## Chat APIs

Each model names the API it speaks. These are the chat APIs, all ported from Pi:

| API | Used by |
| --- | --- |
| `anthropic-messages` | Anthropic's Messages API, and providers that offer it, such as MiniMax, Kimi and GitHub Copilot. |
| `openai-completions` | OpenAI Chat Completions and the many compatible endpoints. |
| `openai-responses` | OpenAI's Responses API, also used by xAI, Meta and gateways. |
| `azure-openai-responses` | The Responses API on Azure OpenAI. |
| `openai-codex-responses` | The ChatGPT backend behind OpenAI Codex. See [OpenAI Codex transport](#openai-codex-transport). |
| `google-generative-ai` | The Gemini API. |
| `google-vertex` | Gemini on Google Vertex AI. |
| `mistral-conversations` | Mistral's Conversations API. |
| `bedrock-converse-stream` | Amazon Bedrock's ConverseStream API. |
| `pi-messages` | Radius. |

The other APIs in the table above aren't chat APIs: `openai-decisions`, `typesafe-system-one` and `cloudflare-workers-ai-system-one` are classifier APIs, and `openrouter-images` generates images. See [Classifier and image models](#classifier-and-image-models).

## Choose a model

- `/model` in the terminal UI, or `Ctrl+P` to cycle.
- `--model sonnet:high`, `--model openai/gpt-4o`: an id, `provider/id` or fuzzy match, with an optional thinking level.
- `--provider openai --model gpt-4o-mini`: search one provider for the model.
- `--models` or the `enabledModels` setting limits which models `Ctrl+P` cycles through.
- `--list-models [search]` prints the models you can use.
- `defaultProvider`, `defaultModel` and `defaultThinkingLevel` in [Settings](/docs/reference/settings/#models-and-thinking) set what a session starts with.

At startup, interactive mode refreshes the model catalogs from pi.dev, as Pi does. `--offline` or `PI_OFFLINE=1` turns that off. `pisharp update --models` refreshes them by hand. Refreshed catalogs are saved in `~/.pi/agent/models-store.json`.

With an OpenRouter key or sign-in, the refresh also asks OpenRouter which chat models that key may use, as Pi 1.2.0 does. The OpenRouter list then shows only those models, with the key's context size, output limit and prices, and is saved for offline use. A `baseUrl` for OpenRouter in `models.json`, such as a regional endpoint, is used for that request.

## Your own endpoints and models

`~/.pi/agent/models.json` adds providers and models, or overrides built-in ones, in Pi's format. A `baseUrl` there also works for built-in providers, for example to send Anthropic requests through a proxy. Add `"$schema": "https://pi.dev/schemas/models.schema.json"` so your editor can check the file.

## Classifier and image models

Pi's classifier APIs (OpenAI decisions, TypeSafe and Cloudflare system-one, llama.cpp) and OpenRouter image generation work. Codemode scripts reach them through `models.classify()` and `models.generateImages()`, and extensions through `ctx.modelRegistry`. TypeScript and C# extensions can register their own classifier and image providers. See [Code mode](/docs/guides/codemode/#models-from-scripts).

## Service tiers

Some providers sell faster or cheaper capacity. `--tier` and `/tier` (experimental, unreleased) choose it for a session. See [Service tiers](/docs/guides/service-tiers/).

## OpenAI Codex transport

As in Pi, Codex requests go over a WebSocket first. If the WebSocket fails before the first event, PiSharp falls back to server-sent events and stays on them for the rest of the session. Request bodies sent over server-sent events are compressed with zstd. The `transport` setting (also in `/settings`) chooses:

| Value | Behaviour |
| --- | --- |
| `auto` (default) | WebSocket. Later turns reuse the open connection and send only the new input. |
| `websocket-cached` | The same as `auto`. |
| `websocket` | WebSocket, sending the whole request every turn. |
| `sse` | Server-sent events only. |

The WebSocket connection gets 15 seconds to open. PiSharp doesn't read Pi's `websocketConnectTimeoutMs` setting yet.

## Differences from Pi

- Requests to Anthropic, Azure, Bedrock, Mistral and OpenAI-compatible endpoints carry a `PiSharp` user agent instead of the SDK's.
- PiSharp's zstd frames are valid zstd but not byte for byte what Node's zstd writes.

The [Parity](/parity/) page lists the smaller differences.
