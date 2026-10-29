---
title: Settings
description: Every settings.json key PiSharp reads, its type, default and effect.
---

PiSharp reads Pi v1.2.0's `settings.json` keys, with Pi's defaults, plus three keys of its own. This page lists them all, grouped by area. Pi's [Settings](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/settings.md) page describes the same keys.

PiSharp ships Pi's settings schema unchanged. Add a `$schema` key so your editor can check the file:

```json
{
  "$schema": "https://pi.dev/schemas/settings.schema.json",
  "defaultModel": "claude-sonnet-4-5",
  "compaction": { "reserveTokens": 32768 }
}
```

The schema allows keys it doesn't list, so extensions can keep their own settings here.

## Files and scope

| Scope | File | Loaded |
| --- | --- | --- |
| Global | `~/.pi/agent/settings.json` (`<agentDir>/settings.json`) | Always. |
| Project | `.pi/settings.json` in the working folder | Only when the project is [trusted](/docs/reference/configuration/#project-trust). |

PiSharp merges the project file over the global one:

- Objects merge key by key, at every depth. A project `compaction: { "enabled": false }` keeps your global `reserveTokens`.
- Every other value, arrays included, replaces the global one.
- One exception: a project `defaultTools` list made only of `+name` and `-name` entries is added to the global list instead of replacing it.

A few keys are read from the global file only: `defaultProjectTrust`, `cacheWarming`, `httpProxy` and `deviceId`.

`/settings`, `/tier ... default` and the other setters write the global file, holding a `settings.json.lock` folder while they write, as Pi does. Package commands with `-l` write the project file, which needs a trusted project.

Before merging, PiSharp migrates old keys in each file, as Pi does: `queueMode` becomes `steeringMode`, `websockets` becomes `transport`, the old `skills` object becomes a `skills` array plus `enableSkillCommands`, and `retry.maxDelayMs` becomes `retry.provider.maxRetryDelayMs`.

## Models and thinking

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `defaultProvider` | string | none | Provider of the default model. |
| `defaultModel` | string | none | Model to start with when `--model` isn't given. |
| `defaultThinkingLevel` | `off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max` | `medium` | Thinking level to start with. In the `/thinking` selector, `Ctrl+S` saves the chosen level here. |
| `modelThinkingLevels` | object | none | Thinking level per model, keyed by `"provider/modelId"`. Wins over `defaultThinkingLevel`. |
| `enabledModels` | string array | none | Model patterns for `Ctrl+P` cycling, in the `--models` format. `/scoped-models` writes it. |
| `thinkingBudgets` | object: `minimal`, `low`, `medium`, `high` (numbers) | none | Pi's custom token budgets per thinking level. PiSharp parses it but ignores it. |
| `serviceTier` | string | none | PiSharp only (experimental, unreleased): the default [service tier](/docs/guides/service-tiers/). See [PiSharp keys](#pisharp-keys). |

## Sessions, queue and compaction

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `sessionDir` | string | none | Session folder, in the `--session-dir` format. `--session-dir` and `PI_CODING_AGENT_SESSION_DIR` win over it. |
| `steeringMode` | `all`, `one-at-a-time` | `one-at-a-time` | How messages you send while the agent works are delivered: all at once, or one per turn. |
| `followUpMode` | `all`, `one-at-a-time` | `one-at-a-time` | The same for follow-up messages, which wait until the agent stops. |
| `doubleEscapeAction` | `tree`, `fork`, `none` | `tree` | What pressing `Esc` twice in an empty editor opens: `/tree`, the `/fork` picker, or nothing. |
| `treeFilterMode` | `default`, `no-tools`, `user-only`, `labeled-only`, `all` | `default` | The filter `/tree` opens with. |
| `compaction.enabled` | boolean | `true` | Automatic compaction. `/settings` and the RPC `set_auto_compaction` command change it. |
| `compaction.reserveTokens` | integer | `16384` | Compaction starts when the context passes the context window minus this many tokens. |
| `compaction.keepRecentTokens` | integer | `20000` | Recent tokens kept verbatim after compaction. |
| `compaction.modelOverrides` | object | none | `reserveTokens` and `keepRecentTokens` for single models, keyed by exact `"provider/modelId"`. |
| `branchSummary.reserveTokens` | number | `16384` | Tokens reserved for the prompt and answer when summarizing a branch. |
| `branchSummary.skipPrompt` | boolean | `false` | Skips the "Summarize branch?" question in `/tree` and makes no summary. |

See [Sessions](/docs/guides/sessions/) for compaction and branch summaries.

## Retry and network

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `retry.enabled` | boolean | `true` | Retries failed provider calls that look transient, such as overloads, rate limits and network errors. |
| `retry.maxRetries` | number | `3` | Retry attempts. |
| `retry.baseDelayMs` | number | `2000` | Backoff base: 2 s, 4 s, 8 s and so on. |
| `retry.maxAgentDelayMs` | number | `60000` | Upper limit of one backoff delay. |
| `retry.provider.timeoutMs` | number | none | Request timeout of the provider client. |
| `retry.provider.maxRetries` | number | none | Retries inside the provider client. |
| `retry.provider.maxRetryDelayMs` | number | `60000` | Longest delay a server may ask for (such as `Retry-After`) before the call fails. |
| `retry.maxDelayMs` | number | none | Legacy. Migrated to `retry.provider.maxRetryDelayMs`. |
| `transport` | `auto`, `sse`, `websocket`, `websocket-cached` | `auto` | How OpenAI Codex requests travel. See [OpenAI Codex transport](/docs/guides/providers/#openai-codex-transport). |
| `httpProxy` | string | none | Global only. Proxy URL. PiSharp sets `HTTP_PROXY` and `HTTPS_PROXY` from it when they're unset. |
| `httpIdleTimeoutMs` | number, or `"disabled"` | `300000` | How long a provider request may wait for headers and for each body chunk. `0` or `"disabled"` turns it off. An invalid value is an error at startup. |
| `websocketConnectTimeoutMs` | number, or `"disabled"` | none | Pi's WebSocket connect timeout. PiSharp parses it but ignores it: the Codex WebSocket gets a fixed 15 seconds. |
| `cacheWarming` | `off`, `streaming`, `idle` | `streaming` | Keeps the provider's prompt cache warm by resending the last request with a one-token answer. `streaming` warms during an agent run, `idle` also between runs (up to 30 minutes), when that is expected to save money. Global only, because each refresh costs money. |

## Tools and shell

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `defaultTools` | string array | `read`, `bash`, `edit`, `write` | The tools a session starts with. Plain names replace the inherited list; `+name` and `-name` add or remove. See [Selecting tools](/docs/reference/tools/#selecting-tools). |
| `toolPolicy` | `pi`, `explicit` | `pi` | PiSharp only. How far the built-in tools reach. See [PiSharp keys](#pisharp-keys). |
| `shellPath` | string | none | The shell `bash` runs, such as Cygwin's on Windows. A leading `~` expands. |
| `shellCommandPrefix` | string | none | Text put on its own line before every bash command, for the model's `bash` and your `!` commands, for example to load aliases. |
| `images.autoResize` | boolean | `true` | Resizes images to at most 2000x2000 before they go to the model. |
| `images.blockImages` | boolean | `false` | Sends no images to any provider. |
| `codemode.mode` | `on`, `only` | `on` | How code mode presents the other tools. `on` keeps them declared; `only` hides them and lists them in the `codemode` description. See [Code mode](/docs/guides/codemode/). |
| `codemode.inlineBudget` | number | `3000` | Estimated tokens for tool declarations inside the `codemode` description. |

## Resources and packages

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `packages` | array of strings or objects | none | npm and git package sources. An object has `source` and can filter what loads with `extensions`, `skills`, `prompts` and `themes`. `pisharp install` writes it. |
| `extensions` | string array | none | Extension files or folders. `-builtin:<name>` turns off a [built-in extension](/docs/reference/configuration/#built-in-extensions); in project settings `+builtin:<name>` turns it back on. |
| `skills` | string array | none | Skill files or folders. The old object form is migrated. |
| `prompts` | string array | none | Prompt template files or folders. |
| `themes` | string array | none | Theme files or folders. |
| `enableSkillCommands` | boolean | `true` | Registers each skill as a `/skill:name` command. |
| `npmCommand` | string array | none | The npm command in argv form, such as `["mise", "exec", "node@20", "--", "npm"]`, for package lookup and installs. |

## Terminal UI

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `theme` | string | none (the `system` theme) | Theme name. `--use-theme` wins for one run. |
| `tuiMode` | `fullscreen`, `regular` | `fullscreen` | Fullscreen uses the alternate screen; regular writes into the scrollback. `--tui-mode` wins. |
| `fullscreenExitOutput` | `transcript`, `resume-hint` | `transcript` | What fullscreen mode leaves on screen when you quit. |
| `fullscreenScrollbar` | `auto`, `always`, `hidden` | `auto` | The fullscreen scrollbar. |
| `fullscreenCopyOnSelect` | boolean | `true` | Copies a mouse selection in fullscreen mode. |
| `fullscreenWheelScrollLines` | number 1 to 100, or `auto` | `auto` | Lines per mouse wheel step in fullscreen mode. |
| `quietStartup` | boolean or `"header"` | `false` | `true` hides the startup output; `"header"` keeps only the header. `--verbose` wins. |
| `collapseChangelog` | boolean | `false` | Shows a short changelog after an update. `/changelog` shows the whole one. |
| `lastChangelogVersion` | string | none | Written by PiSharp: the last version whose changelog you saw. |
| `hideThinkingBlock` | boolean | `false` | Hides thinking blocks. `Ctrl+T` toggles them. |
| `showCacheMissNotices` | boolean | `false` | Shows prompt cache cost and provider recovery notices. |
| `externalEditor` | string | none | Command `Ctrl+G` opens. Wins over `VISUAL` and `EDITOR`. Without any of them PiSharp uses `notepad` on Windows and `nano` elsewhere. |
| `editorPaddingX` | number | `0` | Horizontal padding of the editor, 0 to 3. |
| `outputPad` | `0`, `1` | `1` | Horizontal padding of the transcript. |
| `autocompleteMaxVisible` | number | `5` | Rows in the autocomplete list, 3 to 20. |
| `showHardwareCursor` | boolean | `false` | Shows the terminal cursor. `PI_HARDWARE_CURSOR=1` does the same when the key is absent. |
| `markdown.codeBlockIndent` | string | two spaces | Indent of code blocks. |
| `markdown.mermaid` | `off`, `final`, `streaming` | `streaming` | Draws Mermaid diagrams never, once the message is done, or while it streams. |
| `terminal.showImages` | boolean | `true` | Shows images inline when the terminal can. |
| `terminal.imageWidthCells` | number | `60` | Width of inline images, in cells. |
| `terminal.images` | `kitty`, `iterm2`, `auto`, boolean | auto | Image protocol. `kitty` or `iterm2` forces one and `false` turns images off. `auto` or `true` detects the terminal, and then `PI_IMAGE_PROTOCOL` wins over detection. |
| `terminal.hyperlinks` | boolean or `auto` | auto | Clickable links. `true` or `false` forces them; with `auto`, `PI_HYPERLINKS` wins over detection. |
| `terminal.trueColor` | boolean or `auto` | auto | 24-bit color. `true` or `false` forces it; with `auto`, `PI_TRUE_COLOR` wins over detection. |
| `terminal.clearOnShrink` | boolean | `false` | Clears empty rows when content shrinks. `PI_CLEAR_ON_SHRINK=1` does the same when the key is absent. |
| `terminal.showTerminalProgress` | boolean | `false` | Sends OSC 9;4 progress to the terminal while the agent works. |
| `warnings.anthropicExtraUsage` | boolean | `true` | Warns once when an Anthropic subscription sign-in is in use, since third-party use is billed as extra usage. |

## Trust, telemetry and identity

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `defaultProjectTrust` | `ask`, `always`, `never` | `ask` | Global only. What happens in a project you haven't decided on. Print, JSON and RPC modes can't ask, so `ask` counts as no there. |
| `enableInstallTelemetry` | boolean | `true` | Shown and saved by `/settings`, but it has no effect: PiSharp sends no install telemetry. |
| `enableAnalytics` | boolean | `false` | Pi's analytics opt-in. PiSharp ignores it. |
| `trackingId` | string | none | Pi's analytics id. PiSharp ignores it and removes it from `/bug` reports. |
| `deviceId` | string | none | Global only. A stable install id, created when a sign-in first needs one. Removed from `/bug` reports. |

## PiSharp keys

Pi doesn't have these keys. Pi ignores them, so a shared `settings.json` keeps working in both.

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `toolPolicy` | `pi`, `explicit` | `pi` | `pi` lets the file tools take any path and `bash` run any command, as in Pi. `explicit` limits them to granted paths and commands; the main entry passes no grants, so file tools refuse every path and `bash`, `grep` and `find` are left out. `--tool-policy` wins. See [Tool policy](/docs/reference/configuration/#tool-policy). |
| `serviceTier` | `auto`, `normal`, `economy`, `priority`, `fast`, `ultrafast` | none | The session's service tier when `--tier` isn't given. `/tier <tier> default` writes it. Experimental and unreleased; see below. |
| `media` | object | none | Defaults for the media tools of `pisharp durable`. Experimental and unreleased; see below. |

:::caution[Experimental and unreleased]
`serviceTier` and `media` are on PiSharp's `main` branch but not in any release yet, and need a CLI built from source. They may change or be removed without notice. See [Service tiers](/docs/guides/service-tiers/) and [Media](/docs/guides/media/).
:::

`media` holds these keys:

| Key | Default | Meaning |
| --- | --- | --- |
| `media.enabled` | `true` | `false` leaves the media tools out. |
| `media.outputDir` | `".pi/media"` | Where generated files go. |
| `media.deferSpecializedTools` | `true` | Defers the less common tools (speech, transcription, music, rerank and decisions), so the model finds them with `tool_search` instead of seeing them in every request. |
| `media.<kind>.model` | none | Default model, as `"provider/id"`. |
| `media.<kind>.options` | none | Default tool arguments. |

`<kind>` is `image`, `video`, `speech`, `transcription`, `music`, `embedding`, `rerank` or `decision`. The `set_media_defaults` tool writes these entries. See [Media tools](/docs/guides/media/#settings) for what each kind does.

## Legacy keys

| Key | Replaced by |
| --- | --- |
| `queueMode` | `steeringMode` |
| `websockets` | `transport` (`true` becomes `websocket`, `false` becomes `sse`) |
| `retry.maxDelayMs` | `retry.provider.maxRetryDelayMs` |
| `skills` as an object | `skills` as an array, and `enableSkillCommands` |

PiSharp migrates them when it reads the file, as Pi does.
