---
title: Pi Durable
description: A preview of PiSharp's port of Pi's durable agent harness, whose sessions survive a crash and resume where they stopped. Not yet released.
sidebar:
  badge:
    text: Preview
    variant: caution
---

:::caution[Experimental and unreleased]
Pi Durable is not in any PiSharp release or NuGet package. It lives on the [`feature/pi-durable`](https://github.com/jkelly/PiSharp/tree/feature/pi-durable) branch, and you build that branch to try it. Its command, session format and library API may change or be removed without notice.
:::

Pi includes `pi-durable`, an experimental agent harness that saves every step of a conversation before it shows it. PiSharp ports it as a set of .NET libraries and as an interactive mode, `pisharp durable`.

## What durable means

- Every model turn, streamed partial answer, tool call and queued message is committed to storage before it appears.
- If the process dies mid-turn, reopening the session finishes that turn. The turn still ends with exactly one answer, and queued messages are not duplicated.
- A tool that was interrupted after it started is not run again unless it is declared replay-safe. The model gets an "interrupted" result instead. A replay-safe tool runs again exactly once.
- Subagents run as child conversations owned by the task that called them. A child outlives the call, so you can switch to it and keep talking.

PiSharp's tests check these guarantees by killing a real process at checkpoints in every phase of a turn, then reopening its storage the way `pisharp durable --continue` does.

## Try `pisharp durable`

Build the branch with the .NET SDK version pinned in its `global.json`:

```sh
git clone --branch feature/pi-durable https://github.com/jkelly/PiSharp.git
cd PiSharp
dotnet run --project src/PiSharp.Cli -- durable
```

`pisharp durable` takes one option. `--continue` (or `-c`) reopens the newest session for the current folder, and without it each run starts a new session. The command is left out of `pisharp --help` and prints a one-line experimental notice when it starts.

It reads the same settings, credentials and models as `pisharp`, so providers you have signed in to work here too. The model gets the `read`, `write`, `edit` and `bash` tools, with images from `read`, and a `subagent` tool.

### In a session

| Input | What it does |
| --- | --- |
| A message | Sends it. While the agent is working, it steers the current turn. |
| The follow-up key | Queues the message until the agent finishes. |
| `/model` | Picks the model. |
| `/tasks` | Shows or hides running tasks. |
| `/agents` | Switches between the main conversation and its subagents. |
| `/compact [instructions]` | Compacts the conversation. |
| Escape | Aborts the turn. |
| Ctrl+D | Exits. |

Your [key bindings](/docs/guides/interactive/#key-bindings) apply, including the keys for the model selector and the thinking level.

### Where sessions are stored

Each session is one SQLite database:

```text
~/.pi/agent/experimental/durable-sessions/<folder hash>/<timestamp>-<id>/session.sqlite
```

A session is locked while it is open, so two processes cannot write to it at once. These sessions are separate from ordinary PiSharp sessions: `pisharp --resume` does not list them, and `pisharp durable` does not open ordinary sessions.

## How it differs from Pi

- **The command is PiSharp's.** Pi v1.1.0 ran its durable agent from source, not from the `pi` command, and Pi v1.2.0 removed that agent. PiSharp keeps it as a PiSharp-only mode, ported from Pi v1.1.0's agent.
- **The libraries follow `pi-durable`.** They are being synced to Pi v1.2.0's version, which changed the package heavily. The storage format (SQLite schema, JSONL layout and record JSON) is upstream's. Tests open, resume and rewrite sessions that upstream wrote.
- **Images use SkiaSharp.** Upstream resizes images with Photon compiled to WebAssembly. PiSharp applies the same rules through the image codec its main `read` tool uses.
- **No Cloudflare adapters.** The Cloudflare Durable Object storage and image processor are not ported.

## Libraries

The branch has three projects you can reference from source. They are not packed for NuGet.

| Project | Contents |
| --- | --- |
| `PiSharp.Durable` | The harness, sessions, conversations, tasks, and in-memory and JSONL storage |
| `PiSharp.Durable.Sqlite` | SQLite storage, through `Microsoft.Data.Sqlite` |
| `PiSharp.Durable.Tools` | The local execution environment, the coding tools and `tool_search` |

Every public type is marked `[Experimental("PISHARP_DURABLE001")]`, so the compiler reports an error until you suppress that diagnostic. `samples/Durable.Chat` is a small chat app that runs on a real model, and `samples/Durable.Examples` ports upstream's examples.

## Status

[Decision 0005](https://github.com/jkelly/PiSharp/blob/feature/pi-durable/docs/decisions/0005-pi-durable-experimental.md) on the branch records the scope, the differences from Pi and the current port status. [`durable-port-map.json`](https://github.com/jkelly/PiSharp/blob/feature/pi-durable/compatibility/durable-port-map.json) maps every upstream file to its C# counterpart. Upstream's [`pi-durable` README](https://github.com/earendil-works/pi/blob/v1.2.0/packages/durable/README.md) and [specification](https://github.com/earendil-works/pi/blob/v1.2.0/packages/durable/docs/spec.md) describe the design.

There is no release date. Feedback is welcome in [GitHub issues](https://github.com/jkelly/PiSharp/issues).
