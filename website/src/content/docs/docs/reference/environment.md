---
title: Environment variables
description: Every environment variable PiSharp reads.
---

PiSharp reads Pi v1.2.0's environment variables under the same names, plus a few `PISHARP_*` variables of its own. This page lists them by area. `pisharp --help` prints the common ones.

Boolean variables differ in what they accept. Where a row says `1`, only `1` turns the feature on. `PI_OFFLINE` also takes `true` and `yes`.

## Folders

| Variable | Effect | Default |
| --- | --- | --- |
| `PI_CODING_AGENT_DIR` | The agent folder: settings, credentials, sessions and resources. A leading `~` expands. | `~/.pi/agent` |
| `PI_CODING_AGENT_SESSION_DIR` | The session folder. `--session-dir` wins over it; it wins over the `sessionDir` setting. | `<agentDir>/sessions` |
| `PI_PACKAGE_DIR` | The folder PiSharp treats as its install folder, for its bundled docs and changelog. Meant for Nix or Guix store paths. | The tool's folder |
| `PISHARP_PI_RUNTIME_DIR` | Where Pi's own npm packages for TypeScript extensions are installed. PiSharp only. | `<agentDir>/pisharp/pi-runtime/<Pi version>` |

## Startup and network

| Variable | Effect | Default |
| --- | --- | --- |
| `PI_OFFLINE` | `1`, `true` or `yes` turns off startup network use: the model catalog refresh, the version check and the `rg`/`fd` download. `--offline` sets it. | unset |
| `PI_SKIP_VERSION_CHECK` | Any value skips the check for a newer `PiSharp.Cli` on NuGet. `--offline` sets it. | unset |
| `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY` | Proxy for provider requests, read by .NET's HTTP stack. The `httpProxy` setting fills the first two when they're unset. | unset |
| `PI_OAUTH_CALLBACK_HOST` | Host of the local callback server that browser sign-ins redirect to. | `127.0.0.1` |
| `PI_SHARE_VIEWER_URL` | Base URL of the link `/share` prints. The gist id is added after `#`. | `https://pi.dev/session/` |
| `PI_CACHE_RETENTION` | `long` asks for long prompt cache retention where the API supports it (Bedrock, OpenAI Responses, Radius). | short |
| `PI_TELEMETRY` | Accepted and ignored. PiSharp sends no install telemetry. | unset |

## Diagnostics

| Variable | Effect | Default |
| --- | --- | --- |
| `PISHARP_DEBUG` | `1` writes full exceptions to stderr: RPC and session faults and failed extension actions. Use it for bug reports. PiSharp only. | unset |
| `PI_TIMING` | `1` prints startup timings. | unset |
| `PI_STARTUP_BENCHMARK` | Pi's startup benchmark flag. PiSharp only checks the mode: outside interactive mode it is an error. | unset |
| `PI_TUI_WRITE_LOG` | A file that receives every byte written to the terminal. If it names a folder, PiSharp writes `tui-<time>-<pid>.log` there. | unset |
| `PI_TUI_DEBUG_REDRAW` | `1` logs each full redraw of the regular TUI to `pi-tui-debug.log` in the agent folder. | unset |
| `PI_EXPERIMENTAL` | `1` shows an `xp` marker in the footer. | unset |

## Terminal

| Variable | Effect | Default |
| --- | --- | --- |
| `PI_TUI_ESC_TIMEOUT` | Milliseconds to wait after `Esc` for the rest of a key sequence. | `10`, or `100` over SSH |
| `PI_HYPERLINKS` | `1` or `0` forces clickable links on or off, unless `terminal.hyperlinks` is `true` or `false`. | detected |
| `PI_TRUE_COLOR` | `1` or `0` forces 24-bit color on or off, unless `terminal.trueColor` is `true` or `false`. | detected |
| `PI_IMAGE_PROTOCOL` | `kitty`, `iterm2`, or `none`/`0`, unless `terminal.images` names a protocol or is `false`. | detected |
| `PI_CLEAR_ON_SHRINK` | `1` clears empty rows when content shrinks, when the `terminal.clearOnShrink` setting is absent. | unset |
| `PI_HARDWARE_CURSOR` | `1` shows the terminal cursor, when the `showHardwareCursor` setting is absent. | unset |
| `PI_PROGRAM_STATUS` | `1` or `0` forces OSC 7501 program status reports on or off. Otherwise PiSharp asks the terminal. | asks |
| `PI_CLIPBOARD_OSC52` | `1` also copies through OSC 52, for remote sessions that leave no SSH markers, such as a multiplexer attach. | unset |
| `VISUAL`, `EDITOR` | The external editor for `Ctrl+G`, after the `externalEditor` setting. | `notepad` on Windows, `nano` elsewhere |
| `NO_COLOR` | Any value turns off color in PiSharp's own error messages. | unset |
| `COLUMNS`, `LINES` | Terminal size when the terminal doesn't report one. | 80 by 24 |

PiSharp also reads these to detect the terminal, as Pi does, and never changes them: `TERM`, `COLORTERM`, `TERM_PROGRAM`, `TERM_PROGRAM_VERSION`, `TERMINAL_EMULATOR`, `WT_SESSION`, `KITTY_WINDOW_ID`, `ITERM_SESSION_ID`, `WEZTERM_PANE`, `GHOSTTY_RESOURCES_DIR`, `TMUX`, `COLORFGBG` (for the `system` theme), `SSH_CONNECTION`, `SSH_CLIENT`, `SSH_TTY`, `MOSH_CONNECTION`, `WAYLAND_DISPLAY`, `DISPLAY` and `XDG_SESSION_TYPE` (for the clipboard), `TERMUX_VERSION`, `WSL_DISTRO_NAME` and `WSL_INTEROP`. On WSL, PiSharp uses the Windows key bindings.

## Extensions and code mode

| Variable | Effect | Default |
| --- | --- | --- |
| `PISHARP_NODE` | The Node executable for TypeScript and JavaScript extensions. | `node` on `PATH` |
| `PISHARP_NODE_BRIDGE` | A folder holding `host.mjs`, to use instead of the bundled Node bridge host. | the bundled `node-bridge/pi-host` |
| `PISHARP_CODEMODE_WORKER_STACK_BYTES` | Stack size of the code mode engine thread, in bytes. Values under 1 MiB are ignored. | 256 MiB (64 MiB in a 32-bit process) |

See [Node bridge](/docs/extensions/node-bridge/) and [Code mode](/docs/guides/codemode/#limits).

## Variables PiSharp sets

PiSharp sets these for the programs it starts, as Pi does:

| Variable | Value |
| --- | --- |
| `PI_CODING_AGENT` | `true`, for every child process. |
| `AI_AGENT` | `pi`, for every child process. |
| `PI_SESSION_ID`, `PI_SESSION_FILE` | The session's id and file, for `bash` commands. |
| `PI_PROVIDER`, `PI_MODEL`, `PI_REASONING_LEVEL` | The current model and thinking level, for `bash` commands. |

`bash` commands also find `<agentDir>/bin` first on `PATH`.

## Provider keys

Each provider reads its key from these variables. A key saved with `/login`, or one in `models.json`, wins over them. See [Providers](/docs/guides/providers/).

| Provider | Variables |
| --- | --- |
| `amazon-bedrock` | `AWS_BEARER_TOKEN_BEDROCK`, `AWS_PROFILE`, `AWS_ACCESS_KEY_ID` and the rest of the AWS chain (below) |
| `ant-ling` | `ANT_LING_API_KEY` |
| `anthropic` | `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_OAUTH_TOKEN`, `ANTHROPIC_API_KEY`, or workload identity (below) |
| `azure` | `AZURE_OPENAI_API_KEY` (and the Azure variables below) |
| `baseten` | `BASETEN_API_KEY` |
| `cerebras` | `CEREBRAS_API_KEY` |
| `cloudflare-ai-gateway` | `CLOUDFLARE_API_KEY`, `CLOUDFLARE_ACCOUNT_ID`, `CLOUDFLARE_GATEWAY_ID` |
| `cloudflare-workers-ai` | `CLOUDFLARE_API_KEY`, `CLOUDFLARE_ACCOUNT_ID` |
| `deepseek` | `DEEPSEEK_API_KEY` |
| `fireworks` | `FIREWORKS_API_KEY` |
| `github-copilot` | `COPILOT_GITHUB_TOKEN` |
| `google` | `GEMINI_API_KEY` |
| `google-vertex` | `GOOGLE_CLOUD_API_KEY`, or Application Default Credentials (below) |
| `groq` | `GROQ_API_KEY` |
| `huggingface` | `HF_TOKEN` |
| `kimi-coding` | `KIMI_API_KEY` |
| `meta` | `META_API_KEY` |
| `minimax` | `MINIMAX_API_KEY` |
| `minimax-cn` | `MINIMAX_CN_API_KEY` |
| `mistral` | `MISTRAL_API_KEY` |
| `moonshotai`, `moonshotai-cn` | `MOONSHOT_API_KEY` |
| `nvidia` | `NVIDIA_API_KEY` |
| `openai` | `OPENAI_API_KEY` |
| `openai-codex` | none: sign in with `/login` |
| `opencode`, `opencode-go` | `OPENCODE_API_KEY` |
| `openrouter` | `OPENROUTER_API_KEY` |
| `qwen-token-plan`, `qwen-token-plan-individual` | `QWEN_TOKEN_PLAN_API_KEY` |
| `qwen-token-plan-cn` | `QWEN_TOKEN_PLAN_CN_API_KEY` |
| `radius` | `RADIUS_API_KEY` |
| `together` | `TOGETHER_API_KEY` |
| `typesafe` | `TYPESAFE_API_KEY` |
| `vercel-ai-gateway` | `AI_GATEWAY_API_KEY` |
| `xai` | `XAI_API_KEY` |
| `xiaomi` | `XIAOMI_API_KEY` |
| `xiaomi-token-plan-ams` | `XIAOMI_TOKEN_PLAN_AMS_API_KEY` |
| `xiaomi-token-plan-cn` | `XIAOMI_TOKEN_PLAN_CN_API_KEY` |
| `xiaomi-token-plan-sgp` | `XIAOMI_TOKEN_PLAN_SGP_API_KEY` |
| `zai` | `ZAI_API_KEY` |
| `zai-coding-cn` | `ZAI_CODING_CN_API_KEY` |
| `llama.cpp` (built-in extension) | `LLAMA_BASE_URL`, `LLAMA_API_KEY`. See [llama.cpp](/docs/guides/llama-cpp/). |

### Anthropic workload identity

Without a key, the `anthropic` provider uses workload identity federation when `ANTHROPIC_FEDERATION_RULE_ID`, `ANTHROPIC_ORGANIZATION_ID` and `ANTHROPIC_IDENTITY_TOKEN_FILE` are all set. `ANTHROPIC_SERVICE_ACCOUNT_ID` and `ANTHROPIC_WORKSPACE_ID` are optional; set the workspace when the rule covers more than one.

### Azure OpenAI

| Variable | Effect | Default |
| --- | --- | --- |
| `AZURE_OPENAI_API_KEY` | The key. | none |
| `AZURE_OPENAI_BASE_URL` | The endpoint, such as `https://<resource>.openai.azure.com`. Wins over the resource name. | none |
| `AZURE_OPENAI_RESOURCE_NAME` | Becomes `https://<name>.openai.azure.com/openai/v1`. | none |
| `AZURE_OPENAI_API_VERSION` | API version. | `v1` |
| `AZURE_OPENAI_DEPLOYMENT_NAME_MAP` | `model=deployment` pairs, comma-separated. | none |

### Amazon Bedrock

Bedrock follows the AWS credential chain, as the AWS SDK does:

| Variable | Effect |
| --- | --- |
| `AWS_BEARER_TOKEN_BEDROCK` | A Bedrock API key, sent as a bearer token. |
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `AWS_CREDENTIAL_EXPIRATION` | Static credentials. |
| `AWS_PROFILE`, `AWS_CONFIG_FILE`, `AWS_SHARED_CREDENTIALS_FILE` | A profile from the AWS config files, including SSO and `credential_process`. |
| `AWS_ROLE_ARN`, `AWS_ROLE_SESSION_NAME`, `AWS_WEB_IDENTITY_TOKEN_FILE` | Web identity. |
| `AWS_CONTAINER_CREDENTIALS_RELATIVE_URI`, `AWS_CONTAINER_CREDENTIALS_FULL_URI`, `AWS_CONTAINER_AUTHORIZATION_TOKEN`, `AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE` | Container credentials. |
| `AWS_EC2_METADATA_DISABLED`, `AWS_EC2_METADATA_SERVICE_ENDPOINT` | The EC2 instance metadata service. |
| `AWS_REGION`, `AWS_DEFAULT_REGION` | The region. |
| `AWS_ENDPOINT_URL_BEDROCK_RUNTIME`, `AWS_ENDPOINT_URL`, `AWS_USE_FIPS_ENDPOINT` | The endpoint. |
| `AWS_MAX_ATTEMPTS` | Attempts per request in the SDK's standard retry mode (default 3). |
| `AWS_BEDROCK_FORCE_CACHE` | `1` turns on prompt caching for models whose name doesn't contain `claude`. |
| `AWS_BEDROCK_SKIP_AUTH` | `1` uses dummy credentials instead of real ones, for a proxy that adds its own. |

### Google Vertex

| Variable | Effect |
| --- | --- |
| `GOOGLE_CLOUD_API_KEY` | An API key. Without it, Vertex uses Application Default Credentials. |
| `GOOGLE_APPLICATION_CREDENTIALS` | A credentials file. Otherwise PiSharp reads gcloud's `application_default_credentials.json` in `%APPDATA%\gcloud` on Windows, else in `CLOUDSDK_CONFIG` or `~/.config/gcloud`. |
| `GOOGLE_CLOUD_PROJECT`, `GCLOUD_PROJECT` | The project. |
| `GOOGLE_CLOUD_LOCATION` | The location. |
| `GOOGLE_EXTERNAL_ACCOUNT_ALLOW_EXECUTABLES` | `1` allows executable-sourced credentials in workload identity federation, as Google's libraries require. |
| `GOOGLE_API_CERTIFICATE_CONFIG` | Certificate config for certificate-based workload identity federation (mTLS). |

Google's other `GOOGLE_EXTERNAL_ACCOUNT_*` variables are read for executable-sourced credentials, as Google's libraries define them.
