---
title: Sessions
description: Where PiSharp keeps conversations, and how to resume, branch, fork, compact, export and share them.
---

A session is one conversation, saved as a JSONL file in Pi's format (version 3). PiSharp stores sessions where Pi does, so Pi and PiSharp list and resume each other's sessions. Each entry in the file has a parent, so a session is a tree: going back to an earlier point and continuing makes a new branch, and the old one stays. Pi's [Sessions](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/sessions.md) page applies.

## Where sessions live

Sessions are saved under `~/.pi/agent/sessions/`, in one folder per working folder. The folder name is the working folder's path with `/`, `\` and `:` turned into `-`, between `--` marks. The file name is the start time and the session id:

```text
~/.pi/agent/sessions/--home-me-project--/2026-10-28T09-14-03-512Z_0199a3c4-....jsonl
~/.pi/agent/sessions/--C--Users-me-project--/2026-10-28T09-14-03-512Z_0199a3c4-....jsonl
```

A new session's file appears once the first answer arrives, so starting and quitting leaves nothing behind. To keep sessions elsewhere, use the first of these that applies:

1. `--session-dir <dir>` for one run.
2. The `PI_CODING_AGENT_SESSION_DIR` environment variable.
3. The `sessionDir` setting.

`--no-session` keeps the session in memory and writes no file.

## Start, continue and resume

| Option | What it does |
| --- | --- |
| (none) | Starts a new session. |
| `-c`, `--continue` | Opens the newest session of this folder, or starts a new one if there is none. |
| `-r`, `--resume` | Opens the session picker. `/resume` does the same inside the terminal UI. |
| `--session <path\|id>` | Opens a session by file path, id or id prefix. |
| `--session-id <id>` | Opens the session of this folder with exactly this id, or creates it with that id. |
| `--fork <path\|id>` | Copies a session into a new one in this folder and opens the copy. |
| `-n`, `--name <name>` | Names the session. `/name` does the same later. |

`--session` looks for a match in this order:

1. A value with `/` or `\`, or ending in `.jsonl`, is a file path. A path that doesn't exist yet becomes a new session there.
2. An exact id among this folder's sessions, then an id prefix.
3. An exact id or prefix among all sessions. A match from another folder asks `Fork this session into current directory? [y/N]` and opens a copy here.

A session runs in the folder it was started in. If that folder no longer exists, the terminal UI asks whether to continue in the current folder; print, JSON and RPC modes fail.

`--session-id` takes letters, digits, `-`, `_` and `.`, starting and ending with a letter or digit. When the id is new, PiSharp warns `No project session found with id '<id>'; creating a new session with that id.` It is handy for scripts that want a stable name per task:

```sh
pisharp --session-id fix-1234 -p "Continue the fix for issue 1234"
```

The session picker lists this folder's sessions and can show all of them. `Ctrl+P` shows paths, `Ctrl+S` changes the sort, `Ctrl+N` shows only named sessions, `Ctrl+R` renames and `Ctrl+D` deletes. [Command line](/docs/reference/cli/#sessions) lists which session options can't be combined.

## The session tree

`/tree` shows the whole session as a tree, with the current point marked. Pick an entry to move there; your next message continues from that point, in a new branch. When you pick one of your own messages, its text comes back into the editor so you can change it and send it again. Pressing `Esc` twice in an empty editor also opens `/tree`, unless `doubleEscapeAction` says otherwise.

In the tree:

- `Ctrl+Left` and `Ctrl+Right` (or `Alt+Left` and `Alt+Right`) fold and unfold branches.
- `Shift+L` puts a label on an entry, and `Shift+T` shows when labels were set.
- Filters: `Ctrl+D` default, `Ctrl+T` without tool results, `Ctrl+U` your messages only, `Ctrl+L` labeled entries, `Ctrl+A` everything. `Ctrl+O` cycles them. The `treeFilterMode` setting picks the one `/tree` opens with.

### Branch summaries

When you move to another point, PiSharp asks **Summarize branch?**:

- **No summary** moves without one.
- **Summarize** has the model summarize the branch you are leaving and adds the summary at the new point, so the work isn't lost from the context.
- **Summarize with custom prompt** asks for instructions first.

`Esc` cancels a running summary. Set `branchSummary.skipPrompt` to `true` to skip the question and never summarize. `branchSummary.reserveTokens` (default 16384) keeps room for the summary prompt and answer.

## Fork and clone

| Command | What it does |
| --- | --- |
| `/fork` | Lists your earlier messages. Pick one to start a new session that ends just before it, with its text in the editor. |
| `/clone` | Copies the session as it is now into a new session. |
| `--fork <path\|id>` | Copies a whole session into a new one when PiSharp starts. Add `--session-id` to choose the new id. |

The original session is never changed.

## Compaction

Long sessions outgrow the model's context window. Compaction replaces older messages with a summary written by the model and keeps the recent ones as they are. PiSharp compacts as Pi does, in every mode:

- **Automatically**, when the context passes the model's context window minus `compaction.reserveTokens` (default 16384).
- **After an overflow**: when a provider rejects a prompt as too long, PiSharp compacts and retries.
- **By hand**, with `/compact`, optionally followed by instructions for the summary, such as `/compact keep the API decisions`.

`compaction.keepRecentTokens` (default 20000) is how much recent conversation stays verbatim. `compaction.modelOverrides` sets both values for single models. `compaction.enabled` turns automatic compaction off; `/settings` and the RPC `set_auto_compaction` command change it. The full history stays in the file: compaction only changes what the model sees. See [Settings](/docs/reference/settings/#sessions-queue-and-compaction).

## Export and share

| Command | What it does |
| --- | --- |
| `/export [path]` | Writes the session as an HTML page, or as JSONL when the path ends in `.jsonl`. Without a path the HTML goes to the current folder. |
| `pisharp --export <file> [output]` | Writes a session file as HTML without starting a session. The default output is `pi-session-<name>.html` in the current folder. |
| `/import <path.jsonl>` | Replaces the current session with a session file, after you confirm. |
| `/share` | Uploads the session and prints a link. |

The HTML page is Pi's export page, drawn in your current theme.

`/share` uses the GitHub CLI. Install [`gh`](https://cli.github.com/) and run `gh auth login` first. PiSharp uploads the HTML page as a secret gist and prints two links: the gist, and a viewer link made of `PI_SHARE_VIEWER_URL` (default `https://pi.dev/session/`), `#` and the gist id. Point `PI_SHARE_VIEWER_URL` at your own viewer to keep links off pi.dev. If you are signed in to Radius, `/share` uploads to Radius instead, visible to your organization, as Pi does.

## Copy and migrate session files

`pisharp session` has three commands for session files. They take absolute paths and never change the source.

| Command | What it does |
| --- | --- |
| `pisharp session copy-inspect --source <file>` | Reports whether the file can be copied, and in which formats. |
| `pisharp session copy --source <file> --destination <new file> --format <format>` | Writes a copy as `native-exact`, `current-jsonl` or `native-archive-exact`. |
| `pisharp session migrate --source <file> --destination <new file>` | Writes a copy of an older session in the current format. |

Each takes `--id-plan <file>`, a JSON array of entry ids for old sessions whose entries have none. Files are limited to 8 MiB and 10,000 records. See [session](/docs/reference/cli/#session).

## Scripted sessions

The other `pisharp session` verbs (`create`, `prompt`, `resume`, `inspect`, `tree`, `history`, `rpc`, `chat`, `terminal`, `list`, `context-edit`, `compact` and `branch-summary`) work on one session file at an explicit path, with the `explicit` tool policy. They suit tests and automation. Most of them run against scripted provider responses instead of a real model. [Command line](/docs/reference/cli/#session) lists their options. To embed sessions in your own program, see [Sessions](/docs/embed/sessions/).

## Durable sessions

`pisharp durable` keeps its sessions separately, in `~/.pi/agent/experimental/durable-sessions/`, in a crash-safe format of its own. `pisharp` doesn't list or open them. See [Pi Durable](/docs/guides/durable/).
