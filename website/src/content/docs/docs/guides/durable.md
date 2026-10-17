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

`pisharp durable` takes one option. `--continue` (or `-c`) reopens the newest session for the current folder, and without it each run starts a new session. Any other argument fails with `Unknown argument`. The command is listed in `pisharp --help` as experimental and prints a one-line experimental notice when it starts.

It reads the same settings, credentials and models as `pisharp`, so providers you have signed in to work here too. The model gets the `read`, `write`, `edit` and `bash` tools, with images from `read`, a `subagent` tool, and two PiSharp-only tools that wait for you: `ask_user_question` and `exit_plan_mode` (see [Questions from the agent](#questions-from-the-agent) and [Plan mode](#plan-mode)).

### In a session

| Input | What it does |
| --- | --- |
| A message | Sends it. While the agent is working, it steers the current turn. |
| The follow-up key | Queues the message until the agent finishes. |
| `/model` | Picks the model. |
| `/tasks` | Shows or hides running tasks. |
| `/agents` | Switches between the main conversation and its subagents. |
| `/compact [instructions]` | Compacts the conversation. |
| `/plan` | Turns [plan mode](#plan-mode) on, or off when it is on. |
| `/plan <message>` | Turns plan mode on and sends the message. |
| Escape | Aborts the turn. |
| Ctrl+D | Exits. |

Your [key bindings](/docs/guides/interactive/#key-bindings) apply, including the keys for the model selector and the thinking level.

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

## How it differs from Pi

- **The command is PiSharp's.** Pi v1.1.0 ran its durable agent from source, not from the `pi` command, and Pi v1.2.0 removed that agent. PiSharp keeps it as a PiSharp-only mode, ported from Pi v1.1.0's agent.
- **The libraries follow `pi-durable` v1.2.0.** The storage format (SQLite schema, JSONL layout and record JSON) is upstream's. Tests open, resume and rewrite sessions that upstream wrote, and upstream reads PiSharp's.
- **Code mode scripts don't move between Pi and PiSharp.** Durable code mode records its engine's fingerprint, and PiSharp runs scripts on Jint where Pi uses QuickJS, so neither resumes a script the other interrupted. The resume is refused rather than diverging.
- **A lost lock fails the session.** Pi's agent ends its process when its session lock is lost; PiSharp fails the session instead, so the TUI can say why before it exits with 1. Either way nothing more is saved.
- **Images use SkiaSharp.** Upstream resizes images with Photon compiled to WebAssembly. PiSharp applies the same rules through the image codec its main `read` tool uses.
- **No Cloudflare adapters.** The Cloudflare Durable Object storage and image processor are not ported.
- **Questions and plan mode are PiSharp's.** `pi-durable` has no `ask_user_question` tool and no plan mode. PiSharp adds both, in `PiSharp.Durable.Tools` and in `pisharp durable`, so a session that used them stores two document kinds that Pi doesn't know (`pisharp.interaction` and `pisharp.plan-mode`).

## Libraries

Three NuGet packages, versioned with the rest of PiSharp:

| Package | Contents |
| --- | --- |
| `PiSharp.Durable` | The harness, sessions, conversations, tasks, and in-memory and JSONL storage |
| `PiSharp.Durable.Sqlite` | SQLite storage, through `Microsoft.Data.Sqlite` and SQLitePCLRaw's e_sqlite3 native library |
| `PiSharp.Durable.Tools` | The local execution environment, the coding tools, `tool_search` and durable code mode |

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

`PiSharp.Durable.Sqlite` brings the e_sqlite3 build for each platform, and the packages carry their license notices under `licenses/`. [`samples/Durable.Chat`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Chat) is a small chat app that runs on a real model, and [`samples/Durable.Examples`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Examples) ports upstream's examples.

## Known issues

- JSONL storage keeps the records it wrote in memory, so a long session on JSONL grows (about 400 MiB where Node holds 1 MiB in one benchmark). SQLite storage, which `pisharp durable` uses, does not. See [durable-benchmarks.md](https://github.com/jkelly/PiSharp/blob/main/docs/durable-benchmarks.md).
- `pisharp durable`'s edit cards differ slightly from interactive mode's.
- **Plan mode does not restrict `bash`.** Only `write` and `edit` are confined to the plan directory. The agent is told not to change anything outside it through the shell, but nothing enforces that, so it could change any file with a command. This was a deliberate choice, so that the agent can still inspect the project with commands such as `git log`, builds and tests.
- Plan mode's containment of `write` and `edit` has other limits. Another process could swap a folder for a link between the check and the write, and a hard link or mount inside a plan directory you chose yourself can lead outside it.
- Subagents can't ask you questions or use plan mode: only the conversation you are looking at can show a form.

## Status

[Decision 0005](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0005-pi-durable-experimental.md) records the scope, the differences from Pi, the port status and the decision to release it as experimental. [`durable-port-map.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/durable-port-map.json) maps every upstream file to its C# counterpart. The [1.2.0.1 release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.2.0.1.md) cover what shipped. Upstream's [`pi-durable` README](https://github.com/earendil-works/pi/blob/v1.2.0/packages/durable/README.md) and [specification](https://github.com/earendil-works/pi/blob/v1.2.0/packages/durable/docs/spec.md) describe the design.

Feedback is welcome in [GitHub issues](https://github.com/jkelly/PiSharp/issues).
