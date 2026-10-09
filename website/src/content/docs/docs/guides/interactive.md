---
title: Interactive mode
description: The terminal UI, slash commands, key bindings and themes.
---

`pisharp` opens Pi's terminal UI on Windows, Linux and macOS. It works as Pi's [terminal UI](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/tui.md) does: the same editor, key bindings, selectors, footer, fullscreen and regular modes, inline images, Mermaid diagrams and syntax highlighting.

## Slash commands

Type `/` to search the commands of the current session. PiSharp has all of Pi's built-in commands:

| Area | Commands |
| --- | --- |
| Models and settings | `/settings`, `/model`, `/thinking`, `/scoped-models`, `/login`, `/logout` |
| Sessions | `/new`, `/resume`, `/name`, `/session`, `/tree`, `/fork`, `/clone`, `/compact`, `/import` |
| Export and share | `/copy`, `/export`, `/share`, `/bug` |
| Runtime and project | `/trust`, `/reload`, `/hotkeys`, `/changelog`, `/quit` |
| MCP | `/mcp`, when MCP is on |

Extensions, prompt templates and skills add their own commands, as in Pi. Pi's [Slash commands](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/slash-commands.md) page describes each one.

:::note[/bug]
`/bug` builds Pi's report, with the same prompts, zip and optional model-written summary, but never uploads it. Choose **Open GitHub Issue**: PiSharp writes `pi-bug-report-<id>.zip` to the current folder and opens a prefilled issue on [PiSharp's GitHub](https://github.com/jkelly/PiSharp/issues) with your description, the summary and your PiSharp version, OS, runtime and model. Attach the zip to the issue before you submit it, since a link can't carry files. If no browser can open, for example over SSH, PiSharp prints the zip's path and the issue link instead. **Export as Zip** writes only the zip.
:::

`!command` runs a shell command and adds its output to the context. `!!command` runs it without adding the output.

## Settings

`/settings` has the same items as Pi's: theme, `tuiMode` (fullscreen or regular), `quietStartup`, output padding, images, thinking display, Mermaid rendering and the rest. Changes are saved to `~/.pi/agent/settings.json`.

## Key bindings

Key bindings follow Pi v1.1.0. Change them in `~/.pi/agent/keybindings.json`. `/hotkeys` lists the ones in effect.

## Themes

PiSharp ships Pi's `system`, `dark` and `light` themes. `system` is the default and follows your terminal's colors.

Custom themes are JSON files in Pi's format, in `~/.pi/agent/themes/` or the project's `.pi/themes/`. Choose one in `/settings`, with the `theme` setting, or with `--use-theme <name>` for one run. `--theme <path>` loads a theme file for one run. PiSharp reloads the current custom theme when you save its file.

See Pi's [Themes](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/themes.md) page for the format.

## Clipboard

`/copy` and pasting work as in Pi. On Linux, PiSharp uses `wl-copy` and `wl-paste` on Wayland, and `xclip` or `xsel` on X11.

Pi also has a built-in X11 clipboard helper, which it uses when those programs are missing. PiSharp has none, so on X11 install `xclip` (or `xsel` for text only).

## What's New

After an update, PiSharp shows the new entries from its changelog, as Pi does. `/changelog` shows them all.
