---
title: Providers and models
description: Sign in, choose a model, and add your own endpoints.
---

PiSharp has every provider and chat API that Pi v1.1.0 has, with Pi's model catalogs. Pi's [Providers](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/providers.md) and [Models](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/models.md) pages apply.

## Sign in

Type `/login` in the terminal UI and pick a provider. Every built-in provider takes an API key. These also offer a browser or device sign-in: Anthropic, OpenAI (ChatGPT), OpenAI Codex, GitHub Copilot, OpenRouter, xAI, Kimi, Meta and Radius. `/logout` removes a sign-in.

Sign-ins are saved in `~/.pi/agent/auth.json`, which Pi uses too.

You can also set the provider's environment variable, such as `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` or `GEMINI_API_KEY`. `pisharp --help` lists them all. `--api-key` sets a key for one run.

Some providers need more than a key:

- **Amazon Bedrock** uses the AWS credential chain: `AWS_PROFILE`, access keys, SSO, `credential_process` or a Bedrock API key in `AWS_BEARER_TOKEN_BEDROCK`. Set `AWS_REGION`.
- **Google Vertex** takes an API key or Application Default Credentials (user, service account, impersonated service account and workload identity federation).
- **Cloudflare** needs `CLOUDFLARE_ACCOUNT_ID`, and `CLOUDFLARE_GATEWAY_ID` for AI Gateway.
- **Azure OpenAI** reads the `AZURE_OPENAI_*` variables. `AZURE_OPENAI_BASE_URL` wins; otherwise `AZURE_OPENAI_RESOURCE_NAME` becomes `https://<name>.openai.azure.com/openai/v1`. As in Pi, the name is taken as given and read as a URL, so names with dots or upper case work.
- **llama.cpp** connects to a local router server. See [Local models with llama.cpp](/docs/guides/llama-cpp/).

## Choose a model

- `/model` in the terminal UI, or `Ctrl+P` to cycle.
- `--model sonnet:high`, `--model openai/gpt-4o`: an id, `provider/id` or fuzzy match, with an optional thinking level.
- `--models` or the `enabledModels` setting limits which models `Ctrl+P` cycles through.
- `--list-models [search]` prints the models you can use.

At startup, interactive mode refreshes the model catalogs from pi.dev, as Pi does. `--offline` or `PI_OFFLINE=1` turns that off. `pisharp update --models` refreshes them by hand.

## Your own endpoints and models

`~/.pi/agent/models.json` adds providers and models, or overrides built-in ones, in Pi's format. A `baseUrl` there also works for built-in providers, for example to send Anthropic requests through a proxy.

## Classifier and image models

Pi's classifier APIs (OpenAI decisions, TypeSafe and Cloudflare system-one, llama.cpp) and OpenRouter image generation work. Codemode scripts reach them through `models.classify()` and `models.generateImages()`, and extensions through `ctx.modelRegistry`. TypeScript and C# extensions can register their own classifier and image providers.

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
