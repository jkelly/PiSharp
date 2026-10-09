---
title: TypeScript extensions
description: Run Pi's TypeScript and JavaScript extensions in PiSharp through the Node bridge.
---

PiSharp runs Pi's TypeScript and JavaScript extensions unchanged. It starts Node, loads each extension with Pi's own packages, and connects the extension to the C# session. Write extensions as Pi's [Extensions](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/extensions.md) page describes.

## Where extensions come from

The same places as in Pi:

- `~/.pi/agent/extensions/` and the project's `.pi/extensions/`: a `.ts` or `.js` file, or a folder with `index.ts`, `index.js` or a `package.json` with a `pi.extensions` list.
- The `extensions` and `packages` lists in `settings.json`.
- `-e <path>` or `-e npm:<package>` for one run.

Project extensions load only when you trust the project. `--no-extensions` turns off everything but `-e`. Discovered extensions run as you, with your permissions, as in Pi.

## What you need

- **Node.js 22.13 or later**, on your `PATH` or named by `PISHARP_NODE`. Pi itself asks for Node 22.19.
- **npm**, the first time an extension loads (see below).

PiSharp starts Node only when an extension needs it. Without extensions, or with only [C# extensions](/docs/extensions/build-an-extension/), Node isn't needed.

## Pi's own packages

The first time a TypeScript extension loads, PiSharp installs Pi 1.1.0's npm packages (`@earendil-works/pi-coding-agent`, `pi-ai`, `pi-tui` and `pi-agent-core`, with `typebox` and `jiti`) into `~/.pi/agent/pisharp/pi-runtime/1.1.0`, using your npm. Every package's integrity is checked against the npm registry before it's used. Extensions then load through Pi's own `jiti` setup, so imports such as `@earendil-works/pi-coding-agent` resolve to the real code.

`PISHARP_PI_RUNTIME_DIR` moves this folder.

If the install can't run (offline, no npm, or a failed install), extensions still load against PiSharp's built-in copies of those modules, and PiSharp warns that they aren't running against the Pi 1.1.0 packages. The built-in copies don't support `.tsx` files or built-in tool factories with custom `operations`.

## What works

The whole `ExtensionAPI`: tools, commands, shortcuts, flags, message and tool renderers, providers (chat, classifier and image), virtual models, MCP servers, the event bus, and every extension event. `ctx.ui` dialogs, widgets and custom components show in the terminal UI and over RPC.

## Differences from Pi

- `ctx.ui.setTheme()` returns an error. Choose a theme in `/settings` instead.
- `pi.sendMessage(…, { triggerTurn: true })` from inside a command starts its turn after the command's handler returns. In Pi the turn starts while the handler is still running. A handler that runs longer than about 30 seconds drops the message.
