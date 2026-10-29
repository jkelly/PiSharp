---
title: Built-in tools
description: The parameters, limits and output of each built-in tool, and the --tools grammar.
---

PiSharp has Pi v1.2.0's built-in tools with the same names, parameters, limits and messages. A session starts with `read`, `bash`, `edit` and `write`. `grep`, `find` and `ls` are registered but off until you select them. `tool_search` and `codemode` come from [built-in extensions](/docs/reference/configuration/#built-in-extensions).

This page describes the tools under the default `pi` [tool policy](/docs/reference/configuration/#tool-policy), where they take any path, as in Pi. Relative paths resolve against the working folder, and a leading `~` against your home folder.

## Limits shared by the tools

Tool output that goes to the model is cut at 2000 lines or 50 KB (51,200 bytes), whichever comes first. Each tool says what it cut and how to see more.

| Tool | Keeps | Notice |
| --- | --- | --- |
| `read` | The first lines from `offset`. | `[Showing lines X-Y of N. Use offset=Z to continue.]` |
| `bash` | The last lines. | `[Showing lines X-Y of N. Full output: <file>]` |
| `grep`, `find`, `ls` | The first results. | A limit notice, such as `100 matches limit reached. Use limit=200 for more, or refine pattern` |

## read

Reads a text file or an image.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `path` | string | yes | File to read. |
| `offset` | number | no | First line to read, counting from 1. |
| `limit` | number | no | Most lines to read. |

- Text past the line or byte limit ends with a notice: `[Showing lines 1-2000 of 5000 (50.0KB limit). Use offset=2001 to continue.]`, or `[3000 more lines in file. Use offset=2001 to continue.]` when `limit` stopped it.
- A first line longer than 50 KB returns a hint to read it with `sed -n '<line>p' <file> | head -c 51200` instead.
- An `offset` past the end is an error: `Offset 120 is beyond end of file (100 lines total)`.
- The CLI reads files of up to 64 MiB. A larger file fails with `File content exceeds the supported input limit.`

### Images

`read` recognizes PNG, JPEG, GIF, WebP and BMP files by their content, not their extension. PiSharp decodes them with SkiaSharp, where Pi uses Photon, and follows Pi's steps:

1. It applies the EXIF orientation.
2. An image within 2000x2000 whose base64 data is under 4.5 MB goes to the model unchanged.
3. Otherwise, with `images.autoResize` on (the default), it fits the image in 2000x2000 and tries PNG, then JPEG at quality 80, 85, 70, 55 and 40, and keeps the first result under 4.5 MB.
4. If none fits, it shrinks the image by a quarter and tries again.

The result carries the image and a note about any resize. An image that can't fit is left out with `[Image omitted: could not be resized below the inline image size limit.]`. If the model doesn't take images, the note is `[Current model does not support images. The image will be omitted from this request.]`. `images.blockImages` keeps every image away from the provider. See [Settings](/docs/reference/settings/#tools-and-shell).

## write

Creates or replaces a file, and creates missing parent folders.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `path` | string | yes | File to write. |
| `content` | string | yes | The whole new content. |

Pi has no size limit. In the CLI, one `write` call's arguments can hold up to 8 MiB (8,388,608 characters) of JSON, so one call writes a little under 8 MiB of plain text. Characters that JSON escapes, such as `<`, `&` or accented letters, count up to six characters each. Write a larger file in parts, for example with `bash`.

:::caution[64 KiB in released builds]
PiSharp 1.2.0.1 and earlier limit `write` to 65,536 UTF-8 bytes of content and 65,536 characters of arguments; larger content fails with `File content exceeds the supported input limit.` The larger limit is not in any release yet and needs a CLI built from source.
:::

## edit

Replaces exact text in one file.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `path` | string | yes | File to edit. |
| `edits` | array of `{ oldText, newText }` | yes | One or more replacements. |

Pi's older forms also work: a single top-level `oldText` and `newText`, and `edits` sent as a JSON string.

Matching follows Pi:

- Every `oldText` is matched against the original file, not against the result of earlier edits.
- Exact matching comes first. If that fails, PiSharp matches again with both sides normalized: Unicode NFKC, trailing whitespace removed from each line, curly quotes made straight, Unicode dashes made `-` and special spaces made plain spaces. Lines outside the edited regions keep their original text.
- Each `oldText` must match exactly once, and edits must not overlap.
- Line endings are kept: a CRLF file stays CRLF. A byte order mark is kept too.

The errors are Pi's:

| Error | Cause |
| --- | --- |
| `Could not find the exact text in <path>. The old text must match exactly including all whitespace and newlines.` | No match, even normalized. |
| `Found N occurrences of the text in <path>. The text must be unique. Please provide more context to make it unique.` | More than one match. |
| `edits[i] and edits[j] overlap in <path>. Merge them into one edit or target disjoint regions.` | Two edits touch the same text. |
| `oldText must not be empty in <path>.` | An empty `oldText`. |
| `No changes made to <path>. The replacement produced identical content. ...` | The edit changes nothing. |

With several edits, the messages name the edit, such as `Could not find edits[2] in <path>.` The CLI edits files of up to 64 MiB, and the diff shown in the terminal UI is cut at 4096 characters.

## bash

Runs a shell command in the working folder.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `command` | string | yes | The command. |
| `timeout` | number | no | Seconds before the command is stopped. No default: a command runs until it ends. |

### Shell

PiSharp picks the shell as Pi does:

| Platform | Order |
| --- | --- |
| Windows | The `shellPath` setting, then Git Bash under `Program Files` or `Program Files (x86)`, then `bash.exe` on `PATH`. |
| Linux, macOS | The `shellPath` setting, then `/bin/bash`, then `bash` on `PATH`, then `sh`. |

The command runs with `-c`. The old WSL `bash.exe` gets it on stdin with `-s` instead. `shellCommandPrefix` is put on its own line before the command. The command inherits your environment, with `<agentDir>/bin` first on `PATH` and the session variables `PI_SESSION_ID`, `PI_SESSION_FILE`, `PI_PROVIDER`, `PI_MODEL` and `PI_REASONING_LEVEL` set.

### Output

- The model sees the last 2000 lines or 50 KB of combined stdout and stderr. When output is cut, the full output is saved to `pi-bash-<id>.log` in the system temp folder and the notice names it.
- A non-zero exit code makes the result an error that ends with `Command exited with code N`.
- A timeout ends with `Command timed out after N seconds`, and an abort with `Command aborted`.
- The structured result, which code mode scripts get, is `{ output, truncated, full_output_path?, exit_code, wall_time_seconds }`.

## grep

Searches file contents with ripgrep (`rg`). Off by default.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `pattern` | string | yes | A regular expression, or a literal string with `literal`. |
| `path` | string | no | Folder or file to search. Default: the working folder. |
| `glob` | string | no | File filter, such as `*.ts` or `**/*.spec.ts`. |
| `ignoreCase` | boolean | no | Case-insensitive search. Default `false`. |
| `literal` | boolean | no | Treat `pattern` as plain text. Default `false`. |
| `context` | number | no | Lines to show before and after each match. Default `0`. |
| `limit` | number | no | Most matches. Default `100`. |

PiSharp runs `rg --json --line-number --color=never --hidden`, adding `--ignore-case`, `--fixed-strings` and `--glob` as asked. It respects `.gitignore`. Lines longer than 500 characters are cut, with a notice to use `read` for the full line.

## find

Finds files by glob pattern with `fd`. Off by default.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `pattern` | string | yes | Glob, such as `*.ts`, `**/*.json` or `src/**/*.spec.ts`. |
| `path` | string | no | Folder to search. Default: the working folder. |
| `limit` | number | no | Most results. Default `1000`. |

PiSharp runs `fd --glob --color=never --hidden --max-results <limit>`. Outside a git repository it adds `--no-require-git`, so `.gitignore` files still apply. A pattern with a `/` matches the whole path (`--full-path`), with `**/` added in front when needed. Paths are relative to the search folder.

## ls

Lists a folder.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `path` | string | no | Folder to list. Default: the working folder. |
| `limit` | number | no | Most entries. Default `500`. |

Entries are sorted, folders end with `/`, and dot files are included.

## ripgrep and fd

`grep` and `find` use `rg` and `fd` from `<agentDir>/bin` or your `PATH`. If one is missing, PiSharp downloads it from its GitHub releases into `<agentDir>/bin` the first time it's needed, as Pi does. `PI_OFFLINE=1` or `--offline` stops the download.

## tool_search

Finds tools the model can't see yet and turns them on. It comes from the `builtin:tool-search` extension.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `query` | string | yes | What to look for. Must not be empty. |
| `limit` | number | no | Most tools to load. A positive integer, default `8`. |

It ranks registered tools that are hidden from the model, such as MCP tools with `deferred` or `codemode` exposure, with BM25, and activates the matches for the next model call. It is registered off. It turns on when an MCP server has `deferred` tools, or when you select it with `--tools` or `defaultTools`. See [MCP](/docs/guides/mcp/).

## codemode

Runs JavaScript that calls the other tools. It comes from the `builtin:codemode` extension and is registered off.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `code` | string | yes | Raw JavaScript, optionally starting with a `// @options:` line. |

Turn it on with `--tools +codemode` or `"defaultTools": ["+codemode"]`. MCP servers with `codemode` exposure turn it on unless `autoEnableCodemode` is `false` in `mcp.json`. See [Code mode](/docs/guides/codemode/) for the language, globals and limits.

## powershell

There is no `powershell` tool in the PiSharp CLI. Pi v1.2.0 has one for Windows; PiSharp hasn't ported it to the CLI yet, and the [Parity](/parity/) page lists it as deferred. The name is reserved, and the terminal UI can show a `powershell` call from an extension. On Windows, `bash` runs through Git Bash.

## Selecting tools

The command line and the `defaultTools` setting choose which tools exist and which start on.

| Input | Effect |
| --- | --- |
| `--tools a,b` | Allowlist. Only `a` and `b` exist, and both start on. `*` patterns work, such as `'mcp__radius__*'`. |
| `--tools +a,-b` | Changes the default selection: `defaultTools`, or `read,bash,edit,write`. Every other tool still exists. Exact names only. |
| `--tools ""` | No tools. |
| `--exclude-tools a,b` | Denylist. Removes the tools, MCP tools included. Patterns work. |
| `--no-tools` | No tools. With `--tools`, only the tools it names. |
| `--no-builtin-tools` | No built-in tool starts on. Extension tools that start on by default stay on. |
| `--no-mcp` | Leaves out the `mcp` built-in extension, so there are no MCP tools. |
| `defaultTools: ["a", "b"]` | The tools that start on. Other tools still exist and can be turned on later. |
| `defaultTools: ["+a", "-b"]` | Changes the inherited selection. In project settings it adds to your list. |

Rules:

- A `--tools` list can't mix plain names with `+name` or `-name` entries.
- An allowlist keeps MCP tools unless it is empty or one of its entries starts with `mcp__`. MCP tools it keeps without naming are only for `codemode` and `tool_search`.
- Unknown tool names are ignored, as in Pi.
- `/reload` turns on tools newly added to `defaultTools`. Tools removed from it stay on until the next session.

```sh
pisharp --tools read,grep,find,ls -p "Review the code in src/"   # read-only
pisharp --tools +codemode                                      # defaults plus codemode
pisharp --tools read,bash,codemode,'mcp__radius__*'            # one MCP server's tools
pisharp --exclude-tools ask_question                           # one tool off
```

## Tool policy

The tool policy decides how far the built-in tools reach. Pi has no such switch.

| Policy | Effect |
| --- | --- |
| `pi` (default) | As described on this page: any path, any command. |
| `explicit` | Only the paths and commands granted with `--allow-*`. Plain `pisharp` has no grants, so file tools refuse every path and `bash`, `grep` and `find` are left out. |

Set it with `--tool-policy pi|explicit` or the `toolPolicy` setting. The `pisharp session` commands use `explicit` with their [grants](/docs/reference/cli/#grants). Under both policies PiSharp's own session files, extension manifests and approvals stay protected. See [Tool policy](/docs/reference/configuration/#tool-policy).
