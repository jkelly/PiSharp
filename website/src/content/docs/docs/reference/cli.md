---
title: Command line
description: Every pisharp option, mode and subcommand, with exit codes.
---

`pisharp` takes the same command line as Pi v1.2.0, plus a few PiSharp options and commands. Pi's [Command Line](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/cli.md) reference applies. This page lists every option the PiSharp parser accepts, checked against the source of `pisharp --help` and the parser itself.

```sh
pisharp [options] [--] [@files...] [messages...]
pisharp install <source> [-l] [-a | -na]
pisharp remove <source> [-l] [-a | -na]          # alias: uninstall
pisharp update [source|self|pi] [--self | --extensions | --models | --all] [--extension <source>] [-a | -na] [--force]
pisharp list [-a | -na]
pisharp config [-l] [-a | -na]
pisharp auth <print-api-key|print-bearer-token|check> [options]
pisharp mcp <add|remove|list|login|logout> [options]
pisharp session <verb> [options]               # PiSharp only
pisharp durable [--continue] [--tier <tier>]   # PiSharp only, experimental
```

`pisharp <command> --help` prints the help of `install`, `remove`, `uninstall`, `update`, `list`, `config`, `auth` and `mcp`.

## Modes

PiSharp picks one mode per run:

| Mode | When | What it does |
| --- | --- | --- |
| Interactive | No `-p` and no `--mode json\|rpc`, with stdin and stdout on a terminal. | Opens the terminal UI. Messages on the command line become the first prompts. See [Interactive mode](/docs/guides/interactive/). |
| Print | `-p`, or stdin or stdout is redirected. | Runs the prompts, prints the final answer and exits. |
| JSON | `--mode json` | Runs the prompts and writes Pi's JSONL events to stdout. |
| RPC | `--mode rpc` | Speaks Pi's RPC protocol over stdin and stdout. See [RPC](/docs/reference/rpc/). |

`--mode text` is the default and means interactive or print. `--export` and `--list-models` do their job and exit without starting a session.

Piped stdin is added in front of the first prompt. `@path` arguments add a text file or an image to the first prompt; RPC mode refuses them. Automatic compaction runs in every mode, and in print mode a "prompt is too long" error compacts the session and retries, as in Pi.

## Model and provider

| Option | Meaning |
| --- | --- |
| `--model <pattern>` | Model by id, `provider/id` or fuzzy match, with an optional `:<thinking>` suffix, such as `sonnet:high`. |
| `--provider <name>` | Provider to search for `--model`. Requires `--model`. |
| `--models <patterns>` | Comma-separated model patterns for `Ctrl+P` cycling. Globs (`anthropic/*`, `*sonnet*`), fuzzy matches and `:<thinking>` suffixes work. |
| `--thinking <level>` | `off`, `minimal`, `low`, `medium`, `high`, `xhigh` or `max`. An unknown level is a warning, not an error. |
| `--api-key <key>` | API key for this run, instead of the environment or `auth.json`. Requires a model from `--model` or `--models`. |
| `--tier <tier>`, `--service-tier <tier>` | Service tier: `auto`, `normal`, `economy`, `priority`, `fast` or `ultrafast` (experimental, unreleased). See below. |
| `--list-models [search]` | Lists the models you can use, with an optional fuzzy search, then exits. The search can't start with `-` or `@`. |

:::caution[Service tiers are unreleased]
`--tier` is not in any release yet and needs a CLI built from source. It sets the session's tier for every request; `/tier` changes it later and `serviceTier` in settings sets a default. Names are case-insensitive and some aliases work: `default`, `standard`, `off` and `none` mean `normal`; `flex`, `cheap` and `eco` mean `economy`; `prio` means `priority`; `speed` means `fast`; `ultra`, `super`, `superspeed`, `super-speed` and `turbo` mean `ultrafast`. An unknown tier is a warning. See [Service tiers](/docs/guides/service-tiers/).
:::

## Prompt and input

| Option | Meaning |
| --- | --- |
| `-p`, `--print` | Print mode. The next argument is taken as a message, unless it starts with `@`, or with `-` but not `---`. |
| `--mode <mode>` | `text` (default), `json` or `rpc`. Any other value is an error. |
| `--system-prompt <text>` | Replaces the system prompt. If the value is a file path, the file's contents are used. It replaces `SYSTEM.md` discovery. |
| `--append-system-prompt <text>` | Adds text, or a file's contents, to the system prompt. Repeatable. It replaces `APPEND_SYSTEM.md` discovery. |
| `--` | Ends option parsing. Later arguments are messages, or file arguments when they start with `@`. |

Use `--` for a prompt that starts with a dash:

```sh
pisharp -p -- "- Summarize these points"
```

## Sessions

| Option | Meaning |
| --- | --- |
| `-c`, `--continue` | Continues the newest session for this folder. |
| `-r`, `--resume` | Opens the session picker. |
| `--session <path\|id>` | Opens a session file, or a session by id or id prefix. |
| `--session-id <id>` | Opens the project session with this exact id, or creates it. |
| `--fork <path\|id>` | Copies a session into a new one and opens the copy. |
| `--session-dir <dir>` | Folder for session storage and lookup. |
| `--no-session` | Keeps the session in memory only. |
| `-n`, `--name <name>` | Sets the session's display name. An empty name is an error. |
| `--export <file> [output]` | Writes a session file as HTML and exits. The default output is `pi-session-<basename>.html` in the current folder. Prints `Exported to: <path>`. |

These options can't be combined:

- `--fork` with `--session`, `--continue`, `--resume` or `--no-session`.
- `--session-id` with `--session`, `--continue` or `--resume`. With `--fork`, the id must not exist yet.

A session id has letters, digits, `-`, `_` and `.`, and starts and ends with a letter or digit. See [Sessions](/docs/guides/sessions/) for how `--session` finds a session.

## Tools

| Option | Meaning |
| --- | --- |
| `-t`, `--tools <list>` | Comma-separated allowlist of tool names or `*` patterns. A list of only `+name` and `-name` entries changes the defaults instead. |
| `-xt`, `--exclude-tools <list>` | Comma-separated denylist of names or patterns. Applies to every tool, MCP tools included. |
| `-nt`, `--no-tools` | No tools by default, built-in or extension. `--tools` still adds the ones it names. |
| `-nbt`, `--no-builtin-tools` | No built-in tools by default. Extension and custom tools stay on. |
| `--tool-policy <policy>` | `pi` (default) or `explicit`. PiSharp only. |

`--tools` can't mix plain names with `+name` or `-name` entries, and `+`/`-` entries take exact names, not patterns. [Built-in tools](/docs/reference/tools/#selecting-tools) has the full grammar and the tool policy.

```sh
pisharp --tools read,grep,find,ls -p "Review the code in src/"
pisharp --tools +codemode
pisharp --tools read,bash,codemode,'mcp__radius__*'
```

## Extensions and resources

| Option | Meaning |
| --- | --- |
| `-e`, `--extension <path>` | Loads an extension or package for this run, with its skills, prompt templates and themes. `-e builtin:<name>` loads a [built-in extension](/docs/reference/configuration/#built-in-extensions). Repeatable. |
| `-ne`, `--no-extensions` | Turns off discovered and built-in extensions. `-e` still works. |
| `--no-mcp` | No MCP servers and no MCP tools: the `mcp` built-in extension stays out. |
| `--skill <path>` | Loads a skill file or folder. Repeatable. |
| `-ns`, `--no-skills` | Turns off skill discovery and loading. |
| `--prompt-template <path>` | Loads a prompt template file or folder. Repeatable. |
| `-np`, `--no-prompt-templates` | Turns off prompt template discovery and loading. |
| `--theme <path>` | Loads a theme file or folder. Repeatable. |
| `--use-theme <name>` | Sets the interactive theme for this run. Requires a name. |
| `--no-themes` | Turns off theme discovery and loading. |
| `-nc`, `--no-context-files` | Turns off `AGENTS.md` and `CLAUDE.md` loading. |

### Extension flags

Extensions can register their own flags, such as `--plan` from a plan-mode extension. `pisharp --help` lists them under **Extension CLI Flags** once the extension is loaded.

The parser keeps every `--name` it doesn't know. `--name=value` and `--name value` give a string value. A `--name` followed by nothing, or by an argument that starts with `-` or `@`, is a boolean. After the extensions load, a flag that no extension registered is an error, `Unknown option: --name`, and a string flag without a value is an error too. An unknown short option such as `-x` is always an error.

## Terminal UI, trust and network

| Option | Meaning |
| --- | --- |
| `--tui-mode <mode>` | `fullscreen` (default) or `regular`. Any other value is an error. |
| `--verbose` | Shows the full startup output, overriding `quietStartup`. |
| `-a`, `--approve` | Trusts project files for this run. |
| `-na`, `--no-approve` | Ignores project files for this run. |
| `--offline` | No startup network use: no catalog refresh, no version check, no `rg` or `fd` download. Same as `PI_OFFLINE=1`. |
| `-h`, `--help` | Prints the help. |
| `-v`, `--version` | Prints the PiSharp version. |

See [Project trust](/docs/reference/configuration/#project-trust).

## Help and parser differences

The help text and the parser agree, with these exceptions:

- `--service-tier` is an alias of `--tier` that the help doesn't list.
- `--export` takes an optional output path as the next argument, which the help doesn't show.
- **Built-in Tool Names** in the help lists `powershell`. The CLI registers no `powershell` tool; see [Built-in tools](/docs/reference/tools/#powershell).
- The help's environment list leaves out some provider variables, such as `HF_TOKEN`, `COPILOT_GITHUB_TOKEN`, `MINIMAX_CN_API_KEY`, `TYPESAFE_API_KEY`, `RADIUS_API_KEY` and `GOOGLE_CLOUD_API_KEY`. [Environment variables](/docs/reference/environment/) has them all.
- The `durable` line in the help shows only `--continue`. `pisharp durable` also takes `--tier`.

## Packages

These commands work as in Pi. See [Packages](/docs/extensions/packages/).

| Command | What it does |
| --- | --- |
| `install <source> [-l]` | Installs a package and adds it to settings. `-l` uses the project's `.pi/settings.json`. |
| `remove <source> [-l]`, `uninstall <source> [-l]` | Removes a package and its source from settings. |
| `update [source]` | Updates one package, or PiSharp itself with no source. See below. |
| `list` | Lists installed packages from your settings and the project's. |
| `config [-l]` | Opens a terminal UI to turn package resources on and off. `Tab` switches scope. It needs a terminal. |

Sources take Pi's forms: `npm:@foo/bar`, `git:github.com/user/repo`, `git:git@github.com:user/repo`, `https://github.com/user/repo`, `ssh://git@github.com/user/repo` or a local path such as `./local/path`. Every package command takes `-a`/`--approve` or `-na`/`--no-approve`; a project-scope change needs a trusted project.

`update` options:

| Option | Meaning |
| --- | --- |
| `--self`, `self`, `pi` | Updates PiSharp itself. The default with no target. |
| `--extensions` | Updates installed packages only. |
| `--models` | Refreshes the model catalogs only and prints `Model catalogs refreshed`. |
| `--all` | Updates packages, then PiSharp. |
| `--extension <source>` | Updates one package. |
| `--force` | Reinstalls PiSharp even if it is current. Moot, since self-update isn't possible (below). |

`--all`, `--models` and `--extension` can't be combined with each other, with `--self` or `--extensions`, or with a positional source.

PiSharp can't update itself this way. `pisharp update`, `update self` and `update --all` end with `error: pisharp cannot self-update this installation.` and exit 1 (`--all` updates the packages first). Update the tool with:

```sh
dotnet tool update -g PiSharp.Cli
```

## auth

`pisharp auth` resolves credentials as Pi does, for scripts and other programs. Every command needs `--provider`, `--model` or both.

| Command | What it does |
| --- | --- |
| `auth print-api-key [--provider <p>] [--model <m>]` | Prints the API key PiSharp would use. |
| `auth print-bearer-token [--provider <p>] [--model <m>] [--min-expiry <duration>]` | Prints an OAuth bearer token, refreshing it if it expires sooner than `--min-expiry` (default `30m`). Durations take `ms`, `s`, `m` or `h`, such as `30m` or `1h`. |
| `auth check [--provider <p>] [--model <m>] [--json] [--credentials] [--no-refresh]` | Reports whether the provider is ready. |

`check` options:

| Option | Meaning |
| --- | --- |
| `--json` | Prints the result as JSON. |
| `--credentials` | Prints the credential, or includes it in the JSON. |
| `--no-refresh` | Reads a stored OAuth token as it is, without refreshing an expired one. |

`--json`, `--credentials` and `--no-refresh` work only with `check`, and `--min-expiry` only with `print-bearer-token`. `pisharp auth` with no command prints its help.

```sh
pisharp auth print-api-key --provider openai
pisharp auth print-bearer-token --provider openai-codex
pisharp auth check --provider anthropic --json
```

## mcp

`pisharp mcp` changes and checks MCP servers without starting a session. It reads `~/.pi/agent/mcp.json` and, in trusted projects, `.pi/mcp.json`. See [MCP](/docs/guides/mcp/).

| Command | What it does |
| --- | --- |
| `mcp add <server> [options] -- <command> [args...]` | Adds or replaces a stdio server. |
| `mcp add <server> [options] --url <url>` | Adds or replaces a Streamable HTTP server. |
| `mcp remove <server> [-l]` | Removes a server. |
| `mcp list [--json]` | Shows each server's state, tools and errors. Exits 1 if a server fails. |
| `mcp login <server> [--timeout <seconds>]` | Signs in to an OAuth server through the browser. The default time limit is 300 seconds. |
| `mcp logout <server>` | Deletes the server's stored OAuth credentials. |

`add` options:

| Option | Applies to | Meaning |
| --- | --- | --- |
| `-l`, `--local` | add, remove | Uses the project's `.pi/mcp.json` instead of the global file. |
| `--env <KEY=VALUE>` | stdio | Environment variable for the server. Repeatable. |
| `--cwd <dir>` | stdio | Working folder for the server. |
| `--header <KEY=VALUE>` | HTTP | Request header. Repeatable. |
| `--bearer-token-env-var <NAME>` | HTTP | Sends `Authorization: Bearer ${NAME}`. |
| `--oauth-client-id <id>` | HTTP | Pre-registered OAuth client id. |
| `--oauth-client-secret <secret>` | HTTP | OAuth client secret. May be `${NAME}` or `!command`. |
| `--oauth-callback-port <port>` | HTTP | Fixed OAuth callback port. |
| `--oauth-client-name <name>` | HTTP | Client name sent when registering with the OAuth server. |
| `--exposure <mode>` | both | `codemode` (default), `deferred`, `direct` or `hidden`. |
| `--description <text>` | both | What the server offers, shown in the system prompt. |

An HTTP option on a stdio server, or `--env` or `--cwd` on an HTTP server, is an error. `login` and `logout` work only for HTTP servers that use OAuth, not for servers with their own `Authorization` header. The mcp help prints `PiSharp.Cli` as the command name; type `pisharp`.

## session

`pisharp session <verb>` keeps PiSharp's older explicit-path commands. They take absolute paths, use the `explicit` tool policy and reach only what their `--allow-*` options grant. They suit tests and scripted runs. Most of them run against an offline script of authored provider responses (`--offline-script`), so they make no provider request; `rpc`, `chat` and `terminal` also take `--live`. For everyday use, prefer the Pi options above. There is no `session --help`. A bad command line prints one JSON line, a `session_command_result` with `"code":"InvalidArguments"`, and exits with code 2. The tables below list each verb's options.

| Verb | Required | Optional |
| --- | --- | --- |
| `create` | `--session <new JSONL>`, `--workspace <existing folder>` | `--offline-api`, bash grants, startup options |
| `prompt`, `resume` | `--session`, `--workspace`, `--offline-script <JSON>`, `--message <text>` | `--offline-api`, `--offline-images true\|false`, `--leaf <id>` or `--root`, `--allow-read`, `--allow-write`, `--output report\|print\|json`, bash grants, extension options, startup options |
| `inspect`, `tree`, `history` | `--session <JSONL>` | `--leaf <id>` or `--root` |
| `rpc` | `--session <existing JSONL>`, `--workspace`, and `--offline-script <JSON>` or `--live` | `--thinking`, `--offline-api`, `--offline-images`, `--leaf` or `--root`, `--allow-read`, `--allow-write`, bash grants, extension options, `--session-store`, `--session-mode`, `--tool-policy pi\|explicit`, startup options |
| `chat` | `--session <existing JSONL>`, `--workspace`, `--offline-script` | The `rpc` options. Reads cooked commands from stdin (below). |
| `terminal` | `--terminal-preview` with `--session`, `--workspace`, `--offline-script`; or `--live` with `--model`, `--session`, `--workspace` | `--provider`, `--models`, `--session-mode open\|new-lazy\|new-memory`, `--max-output-tokens 1..8192`, the exact tool grants |
| `list` | `--session-store <storeId=folder>` (repeatable) | `--page-size 1..128` (default 32), `--cursor <cursor>`, `--cwd <folder>` |
| `copy-inspect` | `--source <JSONL>` | `--id-plan <JSON>` |
| `copy` | `--source`, `--destination <new JSONL>`, `--format native-exact\|current-jsonl\|native-archive-exact` | `--id-plan` |
| `migrate` | `--source`, `--destination <new JSONL>` | `--id-plan` |
| `context-edit` | `--session`, `--workspace`, `--target <active entry id>`, `--replacement <JSON file>` | `--leaf` or `--root`, `--offline-api` |
| `compact` | `--session`, `--workspace`, `--offline-script` | `--offline-api`, `--leaf` or `--root`, `--keep-recent <tokens>` (default 20000), `--reserve <tokens>` (default 16384), `--focus <text>`, `--first-kept <id>` or `--retain-none`, `--automatic --context-window <tokens>` (default 128000) |
| `branch-summary` | The `compact` options plus `--target <id\|root>` | The `compact` options |

Common values:

- `--offline-api` is `openai-responses` (default), `anthropic-messages` or `openai-completions`. `--offline-images` applies to the last two.
- `--live` takes `--provider`, `--model <pattern>[:<thinking>]`, `--models` and `--max-output-tokens 1..8192`.
- `--session-mode` is `open`, `new-memory` or `new-lazy`. `--session-store` takes up to 32 stores, each `id=absolute folder`.
- `--id-plan` is a JSON file holding an array of entry id strings, for sessions whose entries have no ids.
- `context-edit --replacement` is a JSON file; `null` leaves the content out.

### Grants

| Option | Meaning |
| --- | --- |
| `--allow-read <absolute file>` | Lets `read` open this file. Up to 128. |
| `--allow-write <absolute file>` | Lets `write` create or replace this file. Up to 128. |
| `--allow-bash-command <exact command>` | Lets `bash` run this exact command. 1 to 16 distinct commands. Needs `--bash-spill-root`. |
| `--bash-spill-root <workspace folder>` | Folder for full bash output that doesn't fit the result. |
| `--bash-executable <absolute file>` | The shell to use. |
| `--bash-timeout <seconds>` | Time limit for each command. |

Paths must be absolute and can't be UNC paths. A `--message` holds up to 65,536 characters.

### Startup options

`create`, `prompt`, `resume`, `rpc` and `chat` also take:

| Option | Meaning |
| --- | --- |
| `--user-settings <JSON>`, `--project-settings <JSON>` | User and project settings files to load, as absolute paths. |
| `--steering-mode`, `--follow-up-mode` | `all` or `one-at-a-time`. |
| `--tools`/`-t`, `--no-tools`/`-nt`, `--no-builtin-tools`/`-nbt`, `--exclude-tools`/`-xt`, `--no-mcp` | Tool selection, as for `pisharp`. |
| `--skill <absolute path>`, `--prompt-template <absolute path>` | Skills and prompt templates. Repeatable. |
| `--extension-package <dir>`, `--extension-manifest <JSON>`, `--extension-approval <JSON>`, `--extension-snapshot-root <dir>` | A native extension package, with its manifest, approval and snapshot folder. |
| `--enable-extension-tool <name>`, `--deny-extension-tool <name>`, `--enable-extension-command <name>` | Turns the package's tools and commands on or off. |

### chat commands

`session chat` reads lines from stdin. A plain line is a prompt. `/edit` starts a multi-line draft that `/save` sends, `/cancel` discards and `/show` prints; `/send <text>` sends text that starts with `/`. The other commands are `/compact [focus]`, `/compact-all`, `/branch-summary <entryId|root>`, `/auto-compact on|off`, `/abort`, `/state`, `/steer <text>`, `/follow-up <text>`, `/clear-queue` and `/quit`. Extension commands and prompt templates work too.

Reports go to stdout. Failures go to stderr as one JSON line such as `{"schemaVersion":1,"type":"session_command_result","status":"failed","code":"InvalidArguments",...}`.

## durable

`pisharp durable` runs the experimental durable agent, whose sessions survive a crash. See [Pi Durable](/docs/guides/durable/).

| Option | Meaning |
| --- | --- |
| `-c`, `--continue` | Continues the newest durable session. |
| `--tier <tier>`, `--service-tier <tier>` | Service tier for a new session (experimental, unreleased). Without it, a new session takes the `serviceTier` setting. A session that `--continue` reopens keeps its stored tier; change it with `/tier`. |

Any other argument is an error, `Unknown argument: <arg>`. The command prints a notice on stderr that it is experimental. `--tier` and the media tools in `pisharp durable` are not in any release yet and need a CLI built from source.

## Exit codes

| Command | Code | Meaning |
| --- | --- | --- |
| `pisharp` (every mode) | 0 | Success. |
| | 1 | An error: bad options, a failed start, a failed export. |
| | 130 | Cancelled with `Ctrl+C`. |
| Print, JSON and RPC modes | 143 | Ended by SIGTERM. |
| | 129 | Ended by SIGHUP (not on Windows). |
| Print mode | 1 | The last answer stopped with an error or was aborted (the error goes to stderr), or it hit the output token limit (`Response was truncated: the model reached its output token limit.`). |
| `auth check` | 0, 1, 2 | Ready, not ready, invalid or error. |
| `mcp list` | 1 | A server failed. |
| `update` | 1 | Self-update isn't possible. |
| `session <verb>` | 0, 1, 2 | Success; a failure, cancellation or provider error; invalid arguments. |
| `durable` | 1 | A bad argument or tier, or the session couldn't open. |

## Environment

PiSharp reads Pi's variables, such as provider keys, `PI_CODING_AGENT_DIR`, `PI_CODING_AGENT_SESSION_DIR` and `PI_OFFLINE`, and a few of its own. `PI_TELEMETRY` is accepted and ignored: PiSharp sends no install telemetry. See [Environment variables](/docs/reference/environment/).
