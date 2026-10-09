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
- **Azure OpenAI** reads the `AZURE_OPENAI_*` variables.

## Choose a model

- `/model` in the terminal UI, or `Ctrl+P` to cycle.
- `--model sonnet:high`, `--model openai/gpt-4o`: an id, `provider/id` or fuzzy match, with an optional thinking level.
- `--models` or the `enabledModels` setting limits which models `Ctrl+P` cycles through.
- `--list-models [search]` prints the models you can use.

At startup, interactive mode refreshes the model catalogs from pi.dev, as Pi does. `--offline` or `PI_OFFLINE=1` turns that off. `pisharp update --models` refreshes them by hand.

## Your own endpoints and models

`~/.pi/agent/models.json` adds providers and models, or overrides built-in ones, in Pi's format. A `baseUrl` there also works for built-in providers, for example to send Anthropic requests through a proxy.

## Classifier and image models

Pi's classifier APIs (OpenAI decisions, TypeSafe and Cloudflare system-one, llama.cpp) and OpenRouter image generation work. Codemode scripts reach them through `models.classify()` and `models.generateImages()`, and extensions through `ctx.modelRegistry`.

## Differences from Pi

- Requests to Anthropic, Azure, Bedrock, Mistral and OpenAI-compatible endpoints carry a `PiSharp` user agent instead of the SDK's.
- OpenAI Codex uses server-sent events only. Pi can also use a WebSocket, and compresses Codex requests with zstd; PiSharp sends them uncompressed.
- PiSharp doesn't discover models on a llama.cpp server. Add them in `models.json`.

The [Parity](/parity/) page lists the smaller differences.
