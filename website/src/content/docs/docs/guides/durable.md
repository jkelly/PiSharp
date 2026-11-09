---
title: Pi Durable
description: PiSharp's experimental port of Pi's durable agent harness, whose sessions survive a crash and resume where they stopped. Experimental since PiSharp 1.2.0.1.
sidebar:
  badge:
    text: Experimental
    variant: caution
---

:::caution[Experimental]
Pi Durable ships as experimental from PiSharp 1.2.0.1: the `pisharp durable` command and the `PiSharp.Durable`, `PiSharp.Durable.Tools` and `PiSharp.Durable.Sqlite` packages. PiSharp 1.2.0.2 adds questions, plan mode, service tiers, the media tools and the [device server](#device-server). PiSharp 1.5.0 moves the libraries to `pi-durable` v1.5.0, whose session format older PiSharp versions can't open (see [Upgrading to 1.5.0](#upgrading-to-150)). Its command, session format and library API may change or be removed in any release, without notice or a deprecation period.
:::

Pi includes `pi-durable`, an experimental agent harness that saves every step of a conversation before it shows it. PiSharp ports it as a set of .NET libraries and as an interactive mode, `pisharp durable`.

## What durable means

- Every model turn, streamed partial answer, tool call and queued message is committed to storage before it appears.
- If the process dies mid-turn, reopening the session finishes that turn. The turn still ends with exactly one answer, and queued messages are not duplicated. From 1.5.0, a model request the crash cut off counts as a failed attempt and is retried after the usual backoff, and up to about a second of the answer streamed before the crash may be lost, since partial answers are stored about once a second.
- Work that keeps crashing its process stops: from 1.5.0, a task cut off five times in a row ends instead of running again.
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
| `--tier <tier>`, `--service-tier <tier>` | (from 1.2.0.2) Sets the main conversation's [service tier](/docs/guides/service-tiers/): `auto`, `normal`, `economy`, `priority`, `fast` or `ultrafast`. It applies when the run creates the session; a session that `--continue` reopens keeps its stored tier, which you change with `/tier`. |

`--help` or `-h` prints the options and exits without opening a session (from 1.2.0.2). Any other argument fails with `Unknown argument: <argument>`, a tier name it doesn't know fails with `Invalid service tier`, and `--tier` without a value fails with `--tier needs a tier`. Each exits with 1. The command is listed in `pisharp --help` as experimental, and it prints a one-line experimental notice when it starts. The [device server](#device-server) commands, such as `pisharp durable serve`, are separate.

It reads the same settings, credentials and models as `pisharp`, so providers you have signed in to work here too. A new session starts on your `defaultProvider`, `defaultModel` and `defaultThinkingLevel`.

### Tools

| Tool | What it does |
| --- | --- |
| `read`, `write`, `edit`, `bash` | The coding tools. `read` returns images too. A call that a crash interrupts is not run again: the model gets an "interrupted" result. |
| `subagent` | Hands a self-contained task to a child conversation with the same tools, except `subagent`, `ask_user_question` and `exit_plan_mode`, and returns its answer. Arguments: `task` (required) and `tier` (from 1.2.0.2), the child's service tier; without it the child uses this conversation's. Replay-safe: after a crash, the rerun finds the child and the message it already sent. |
| Media tools | (from 1.2.0.2) `generate_image`, `generate_video`, `embed`, `list_media_models` and `set_media_defaults`, plus `generate_speech`, `transcribe_audio`, `generate_music`, `rerank` and `decide`, which are deferred. Installed only when the `media.enabled` setting isn't `false` and a provider with a media model is configured. See [Media tools](/docs/guides/media/). |
| `ask_user_question`, `exit_plan_mode` | PiSharp-only tools that wait for you: see [Questions from the agent](#questions-from-the-agent) and [Plan mode](#plan-mode). Only the conversation you are looking at has them. |
| `tool_search` | Finds deferred tools and loads them. Offered only while an installed tool is deferred, which today means the media tools. |

### In a session

| Input | What it does |
| --- | --- |
| A message | Sends it. While the agent is working, it steers the current turn. |
| The follow-up key | Queues the message until the agent finishes. |
| `/model` | Picks the model. |
| `/tier [tier]` | (from 1.2.0.2) Without a tier, shows the shown conversation's service tier and the choices. With one, sets it for that conversation from its next request; `/tier none` (or `clear`) removes it. The tier is saved in the session. See [Service tiers](/docs/guides/service-tiers/#in-pisharp-durable). |
| `/tasks` | Shows or hides running tasks. |
| `/agents` | Switches between the main conversation and its subagents. |
| `/compact [instructions]` | Compacts the conversation. |
| `/plan` | Turns [plan mode](#plan-mode) on, or off when it is on. |
| `/plan <message>` | Turns plan mode on and sends the message. |
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
| `serviceTier` | (from 1.2.0.2) The tier of a new session when you don't pass `--tier`. |
| `planMode.directory` | Where [plan mode](#plan-mode) keeps plans. By default each conversation gets `plans/<conversation id>` in the session's folder. |
| `media` | (from 1.2.0.2) The media tools. See [Media tools](/docs/guides/media/#settings). |

See [Settings](/docs/reference/settings/) for where settings files live.

### Questions from the agent

When the agent needs a decision from you, it calls `ask_user_question` with one to four multiple-choice questions, and a form replaces the editor until you answer. Whatever you had typed stays in the editor and comes back afterwards.

- **Tabs.** The top row has a tab for each question, named by its short header, and a Submit tab. ←/→ or Tab/Shift+Tab move between them. A ✓ marks the questions you have answered.
- **Choosing.** ↑/↓ move over the options, shown as "label — description". A digit jumps to that row.
  - A single-choice question takes Enter or the option's digit, then moves to the next tab.
  - A multiple-choice question toggles options with Space or their digits, and Enter moves on.
- **Other.** Every question has an "Other…" row for your own text. It opens an inline input: Enter keeps the text, Escape goes back to the options.
- **Submit.** The Submit tab sums up your answers and offers "Submit answers" and "Decline to answer". If you decline, the agent proceeds on its own judgement.
- **Abort or leave.** Escape (or Ctrl+C) aborts the turn. Ctrl+D exits and leaves the question waiting: the question is saved, so `pisharp durable --continue` shows the form again, even after a crash.

The status line says "Waiting for your answer...", and the tool card shows the questions and, once you answer, your answers. The answers go back to the agent as the tool's result in the same turn.

### Plan mode

Plan mode is for agreeing on a plan before anything in the project changes, as in Claude Code and Codex.

- **Turning it on.** `/plan` turns it on for the conversation you are looking at, and `/plan <message>` turns it on and sends the message. The footer shows a **plan mode** badge.
- **What the agent can do.** `write` and `edit` work only inside a plan directory, for the plan file, notes and prototypes. `read` and `bash` work as usual, and the agent can ask you questions. `/plan` tells you where the directory is: by default `plans/<conversation id>` in the session's folder, outside your project. To put plans elsewhere, set `planMode.directory` in `settings.json` to an absolute path, a `~` path or a path relative to the project; each conversation then gets `<session id>-<conversation id>` inside it.
- **Reviewing the plan.** When the plan is ready, the agent calls `exit_plan_mode`, and a review replaces the editor. It shows the plan and its file, and offers four choices:

  | Choice | What happens |
  | --- | --- |
  | Yes, implement it | Plan mode ends, the agent gets all its tools back and starts on the plan. |
  | Yes, but don't start yet | Plan mode ends and the turn stops. Tell the agent when to start. |
  | No, keep planning (give feedback) | Opens an editor for your feedback. The agent stays in plan mode and revises the plan. |
  | No, keep planning | The agent stays in plan mode and revises the plan. |

- **Turning it off.** `/plan` while plan mode is on turns it off. A plan still waiting for review is then cancelled and the turn ends.

Like questions, a plan review survives closing and crashing: `--continue` shows it again.

### Where sessions are stored

Each session is one SQLite database:

```text
~/.pi/agent/experimental/durable-sessions/<folder hash>/<timestamp>-<id>/session.sqlite
```

A session is locked while it is open, so two processes cannot write to it at once. If the lock is lost while the session is open (its lock directory removed, or taken over by another process), the session fails at once: nothing more is saved, the TUI says the lock was lost, and `pisharp durable` exits with 1. These sessions are separate from ordinary PiSharp sessions: `pisharp --resume` does not list them, and `pisharp durable` does not open ordinary sessions.

The layout and the lock are those of Pi v1.1.0's experimental durable agent, so each finds the other's newest session with `--continue` and respects the other's lock. Pi v1.1.0 cannot resume the records PiSharp's v1.2.0 library writes, though, so don't continue a PiSharp session with it.

### Upgrading to 1.5.0

PiSharp 1.5.0 opens and continues sessions that 1.2.0.x wrote. Opening one migrates its database to schema version 2, and the tasks and documents 1.5.0 writes are newer than 1.2.0.x knows, so once 1.5.0 has opened a session, PiSharp 1.2.0.x can't open it again. Copy a session's folder first if you may go back. A conversation from 1.2.0.x records its tool declarations once more on its next turn, because 1.5.0 writes them in another key order; the model sees the same tools.

## Device server

From 1.2.0.2, `pisharp durable serve` runs this machine as a durable device: many durable sessions in one process, behind an authenticated HTTP and server-sent-events API. Pi has no device server, and neither does `pi-durable`; this is a PiSharp addition ([decision 0008](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0008-durable-network-host-and-web.md)) that follows the wire format of the MIT-licensed durable-pi project. These commands are not in `pisharp --help`; `pisharp durable help` lists them all, and `pisharp durable help <command>` shows one.

```sh
pisharp durable serve                 # listen on 127.0.0.1:4780
pisharp durable token                 # print the device token
pisharp durable sessions list         # a device command, on the local device
```

:::danger[The device token is a shell]
Anyone who has the token can run commands on this machine as you, in any of the device's start directories. The start directories are not a sandbox. The server listens on `127.0.0.1` unless you pass `--host`, and then it warns you. The API is plain HTTP, so to reach a device from another machine, use an SSH tunnel or a TLS proxy rather than `--host`.
:::

| `serve` option | What it does |
| --- | --- |
| `--port <n>` | The port. The default is 4780. |
| `--host <address>` | The address to listen on. The default is `127.0.0.1`. |
| `--name <name>` | The device's name. |
| `--start-dirs <dir>[,<dir>...]` | The folders where sessions may be created. The default is your home folder. |
| `--data-dir <dir>` | The device's folder. The default is `~/.pi/agent/experimental/durable-device`, which holds `device.json`, the token file, the catalog and the sessions. |
| `--model <provider>/<id>` | The default model. Models come from your Pi configuration, as in `pisharp`. |
| `--provider faux` | Uses a scripted model instead, for trying the device without a provider. |
| `--log <file>` | Writes the device's output to a size-rotated log file, for service managers that keep no journal. |
| `--save` | Saves these options in `device.json`. |

`pisharp durable token` prints the token, and `--rotate` replaces it. The token is kept in a file only you can read. It never goes on a command line: a command that works on another device reads it from the `PISHARP_DEVICE_TOKEN` environment variable or from `--token-file <path>`.

The device commands cover the device and its capabilities, sessions, executions, schedules, trigger credentials, budgets, retention, policies, judgments, artifacts, evidence, sources, research, feedback and preferences. Each takes `--json` for machine-readable output. Without `--device <url>`, a command uses the local device. Four commands work on the local device's folder: `attach` (a line-mode terminal on a session's agent), `doctor` (checks the device), `service` (writes a per-user systemd, launchd or Windows service definition, and installs nothing) and `backup create`, `backup verify` and `backup restore`.

The device server needs the ASP.NET Core runtime. The .NET SDK includes it; with the .NET runtime alone, install the ASP.NET Core runtime too.

## How it differs from Pi

- **The command is PiSharp's.** Pi v1.1.0 ran its durable agent from source, not from the `pi` command, and Pi v1.2.0 removed that agent. PiSharp keeps it as a PiSharp-only mode, ported from Pi v1.1.0's agent.
- **The libraries follow `pi-durable` v1.5.0.** The storage format (SQLite schema, JSONL layout and record JSON) follows upstream's. Tests open, resume and rewrite sessions that upstream v1.2.0 wrote, comparing with v1.5.0's forms of those records; sessions written by upstream v1.5.0 have not been compared yet.
- **Code mode scripts don't move between Pi and PiSharp.** Durable code mode records its engine's fingerprint, and PiSharp runs scripts on Jint where Pi uses QuickJS, so neither resumes a script the other interrupted. The resume is refused rather than diverging.
- **A lost lock fails the session.** Pi's agent ends its process when its session lock is lost; PiSharp fails the session instead, so the TUI can say why before it exits with 1. Either way nothing more is saved.
- **Images use SkiaSharp.** Upstream resizes images with Photon compiled to WebAssembly. PiSharp applies the same rules through the image codec its main `read` tool uses.
- **No Cloudflare adapters.** The Cloudflare Durable Object storage and image processor are not ported, nor is the browser's IndexedDB storage.
- **Chord libraries ship in 1.5.0.1.** This release includes the real [Chord host](/docs/embed/chord/) and [Durable.Store examples 35–37](/docs/embed/durable-store/). These libraries retain their experimental diagnostics and exact sibling-version requirements.
- **Questions and plan mode are PiSharp's.** `pi-durable` has no `ask_user_question` tool and no plan mode. PiSharp adds both, in `PiSharp.Durable.Tools` and in `pisharp durable`, so a session that used them stores two document kinds that Pi doesn't know (`pisharp.interaction` and `pisharp.plan-mode`).

## Libraries

Three NuGet packages, versioned with the rest of PiSharp:

| Package | Contents |
| --- | --- |
| `PiSharp.Durable` | The harness, sessions, conversations, tasks, and in-memory and JSONL storage |
| `PiSharp.Durable.Sqlite` | SQLite storage, through `Microsoft.Data.Sqlite` and SQLitePCLRaw's e_sqlite3 native library |
| `PiSharp.Durable.Tools` | The local execution environment, the coding tools, `tool_search`, durable code mode, and, from 1.2.0.2, the media tools |

Every public type is marked `[Experimental("PISHARP_DURABLE001")]`, so the compiler reports an error until you opt in by suppressing that diagnostic:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);PISHARP_DURABLE001</NoWarn>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="PiSharp.Durable" Version="1.5.0.1" />
  <PackageReference Include="PiSharp.Durable.Sqlite" Version="1.5.0.1" />
  <PackageReference Include="PiSharp.Durable.Tools" Version="1.5.0.1" />
</ItemGroup>
```

`PiSharp.Durable.Sqlite` brings the e_sqlite3 build for each platform, and the packages carry their license notices under `licenses/`. [Durable library](/docs/embed/durable/) shows how to build an agent on them. [`samples/Durable.Chat`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Chat) is a small chat app that runs on a real model, and [`samples/Durable.Examples`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Examples) ports upstream's examples.

## Known issues

- JSONL storage keeps the records it wrote in memory, so a long session on JSONL grows (about 400 MiB where Node holds 1 MiB in one benchmark). SQLite storage, which `pisharp durable` uses, does not. See [durable-benchmarks.md](https://github.com/jkelly/PiSharp/blob/main/docs/durable-benchmarks.md).
- `pisharp durable`'s edit cards differ slightly from interactive mode's.
- **Plan mode does not restrict `bash`.** Only `write` and `edit` are confined to the plan directory. The agent is told not to change anything outside it through the shell, but nothing enforces that, so it could change any file with a command. This was a deliberate choice, so that the agent can still inspect the project with commands such as `git log`, builds and tests.
- Plan mode's containment of `write` and `edit` has other limits. Another process could swap a folder for a link between the check and the write, and a hard link or mount inside a plan directory you chose yourself can lead outside it.
- Subagents can't ask you questions or use plan mode: only the conversation you are looking at can show a form.

## Status

[Decision 0005](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0005-pi-durable-experimental.md) records the scope, the differences from Pi, the port status and the decision to release it as experimental. [`durable-port-map.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/durable-port-map.json) maps every upstream file to its C# counterpart. The [1.2.0.1](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.2.0.1.md), [1.2.0.2](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.2.0.2.md) and [1.5.0](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.5.0.md) release notes cover what shipped. Upstream's [`pi-durable` README](https://github.com/earendil-works/pi/blob/v1.5.0/packages/durable/README.md) and [specification](https://github.com/earendil-works/pi/blob/v1.5.0/packages/durable/docs/spec.md) describe the design.

Feedback is welcome in [GitHub issues](https://github.com/jkelly/PiSharp/issues).
