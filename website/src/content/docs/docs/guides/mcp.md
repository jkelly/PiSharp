---
title: MCP servers
description: Connect MCP servers, sign in with OAuth, and reach their tools directly, through tool_search or from codemode.
---

PiSharp connects to MCP servers over stdio or streamable HTTP, as Pi does. It uses the same files, so servers you set up for Pi work in PiSharp. Pi's [MCP Servers](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/mcp.md) page describes the format in full.

## Add a server

```sh
pisharp mcp add filesystem -- npx -y @modelcontextprotocol/server-filesystem .
pisharp mcp add docs --url https://example.com/mcp --bearer-token-env-var DOCS_TOKEN
pisharp mcp list
```

`add` and `remove` change `~/.pi/agent/mcp.json`, or the project's `.pi/mcp.json` with `-l`. `list` connects to every enabled server and prints its state and tools.

## Where servers come from

- `~/.pi/agent/mcp.json`: your servers, in every folder.
- `.pi/mcp.json`: project servers. They load only when you trust the project. An entry with only `enabled`, `exposure` or `toolExposure` changes your server of the same name for that project.

Configuring a server trusts it. Its tools can be called without a separate approval, as in Pi.

## Sign in with OAuth

HTTP servers that use OAuth need no credentials in `mcp.json`. Sign in with `pisharp mcp login <server>`, or with `/mcp` inside a session. Tokens are stored in `~/.pi/agent/mcp-auth.json` and refreshed when they expire. `pisharp mcp logout <server>` deletes them.

The `oauth` options (`clientId`, `clientSecret`, `callbackPort`, `callbackUrl`, `scope`, `clientName`, `clientRegistration: "cimd"` and `authServerMetadataUrl`) work as in Pi.

## How tools reach the model

Each tool is named `mcp__<server>__<tool>`. The server's `exposure` decides how the model reaches it:

| Exposure | Behaviour |
| --- | --- |
| `codemode` (default) | Called from `codemode` scripts. Not declared to the model. |
| `deferred` | Declared once `tool_search` finds it. |
| `direct` | Declared to the model like a built-in tool. |
| `hidden` | Not reachable. |

`toolExposure` sets the exposure of single tools or `*` patterns.

PiSharp turns on `codemode` when a server with `codemode` exposure connects, and `tool_search` for `deferred` servers. Both are always registered, so `--tools` and `defaultTools` can name them. Servers connect in the background. The first prompt waits up to 10 seconds for servers with `direct` tools; `tool_search` and codemode scripts wait for the servers they need.

Servers that offer resources get the `list_mcp_resources`, `list_mcp_resource_templates` and `read_mcp_resource` tools.

## Codemode

`codemode` runs a JavaScript script the model writes. The script calls other tools with `tools.<name>(args)`, so it can run calls in parallel and filter large results before they reach the model. Pi runs these scripts in QuickJS. PiSharp runs them in [Jint](https://github.com/sebastienros/jint), a JavaScript engine for .NET, in a separate worker process per script.

The script API, the `store()` and `models` globals and the limits are Pi's. The [codemode guide](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/codemode.md) ships with PiSharp as `docs/codemode.md`. Differences scripts can see:

- Each script pays a process start, about 100 to 300 ms.
- The deadline starts when the script starts running in its worker, not when the worker launches.
- Running out of memory or recursion depth ends the script. In Pi, a script can catch those errors.
- Error messages from the engine itself, such as a `JSON.stringify` failure, have different text.

## Manage servers in a session

`/mcp` lists servers with their state, tool count and exposure. Select one to see its tools, reconnect, sign in or out, change its exposure, or turn it off. `--no-mcp` turns MCP off for one run.
