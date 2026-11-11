---
title: TypeScript extensions
description: Run Pi's TypeScript and JavaScript extensions in PiSharp through the Node bridge.
---

PiSharp runs Pi's TypeScript and JavaScript extensions unchanged. It starts Node, loads each extension with Pi's own packages, and connects the extension to the C# session. Write extensions as Pi's [Extensions](https://github.com/earendil-works/pi/blob/v1.5.0/packages/coding-agent/docs/extensions.md) page describes.

:::note[Unreleased 1.5.2 source candidate]
The source candidate installs exact Pi 1.5.2 packages into a separate `1.5.2-no-default-attribution-v1` cache and regenerates the fallback modules from the pinned 1.5.2 source. It preserves older caches and explicit extension identities. Local candidate source validation is recorded with explicit suite and platform limitations. The 1.5.0 runtime details below describe the published PiSharp 1.5.0.1 release; they are retained as release history.
:::

:::note[Runtime changes in 1.5.0.1]
The installed Pi 1.5.0 runtime and overlay handle behavior described below ship in PiSharp 1.5.0.1. The earlier PiSharp 1.5.0 release used Pi 1.2.0 runtime packages. This release retains old caches for rollback, disables default attribution and adds overlay lifetime/focus/visibility/resize synchronization. Platform qualification is tracked separately from the runtime installation check.
:::

## Where extensions come from

The same places as in Pi:

- `~/.pi/agent/extensions/` and the project's `.pi/extensions/`: a `.ts` or `.js` file, or a folder with `index.ts`, `index.js` or a `package.json` with a `pi.extensions` list.
- The `extensions` and `packages` lists in `settings.json`.
- `-e <path>` or `-e npm:<package>` for one run.

Project extensions load only when you trust the project. `--no-extensions` turns off everything but `-e`. Discovered extensions run as you, with your permissions, as in Pi.

## What you need

- **Node.js 22.19 or later**, on your `PATH` or named by `PISHARP_NODE`, for the installed Pi 1.5.0 packages.
- **npm**, the first time an extension loads (see below).

PiSharp starts Node only when an extension needs it. Without extensions, or with only [C# extensions](/docs/extensions/build-an-extension/), Node isn't needed.

## Pi's own packages

The first time a TypeScript extension loads, PiSharp installs Pi 1.5.0's npm packages (`@earendil-works/pi-coding-agent`, `pi-ai`, `pi-tui` and `pi-agent-core`, with `typebox` 1.3.27 and `jiti` 2.7.0) into `~/.pi/agent/pisharp/pi-runtime/1.5.0-no-default-attribution-v1`, using your npm. The Pi packages are pinned to exactly 1.5.0 and `undici` to 8.12.3. Every package's lockfile integrity is checked against the npm registry before installation is admitted. Extensions load through Pi's own `jiti` setup and share the installed packages' module identities. PiSharp clears the installed agent-core's default Pi attribution before extensions load; an extension's explicit request identity remains effective.

`PISHARP_PI_RUNTIME_DIR` selects the parent of the version and policy folder. Existing caches, including Pi 1.2.0 caches and earlier configured runtime directories, remain untouched for rollback. An unverified existing target is preserved and causes an explicit fallback.

The Node child defaults `PI_TELEMETRY` to `0` so the installed SDK also defaults to no Pi attribution. An explicit environment value remains effective; the parent process environment is unchanged.

If the install can't run (offline, no npm, or a failed install), extensions still load against PiSharp's built-in copies of those modules, and PiSharp warns that they aren't running against the Pi 1.5.0 packages. The built-in copies don't support `.tsx` files or built-in tool factories with custom `operations`.

## What works

The whole `ExtensionAPI`: tools, commands, shortcuts, flags, message and tool renderers, providers (chat, classifier and image), virtual models, MCP servers, the event bus, and every extension event. [Extension events](/docs/extensions/events/) lists the events and when each one fires. `ctx.ui` dialogs, widgets and custom components show in the terminal UI and over RPC.

Pi 1.5.0's additions work too. Tool, message and entry renderers get `keyText()` and `keyHint()`, which read the terminal UI's current keybindings. An inline `ctx.ui.custom()` component with a `blocked: { kind, message }` option reports pi as blocked in its OSC 7501 program status while it is open (overlays ignore the option). Markdown transformers see `messageId` and `timestamp` for messages restored from the session file.

Dialogs and inline custom components stack in the editor slot, as in Pi 1.4.0. A `select`, `confirm`, `input` or `editor` dialog, or an inline `ctx.ui.custom()` component, hides the one that is showing. When it closes, the one below shows again. Every dialog still settles when it is answered, times out or is aborted, and the blocked program status follows the dialog that is showing. If an extension calls `ctx.ui.setEditorComponent()` while a dialog is open, the dialog keeps its place and focus. The new editor shows when the last dialog closes.

`ctx.ui.custom(factory, { overlay: true, overlayOptions, onHandle })` opens the component as an overlay on top of the screen, as in Pi 1.3.0. Its `done()` hides the overlay it opened, even when another overlay opened above it. The overlay options are `width`, `minWidth`, `maxHeight`, `anchor`, `offsetX`, `offsetY`, `row`, `col`, `margin`, `nonCapturing` and `visible`. Without `overlayOptions` the overlay takes the component's `width`. A function `overlayOptions` is called once after the component factory settles. The `visible(columns, rows)` callback remains in Node and is evaluated with current terminal dimensions. `onHandle` receives `hide`, `setHidden`, `isHidden`, `focus`, `unfocus`, `isFocused` and `getBounds`. Mutations settle on the next UI turn; getters use the latest UI snapshot and reflect the handle's pending mutations. Parameterless `unfocus()` is supported. A handle becomes inert after its component closes or the extension runtime is invalidated.

A component's `capturesViewportInput` (Pi 1.5.0) reaches the full-screen viewport, so a custom component or an extension editor with a menu open gets the scroll keys. PiSharp reads the value after each render and each key. The key hints keep the terminal UI's keybindings after `/reload`.

`ctx.ui.setTheme()` switches the theme in the terminal UI and saves it, as in Pi. `pi.sendMessage(…, { triggerTurn: true })` starts its turn at once, also from inside a command's handler. Packages loaded with `-e` add their skills, prompt templates and themes as well as their extensions.

Pi's built-in extensions (`mcp`, `llama.cpp`, `codemode` and `tool-search`) are extensions here too, and an extension that registers its own `codemode` or `tool_search` tool or `/mcp` command replaces the built-in one. See [Built-in extensions](/docs/reference/configuration/#built-in-extensions).

The [Parity](/parity/) page lists the differences that remain.
