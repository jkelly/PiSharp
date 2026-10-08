---
title: Coming from Pi
description: What carries over from Pi, what's imported, and what's different.
---

PiSharp follows Pi's design closely, so most of what you know carries over. This page covers what's shared, what's imported, and what's different.

:::tip
These docs describe PiSharp's current baseline, Pi v0.99.1. Features Pi added later aren't covered until the next sync. Check [Parity](/parity/) for details.
:::

## What carries over

| From Pi | In PiSharp | Notes |
| --- | --- | --- |
| `AGENTS.md`, `SYSTEM.md` | Read as-is | Project instructions and system prompt files. |
| Skills, prompt templates, themes | Read as-is | Copy them, or point PiSharp at your existing folders. |
| Session files | Import | Copied into PiSharp's store. The original file is never modified. |
| RPC clients | Compatible protocol | The same JSON over stdin/stdout. |
| TypeScript extensions | Node bridge | Optional and tiered. See [Node bridge](/docs/extensions/node-bridge/). |

## Where PiSharp keeps its files

PiSharp uses its own home directory by default, so it can run side by side with Pi. It never silently overwrites Pi's settings, credentials or session files.

## Importing Pi sessions

Imports are explicit: you choose a path, PiSharp copies the session in, and the original file is preserved. Older session versions are migrated during import, and the migration is reported to you.

When PiSharp has state that Pi's format can't represent, it offers a Pi-compatible export that tells you what was left out, plus a native export that keeps everything.

## What's different

- **Extensions are C#.** Native extensions are .NET assemblies that implement `IPiSharpExtension`. See [Build an extension](/docs/extensions/build-an-extension/).
- **No Node required.** Node is only needed if you opt into the bridge for existing TypeScript extensions.
- **Versions match Pi's.** See [How versions work](/docs/versioning/).
