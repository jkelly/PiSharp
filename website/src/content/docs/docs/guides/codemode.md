---
title: Code mode
description: Let the model call tools from JavaScript in a sandbox, and how to tune it.
---

Code mode gives the model one more tool, `codemode`. Its input is a JavaScript script that calls the session's other tools and runs non-LLM models, such as classifiers and image models. Only what the script outputs reaches the model. A script can run many calls in parallel, filter large results, and chain steps without a round trip to the model for each one.

PiSharp ports Pi v1.2.0's code mode. The scripts, globals, limits and messages are Pi's; the JavaScript engine is different. Pi's own page ships with PiSharp at [src/PiSharp.Cli/docs/codemode.md](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Cli/docs/codemode.md) and is the full reference for scripts. This page covers turning it on, the settings, and what differs in PiSharp.

## Turn it on

`codemode` comes from the `builtin:codemode` [built-in extension](/docs/reference/configuration/#built-in-extensions). It is registered with every session but starts off. Turn it on for one run, or for good:

```sh
pisharp --tools +codemode
```

```json
{
  "defaultTools": ["+codemode"]
}
```

MCP servers turn it on too: a server whose tools have `codemode` exposure (the default for `pisharp mcp add`) activates `codemode`, unless `autoEnableCodemode` is `false` in `mcp.json`. See [MCP](/docs/guides/mcp/).

`codemode` is missing when `builtin:codemode` is off, for example with `--no-extensions` or `"extensions": ["-builtin:codemode"]`. `--no-mcp` doesn't remove it. An extension that registers its own `codemode` tool replaces the built-in one.

To give a script only some tools, name them in an allowlist:

```sh
pisharp --tools read,bash,codemode,'mcp__radius__*'
```

## Settings

| Key | Default | Effect |
| --- | --- | --- |
| `codemode.mode` | `on` | `on` keeps the other tools declared to the model, and their descriptions say how to call them from scripts. `only` hides them from the model and lists them in the `codemode` description instead, so the model reaches them only through scripts. |
| `codemode.inlineBudget` | `3000` | Estimated tokens for the tool declarations listed in the `codemode` description. Tools past the budget are still callable; scripts find them with `searchTools()`. |

Both are Pi's settings. See [Settings](/docs/reference/settings/#tools-and-shell).

## Scripts

A script is raw JavaScript, run as the body of an async function, so top-level `await` and `return` work. It may start with an options line:

```js
// @options: {"max_output_tokens": 2000, "timeout_ms": 60000}
const [pkg, files] = await Promise.all([
  tools.read({ path: "package.json" }),
  tools.bash({ command: "git ls-files | wc -l" }),
]);
text(JSON.parse(pkg).dependencies);
return files.output;
```

| Option | Default | Meaning |
| --- | --- | --- |
| `max_output_tokens` | `10000` | Output limit. Longer output keeps its start and end, and the full text goes to a temp file named in the result. |
| `timeout_ms` | none | Hard deadline for the whole script. |

Each tool is a method of `tools`, named by its identifier with characters JavaScript doesn't allow turned into `_`: the MCP tool `mcp__dev-radius__search` is `tools.mcp__dev_radius__search`. A failed call rejects with the tool's error text. Tool calls are real: calls made before a script fails aren't undone.

The globals are `tools`, `text()`, `image()`, `console`, `exit()`, `store()` and `load()`, `ALL_TOOLS`, `searchTools()`, `describeTool()`, `describeNamespace()` and `models`. There are no timers, no `fetch`, no file system and no Node APIs. Pi's [code mode page](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Cli/docs/codemode.md) describes each global and what each tool call resolves to.

## Models from scripts

`models` lists the model catalog and runs classifier and image models with the session's credentials: `models.classify()` answers typed questions about JSON state, and `models.generateImages()` makes images. Chat models are listed but can't be run from scripts. At most four such calls run at once per script; more wait for a slot. Their cost counts toward the session. See [Classifier and image models](/docs/guides/providers/#classifier-and-image-models).

## MCP tools

With the default `codemode` exposure, an MCP server's tools aren't declared to the model and aren't listed in the `codemode` description either, so the description stays the same while servers connect. Scripts find them with `searchTools()`, `describeTool()`, `describeNamespace()` or `ALL_TOOLS`, and call them like any other tool. Before a script runs, PiSharp waits for the servers it may reach: those whose names appear in the script, or all of them when it uses `searchTools()`, `describeTool()`, `describeNamespace()` or `ALL_TOOLS`. See [MCP](/docs/guides/mcp/) for the other exposures.

## Limits

| Limit | Value |
| --- | --- |
| Memory | 256 MiB allocated between two tool calls, and 1 GiB for the whole script. |
| Recursion | 10,000 nested script function calls. |
| Native nesting | About 1000 levels for `JSON.stringify`, `JSON.parse`, `flat`, `join` and similar built-ins; deeper values throw a catchable error. |
| Arrays | 32 Mi elements. |
| Regular expressions | 5 seconds per match. |
| Output | 16,777,216 characters of text and image data, and 100,000 `text()`, `image()` and `console` calls. |
| Store | 262,144 characters of JSON per value, 1,048,576 in total. |
| Model calls | 4 at once per script. |
| Deadline | `timeout_ms`, counted from when the script starts running. |

`store()` and `load()` keep small JSON values across scripts. Values are saved in the session only when a script succeeds, so a resumed session keeps them and each branch sees only its own.

## Jint instead of QuickJS

Pi runs scripts in QuickJS compiled to WebAssembly, in a Node worker thread. PiSharp runs them in [Jint](https://github.com/sebastienros/jint) 4.16.3, a JavaScript interpreter for .NET, so code mode needs no Node and no native binaries. Pi's script prelude runs unchanged, so lockdown, the `tools` proxy, output limits and the store behave as in Pi. See [decision 0003](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0003-codemode-javascript-engine.md).

What scripts can notice:

- **Some limits can't be caught.** Running out of memory or recursion ends the script with `InternalError: out of memory` or `RangeError: Maximum call stack size exceeded`. In QuickJS a script can catch both.
- **Deep throws cost memory.** A script that throws about 3000 calls deep runs out of memory instead of reaching its `catch`.
- **Memory counts allocations.** A script that makes several hundred MiB of short-lived garbage without awaiting a tool can hit the limit where QuickJS would have collected it. Each awaited tool call starts a new count.
- **Engine messages differ.** For example, `JSON.stringify` of a circular value throws `TypeError: Cyclic reference detected.`. Messages from Pi's prelude, the tools and the host are Pi's.
- **Newer built-ins exist**, such as `Temporal` and `Iterator` helpers. `Atomics.wait` is removed.

The full list is in [Codemode engine: Jint versus QuickJS](https://github.com/jkelly/PiSharp/blob/main/docs/compatibility/codemode-engine.md).

The `codemode` description the model reads is Pi's, so it still says scripts run in a QuickJS sandbox.

### Worker processes

Jint runs on the .NET stack, and .NET can't recover from a stack overflow. So each script runs in a child process of PiSharp itself (`pisharp --codemode-worker`). A crash ends only that worker, and the script fails with `Worker exited with code N before the script settled`, as in Pi. Deadlines and aborts kill the worker.

Starting a process costs about 100 to 300 ms per script, against a few milliseconds for Pi's worker thread. PiSharp keeps one warmed spare worker ready to cut that, and the start isn't charged to `timeout_ms`. A worker that doesn't start within 60 seconds fails the script.

The worker's engine thread gets a 256 MiB stack (64 MiB in a 32-bit process). Set `PISHARP_CODEMODE_WORKER_STACK_BYTES` to change it, in bytes; values under 1 MiB are ignored.

## Embedding code mode

The `PiSharp.Codemode` library runs the same sandbox in your own program. See [Code mode](/docs/embed/codemode/) in the embedding guide.
