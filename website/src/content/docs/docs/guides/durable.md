---
title: Pi Durable
description: PiSharp's experimental port of Pi's durable agent harness, whose sessions survive a crash and resume where they stopped. Ships as experimental in PiSharp 1.2.0.1.
sidebar:
  badge:
    text: Experimental
    variant: caution
---

:::caution[Experimental]
Pi Durable ships as experimental in PiSharp 1.2.0.1: the `pisharp durable` command and the `PiSharp.Durable`, `PiSharp.Durable.Tools` and `PiSharp.Durable.Sqlite` packages. Its command, session format and library API may change or be removed in any release, without notice or a deprecation period.
:::

Pi includes `pi-durable`, an experimental agent harness that saves every step of a conversation before it shows it. PiSharp ports it as a set of .NET libraries and as an interactive mode, `pisharp durable`.

## What durable means

- Every model turn, streamed partial answer, tool call and queued message is committed to storage before it appears.
- If the process dies mid-turn, reopening the session finishes that turn. The turn still ends with exactly one answer, and queued messages are not duplicated.
- A tool that was interrupted after it started is not run again unless it is declared replay-safe. The model gets an "interrupted" result instead. A replay-safe tool runs again exactly once.
- Subagents run as child conversations owned by the task that called them. A child outlives the call, so you can switch to it and keep talking.

PiSharp's tests check these guarantees by killing a real process at checkpoints in every phase of a turn, then reopening its storage the way `pisharp durable --continue` does.

## Try `pisharp durable`

`pisharp durable` is part of the PiSharp CLI from 1.2.0.1. Install or update the tool, then run it in a project folder:

```sh
dotnet tool install -g PiSharp.Cli    # or: dotnet tool update -g PiSharp.Cli
pisharp durable                        # start a new durable session
pisharp durable --continue             # reopen the newest durable session for this folder
```

| Option | What it does |
| --- | --- |
| `--continue`, `-c` | Reopens the newest session for the current folder. Without it, each run starts a new session. |
| `--tier <tier>`, `--service-tier <tier>` | (experimental, unreleased) Sets the main conversation's [service tier](/docs/guides/service-tiers/): `auto`, `normal`, `economy`, `priority`, `fast` or `ultrafast`. It applies when the run creates the session; a session that `--continue` reopens keeps its stored tier, which you change with `/tier`. |

Any other argument fails with `Unknown argument: <argument>`, a tier name it doesn't know fails with `Invalid service tier`, and `--tier` without a value fails with `--tier needs a tier`. Each exits with 1. The command is listed in `pisharp --help` as experimental, though its help line shows only `[--continue]`, and it prints a one-line experimental notice when it starts.

`--tier`, `/tier`, the `subagent` tool's `tier` argument and the media tools are on PiSharp's `main` branch but not in any release yet. To try them, run a CLI built from source (see [Run the CLI from source](/docs/contribute/build/#run-the-cli-from-source)).

It reads the same settings, credentials and models as `pisharp`, so providers you have signed in to work here too. A new session starts on your `defaultProvider`, `defaultModel` and `defaultThinkingLevel`.

### Tools

| Tool | What it does |
| --- | --- |
| `read`, `write`, `edit`, `bash` | The coding tools. `read` returns images too. A call that a crash interrupts is not run again: the model gets an "interrupted" result. |
| `subagent` | Hands a self-contained task to a child conversation with the same tools, except `subagent`, and returns its answer. Arguments: `task` (required) and `tier` (experimental, unreleased), the child's service tier; without it the child uses this conversation's. Replay-safe: after a crash, the rerun finds the child and the message it already sent. |
| Media tools | (experimental, unreleased) `generate_image`, `generate_video`, `embed`, `list_media_models` and `set_media_defaults`, plus `generate_speech`, `transcribe_audio`, `generate_music`, `rerank` and `decide`, which are deferred. Installed only when the `media.enabled` setting isn't `false` and a provider with a media model is configured. See [Media tools](/docs/guides/media/). |
| `tool_search` | Finds deferred tools and loads them. Offered only while an installed tool is deferred, which today means the media tools. |

### In a session

| Input | What it does |
| --- | --- |
| A message | Sends it. While the agent is working, it steers the current turn. |
| The follow-up key | Queues the message until the agent finishes. |
| `/model` | Picks the model. |
| `/tier [tier]` | (experimental, unreleased) Without a tier, shows the shown conversation's service tier and the choices. With one, sets it for that conversation from its next request; `/tier none` (or `clear`) removes it. The tier is saved in the session. See [Service tiers](/docs/guides/service-tiers/#in-pisharp-durable). |
| `/tasks` | Shows or hides running tasks. |
| `/agents` | Switches between the main conversation and its subagents. |
| `/compact [instructions]` | Compacts the conversation. |
| Escape | Aborts the turn. |
| Ctrl+D | Exits. |

Your [key bindings](/docs/guides/interactive/#key-bindings) apply, including the keys for the model selector and the thinking level. The footer shows `tier:<tier> (/tier)` while the shown conversation has a tier.

### Settings it reads

| Setting | Used for |
| --- | --- |
| `defaultProvider`, `defaultModel`, `defaultThinkingLevel` | The model of a new session. |
| `retry.enabled`, `retry.maxRetries`, `retry.baseDelayMs`, `retry.maxAgentDelayMs` | Retries of a failed model turn. Defaults: on, 3, 2000, 60000. |
| `retry.provider.timeoutMs`, `retry.provider.maxRetries`, `retry.provider.maxRetryDelayMs`, `httpIdleTimeoutMs` | Request timeouts and retries inside one request. The timeout falls back to `httpIdleTimeoutMs`. |
| `compaction.enabled`, `compaction.reserveTokens`, `compaction.keepRecentTokens` | Automatic compaction. Defaults: on, 16384, 20000. |
| `steeringMode`, `followUpMode` | `all` places every queued message at once; anything else, one at a time. |
| `serviceTier` | (experimental, unreleased) The tier of a new session when you don't pass `--tier`. |
| `media` | (experimental, unreleased) The media tools. See [Media tools](/docs/guides/media/#settings). |

See [Settings](/docs/reference/settings/) for where settings files live.

### Where sessions are stored

Each session is one SQLite database:

```text
~/.pi/agent/experimental/durable-sessions/<folder hash>/<timestamp>-<id>/session.sqlite
```

A session is locked while it is open, so two processes cannot write to it at once. If the lock is lost while the session is open (its lock directory removed, or taken over by another process), the session fails at once: nothing more is saved, the TUI says the lock was lost, and `pisharp durable` exits with 1. These sessions are separate from ordinary PiSharp sessions: `pisharp --resume` does not list them, and `pisharp durable` does not open ordinary sessions.

The layout and the lock are those of Pi v1.1.0's experimental durable agent, so each finds the other's newest session with `--continue` and respects the other's lock. Pi v1.1.0 cannot resume the records PiSharp's v1.2.0 library writes, though, so don't continue a PiSharp session with it.

## How it differs from Pi

- **The command is PiSharp's.** Pi v1.1.0 ran its durable agent from source, not from the `pi` command, and Pi v1.2.0 removed that agent. PiSharp keeps it as a PiSharp-only mode, ported from Pi v1.1.0's agent.
- **The libraries follow `pi-durable` v1.2.0.** The storage format (SQLite schema, JSONL layout and record JSON) is upstream's. Tests open, resume and rewrite sessions that upstream wrote, and upstream reads PiSharp's.
- **Code mode scripts don't move between Pi and PiSharp.** Durable code mode records its engine's fingerprint, and PiSharp runs scripts on Jint where Pi uses QuickJS, so neither resumes a script the other interrupted. The resume is refused rather than diverging.
- **A lost lock fails the session.** Pi's agent ends its process when its session lock is lost; PiSharp fails the session instead, so the TUI can say why before it exits with 1. Either way nothing more is saved.
- **Images use SkiaSharp.** Upstream resizes images with Photon compiled to WebAssembly. PiSharp applies the same rules through the image codec its main `read` tool uses.
- **No Cloudflare adapters.** The Cloudflare Durable Object storage and image processor are not ported.

## Libraries

Three NuGet packages, versioned with the rest of PiSharp:

| Package | Contents |
| --- | --- |
| `PiSharp.Durable` | The harness, sessions, conversations, tasks, and in-memory and JSONL storage |
| `PiSharp.Durable.Sqlite` | SQLite storage, through `Microsoft.Data.Sqlite` and SQLitePCLRaw's e_sqlite3 native library |
| `PiSharp.Durable.Tools` | The local execution environment, the coding tools, `tool_search`, durable code mode, and on `main` the media tools (experimental, unreleased) |

Every public type is marked `[Experimental("PISHARP_DURABLE001")]`, so the compiler reports an error until you opt in by suppressing that diagnostic:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);PISHARP_DURABLE001</NoWarn>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="PiSharp.Durable" Version="1.2.0.1" />
  <PackageReference Include="PiSharp.Durable.Sqlite" Version="1.2.0.1" />
  <PackageReference Include="PiSharp.Durable.Tools" Version="1.2.0.1" />
</ItemGroup>
```

`PiSharp.Durable.Sqlite` brings the e_sqlite3 build for each platform, and the packages carry their license notices under `licenses/`. [Durable library](/docs/embed/durable/) shows how to build an agent on them. [`samples/Durable.Chat`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Chat) is a small chat app that runs on a real model, and [`samples/Durable.Examples`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Examples) ports upstream's examples.

## Known issues

- JSONL storage keeps the records it wrote in memory, so a long session on JSONL grows (about 400 MiB where Node holds 1 MiB in one benchmark). SQLite storage, which `pisharp durable` uses, does not. See [durable-benchmarks.md](https://github.com/jkelly/PiSharp/blob/main/docs/durable-benchmarks.md).
- `pisharp durable`'s edit cards differ slightly from interactive mode's.

## Status

[Decision 0005](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0005-pi-durable-experimental.md) records the scope, the differences from Pi, the port status and the decision to release it as experimental. [`durable-port-map.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/durable-port-map.json) maps every upstream file to its C# counterpart. The [1.2.0.1 release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.2.0.1.md) cover what shipped. Upstream's [`pi-durable` README](https://github.com/earendil-works/pi/blob/v1.2.0/packages/durable/README.md) and [specification](https://github.com/earendil-works/pi/blob/v1.2.0/packages/durable/docs/spec.md) describe the design.

Feedback is welcome in [GitHub issues](https://github.com/jkelly/PiSharp/issues).
