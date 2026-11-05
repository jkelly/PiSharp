---
title: Packages
description: Install Pi packages from npm, git or a local folder.
---

Pi packages bundle extensions, skills, prompt templates and themes. PiSharp installs and loads them as Pi does. See Pi's [Packages](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/packages.md) page for how to build one.

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

If a configured package fails to install at startup, for example because npm cannot reach the registry, PiSharp warns about it, skips that package's resources, and keeps running. It tries to install the package again on the next start or `/reload`. `pisharp install` and `pisharp update --extensions` still report the failure and exit non-zero.

## Manage

| Command | What it does |
| --- | --- |
| `pisharp list` | Lists installed packages. |
| `pisharp remove <source>` | Removes a package and its settings entry. `uninstall` is the same. |
| `pisharp update --extensions` | Updates installed packages. Pinned versions stay pinned. |
| `pisharp update <source>` | Updates one package. |
| `pisharp config` | Opens a screen to turn single resources of a package, or Pi's [built-in extensions](/docs/reference/configuration/#built-in-extensions), on or off. Tab switches between your settings and the project's. |

To try a package without installing it, load it for one run with `-e npm:@example/pi-tools`. Its extensions, skills, prompt templates and themes all load, as in Pi.

A package can hold TypeScript extensions, C# extensions (a folder with a `pisharp-extension.json`), skills, prompt templates and themes.

## Show a changelog

After a package update, PiSharp shows what changed, as Pi 1.5.0 does. On the next fresh launch, it reads the package's changelog and shows the entries newer than the last version you saw under "Extension Updates", after PiSharp's own "What's New". The first time PiSharp sees a package, it records the version and shows nothing. Resumed and continued sessions show nothing.

This applies to packages that load at least one extension. Packages that only hold skills, prompt templates or themes show no changelog.

PiSharp reads `CHANGELOG.md` in the package root by default. Point `pi.changelogPath` at another file inside the package, or set it to `""` to turn the changelog off:

```json
{
  "name": "my-pi-package",
  "version": "1.2.0",
  "pi": {
    "changelogPath": "./docs/CHANGELOG.md"
  }
}
```

A path that leaves the package, or isn't a file, is ignored. Use version headings such as `## [1.2.0]` or `## 1.2.0 - 2026-01-01`. Entries are matched against the `version` in `package.json`, so a git package shows a changelog only when that field goes up. `collapseChangelog` shortens the section to one line per package, such as `Updated my-pi-package to v1.2.0.` `/changelog` doesn't show package changelogs.

A `pi` object that holds only metadata, such as `changelogPath`, doesn't count as a resource list: PiSharp still finds the package's `extensions`, `skills`, `prompts` and `themes` folders.
