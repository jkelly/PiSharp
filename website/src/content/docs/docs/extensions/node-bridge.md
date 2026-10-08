---
title: Node bridge
description: Run Pi's existing TypeScript extensions alongside native ones.
---

The Node bridge is an optional way to run Pi's existing TypeScript extensions. They run in a separate Node worker that PiSharp starts and supervises. PiSharp installs and runs fully without Node; you only need Node if you turn the bridge on.

## How it works

- Extension descriptors, messages, tool arguments, results and hook decisions are sent between PiSharp and the worker as data. Functions, class instances and terminal UI component objects can't cross the process boundary.
- Module resolution, transpilation and dependency installation stay inside the worker host. npm install scripts and native modules count as executable code, so they need your explicit trust and never run during ordinary discovery.
- Each compatibility session starts with one worker, which keeps the ordering between extensions the same as Pi's.
- Each handler is tied to its registration and released on unsubscribe, reload or session replacement. If the worker crashes, PiSharp doesn't replay an action with side effects just to recover.

## Compatibility tiers

Bridge support is tiered. If an extension registers something the bridge doesn't support, PiSharp reports it when it starts, so nothing is silently ignored. See the **TypeScript extension compatibility** tab on the [Parity](/parity/) page for current status.

:::note
Prefer [native C# extensions](/docs/extensions/build-an-extension/) for new work. They run in-process, are typed, and don't need Node.
:::
