---
title: Interactive mode
description: The terminal UI, slash commands, key bindings and themes.
---

`pisharp` opens Pi's terminal UI on Windows, Linux and macOS. It works as Pi's [terminal UI](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/tui.md) does: the same editor, key bindings, selectors, footer, fullscreen and regular modes, inline images, Mermaid diagrams and syntax highlighting.

## Prompts, steering and shell commands

Type a message and press `Enter`. While the agent works:

- `Enter` sends a steering message, which reaches the agent at its next step.
- `Alt+Enter` (`Ctrl+Q` on Windows) queues a follow-up, which waits until the agent stops.
- `Alt+Up` (`Alt+Q` on Windows) brings queued messages back into the editor.
- `Esc` aborts the run and puts queued messages back into the editor.

The `steeringMode` and `followUpMode` settings decide whether queued messages go one at a time or all at once. See [Settings](/docs/reference/settings/#sessions-queue-and-compaction).

`!command` runs a shell command and adds its output to the context. `!!command` runs it without adding the output. `Esc` stops a running command.

`@` in the editor completes file paths. `Ctrl+V` (`Alt+V` on Windows) pastes an image, or files on macOS.

## Slash commands

Type `/` to search the commands of the current session. PiSharp has all of Pi's built-in commands and one of its own, `/tier`.

| Command | What it does |
| --- | --- |
| `/settings` | Opens the settings menu. |
| `/model [provider/model]` | Opens the model selector, or switches to the model you name. |
| `/scoped-models` | Chooses the models `Ctrl+P` cycles through, saved as `enabledModels`. |
| `/thinking [level]` | Opens the thinking selector, or sets the level for this session. |
| `/tier [tier [default]]` | Shows or sets the service tier (experimental, unreleased). See below. |
| `/login [provider]` | Signs in to a provider, or saves its API key. |
| `/logout` | Removes a sign-in. |
| `/new` | Starts a new session. |
| `/resume` | Opens the session picker. |
| `/name [name]` | Sets the session's display name, or shows it. |
| `/session` | Shows the session's name, file, id, message counts, tokens and cost. |
| `/tree` | Opens the session tree to move to another point. See [Sessions](/docs/guides/sessions/#the-session-tree). |
| `/fork` | Starts a new session from an earlier message of this one. |
| `/clone` | Copies the session, as it is now, into a new session. |
| `/compact [instructions]` | Compacts the context now, with optional instructions for the summary. |
| `/export [path]` | Writes the session as HTML, or as JSONL when the path ends in `.jsonl`. |
| `/import <path.jsonl>` | Replaces the current session with a session file, after you confirm. |
| `/share` | Uploads the session as a secret GitHub gist and prints a link. See [Sessions](/docs/guides/sessions/#export-and-share). |
| `/copy` | Copies the last answer to the clipboard. |
| `/bug [description]` | Writes a bug report and opens a GitHub issue for it. See below. |
| `/trust` | Saves a trust decision for this project. |
| `/reload` | Reloads key bindings, extensions, skills, prompt templates, themes and context files. |
| `/hotkeys` | Lists the key bindings in effect. |
| `/changelog` | Shows the changelog. |
| `/quit` | Quits. |
| `/mcp` | Manages MCP servers, while the `mcp` [built-in extension](/docs/reference/configuration/#built-in-extensions) is on. See [MCP](/docs/guides/mcp/). |
| `/llama` | Manages llama.cpp models, while the `llama.cpp` built-in extension is on. See [llama.cpp](/docs/guides/llama-cpp/). |

Extensions, prompt templates and skills add their own commands, as in Pi. Skills appear as `/skill:<name>` while `enableSkillCommands` is on. Extensions can't take the names `help`, `quit`, `exit`, `reload`, `settings`, `trust` or `permissions`. Pi's [Slash commands](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/slash-commands.md) page describes each built-in command.

PiSharp also keeps Pi's hidden commands. `/debug` writes the rendered screen lines and the context messages to `pi-debug.log` in the agent folder, for bug reports.

:::caution[/tier is unreleased]
`/tier` is not in any release yet and needs a CLI built from source. Without an argument it shows the current tier and the choices: `auto`, `normal`, `economy`, `priority`, `fast` and `ultrafast`. `/tier <tier>` applies from the next request. `/tier <tier> default` also saves it as the `serviceTier` setting, and `/tier none` or `/tier clear` removes the tier. See [Service tiers](/docs/guides/service-tiers/).
:::

:::note[/bug]
`/bug` builds Pi's report, with the same prompts, zip and optional model-written summary, but never uploads it. Choose **Open GitHub Issue**: PiSharp writes `pi-bug-report-<id>.zip` to the current folder and opens a prefilled issue on [PiSharp's GitHub](https://github.com/jkelly/PiSharp/issues) with your description, the summary and your PiSharp version, OS, runtime and model. Attach the zip to the issue before you submit it, since a link can't carry files. If no browser can open, for example over SSH, PiSharp prints the zip's path and the issue link instead. **Export as Zip** writes only the zip.
:::

## Settings

`/settings` has the same items as Pi's: theme, `tuiMode` (fullscreen or regular), `quietStartup`, output padding, images, thinking display, Mermaid rendering and the rest. Changes are saved to `~/.pi/agent/settings.json`. [Settings](/docs/reference/settings/) lists every key.

## Key bindings

Key bindings follow Pi v1.2.0. `/hotkeys` lists the ones in effect, editor keys included. On Windows and in WSL, PiSharp uses Pi's Windows defaults, shown in their own column.

| Action id | Key | On Windows and WSL | What it does |
| --- | --- | --- | --- |
| `app.interrupt` | `Esc` | | Cancels or aborts. Twice in an empty editor opens `/tree` (see `doubleEscapeAction`). |
| `app.clear` | `Ctrl+C` | | Clears the editor. Twice quickly quits. |
| `app.exit` | `Ctrl+D` | | Quits when the editor is empty. |
| `app.suspend` | `Ctrl+Z` | none | Suspends PiSharp to the background. |
| `app.thinking.cycle` | `Shift+Tab` | | Cycles the thinking level. |
| `app.model.cycleForward` | `Ctrl+P` | | Next model. |
| `app.model.cycleBackward` | `Shift+Ctrl+P` | `Alt+P` | Previous model. |
| `app.model.select` | `Ctrl+L` | | Opens the model selector. |
| `app.tools.expand` | `Ctrl+O` | | Expands or collapses tool output. |
| `app.thinking.toggle` | `Ctrl+T` | | Shows or hides thinking blocks. |
| `app.editor.external` | `Ctrl+G` | | Opens the message in your external editor. |
| `app.message.copy` | `Ctrl+X` | | Copies the selection or the last answer. |
| `app.message.followUp` | `Alt+Enter` | `Ctrl+Q` | Queues a follow-up. |
| `app.message.dequeue` | `Alt+Up` | `Alt+Q` | Restores queued messages. |
| `app.clipboard.pasteImage` | `Ctrl+V` | `Alt+V` | Pastes an image, files on macOS, or text. |
| `app.session.new`, `app.session.tree`, `app.session.fork`, `app.session.resume` | none | none | `/new`, `/tree`, `/fork` and `/resume`, if you bind them. |

The selectors have their own keys:

| Where | Keys |
| --- | --- |
| `/thinking` | `Ctrl+S` saves the chosen level as `defaultThinkingLevel`. |
| `/tree` | `Ctrl+Left`/`Alt+Left` folds or moves up, `Ctrl+Right`/`Alt+Right` unfolds or moves down, `Shift+L` edits a label, `Shift+T` shows label times. Filters: `Ctrl+D` default, `Ctrl+T` no tools, `Ctrl+U` user messages, `Ctrl+L` labeled, `Ctrl+A` all, `Ctrl+O` and `Shift+Ctrl+O` cycle. |
| Session picker | `Ctrl+P` shows paths, `Ctrl+S` changes the sort, `Ctrl+N` shows named sessions only, `Ctrl+R` renames, `Ctrl+D` deletes, `Ctrl+Backspace` deletes when the search is empty. |
| `/scoped-models` | `Ctrl+S` saves, `Ctrl+A` enables all, `Ctrl+X` clears all, `Ctrl+P` toggles a provider, `Alt+Up` and `Alt+Down` reorder. |

Change bindings in `~/.pi/agent/keybindings.json`, keyed by action id, with a key or a list of keys:

```json
{
  "app.model.select": "ctrl+m",
  "app.session.tree": ["ctrl+shift+t"]
}
```

Pi's older binding names, such as `selectModel`, are migrated. `/reload` applies changes.

## Themes

PiSharp ships Pi's `system`, `dark` and `light` themes. `system` is the default and follows your terminal's colors.

Custom themes are JSON files in Pi's format, in `~/.pi/agent/themes/` or the project's `.pi/themes/`. Choose one in `/settings`, with the `theme` setting, or with `--use-theme <name>` for one run. `--theme <path>` loads a theme file for one run. PiSharp reloads the current custom theme when you save its file.

See Pi's [Themes](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/themes.md) page for the format.

## Clipboard

`/copy` and pasting work as in Pi. On Linux, PiSharp copies with `wl-copy` on Wayland and `xclip` or `xsel` on X11, as Pi does. Pasting tries `wl-paste`, `xclip` and `xsel`, then reads the X11 clipboard itself through `libxcb`, like Pi's built-in helper, so pasting text and images works on X11 without those programs.

Over SSH or Mosh, PiSharp also copies with OSC 52, so the copy reaches your local terminal. Set `PI_CLIPBOARD_OSC52=1` for remote sessions that leave no SSH markers, such as a multiplexer attach.

## What's New

After an update, PiSharp shows the new entries from its changelog, as Pi does. `/changelog` shows them all, and `collapseChangelog` shortens the post-update view.
