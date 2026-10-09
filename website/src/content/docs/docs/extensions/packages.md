---
title: Packages
description: Install Pi packages from npm, git or a local folder.
---

Pi packages bundle extensions, skills, prompt templates and themes. PiSharp installs and loads them as Pi does. See Pi's [Packages](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/packages.md) page for how to build one.

## Install

```sh
pisharp install npm:@example/pi-tools@1.0.0
pisharp install git:github.com/example/pi-tools@v1
pisharp install https://github.com/example/pi-tools
pisharp install ./local-package
```

`install` adds the package to `packages` in `~/.pi/agent/settings.json`. With `-l` it goes into the project's `.pi/settings.json` instead, and loads only when you trust the project.

| Source | Installed in |
| --- | --- |
| `npm:` | `~/.pi/agent/npm/node_modules`, or `.pi/npm` with `-l` |
| `git:`, `https://`, `ssh://` | `~/.pi/agent/git/<host>/<path>`, or `.pi/git` with `-l` |
| Local path | Loaded in place, not copied |

PiSharp uses your `npm` (or the `npmCommand` setting) and `git`.

## Manage

| Command | What it does |
| --- | --- |
| `pisharp list` | Lists installed packages. |
| `pisharp remove <source>` | Removes a package and its settings entry. `uninstall` is the same. |
| `pisharp update --extensions` | Updates installed packages. Pinned versions stay pinned. |
| `pisharp update <source>` | Updates one package. |
| `pisharp config` | Opens a screen to turn single resources of a package on or off. Tab switches between your settings and the project's. |

To try a package without installing it, load it for one run with `-e npm:@example/pi-tools`.

A package can hold TypeScript extensions, C# extensions (a folder with a `pisharp-extension.json`), skills, prompt templates and themes.
