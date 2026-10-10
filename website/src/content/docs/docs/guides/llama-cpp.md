---
title: Local models with llama.cpp
description: Run local GGUF models through a llama.cpp router server, load and unload them with /llama, and use them as classifiers.
---

PiSharp has Pi's built-in llama.cpp provider. It talks to a [llama.cpp](https://github.com/ggml-org/llama.cpp) router server, which finds the GGUF models in a folder and loads or unloads them on demand. Pi's [llama.cpp guide](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/llama-cpp.md) applies unchanged, and ships with PiSharp as `docs/llama-cpp.md` next to the tool.

## Start the router

Start `llama-server` without `--model` or `-m`, so it runs in router mode:

```sh
llama-server --models-dir ~/models --no-models-autoload --jinja --host 127.0.0.1 --port 8080 -ngl 999 -c 32768
```

`--models-dir` is where your GGUF files are. Put multimodal and multi-shard models in their own subfolders. `--jinja` turns on chat templates and tool calling.

## Connect PiSharp

In the terminal UI, run `/login llama.cpp` and enter the router's URL (the default is `http://127.0.0.1:8080`) and an API key if the server needs one. The URL and key are saved in `~/.pi/agent/auth.json`, which Pi uses too.

The environment variables `LLAMA_BASE_URL` and `LLAMA_API_KEY` do the same without `/login`.

## Load models with /llama

`/llama` shows the router's models:

- Select an unloaded model to load it, or a loaded one to unload it.
- **Download model…** searches Hugging Face and asks the router to download a repository and quantization. `owner/repository[:quant]` works too.
- Escape during a load or download asks whether to cancel it.

Loaded and sleeping models appear in `/model`. With `--no-models-autoload`, load a model with `/llama` before you select it. PiSharp never unloads models without asking and never deletes model files.

Hugging Face search uses `HF_TOKEN`, or the token files Pi reads (`$HF_TOKEN_PATH`, `$HF_HOME/token`, `~/.cache/huggingface/token`). The router does the download, so it also needs `HF_TOKEN` for gated repositories.

`/llama` needs the terminal UI. Elsewhere, such as from an RPC client, it only warns that it's available in interactive mode, as in Pi.

## Classifier models

The router's models are also classifier models, as in Pi. Decision models (such as Julia-1 or OpenJev) answer through llama.cpp's `/v1/systemone` endpoint and appear only as classifiers. Chat models are listed as classifiers too, answering from next-token probabilities. Codemode scripts reach them with `models.classify()`, and extensions with `ctx.modelRegistry.classify()`.

## Turn it off

llama.cpp is one of Pi's built-in extensions. To remove the provider and `/llama`, add `"-builtin:llama.cpp"` to `extensions` in `settings.json`, or turn it off under **Built-in** in `pisharp config`. See [Built-in extensions](/docs/reference/configuration/#built-in-extensions).
