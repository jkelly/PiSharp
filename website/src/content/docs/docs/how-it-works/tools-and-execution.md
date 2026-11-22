---
title: Tools, schemas & trust boundaries
description: A unified comparison of all six ways to expose tools in PiSharp, including built-ins, typed C#, Jint JavaScript, Roslyn scripts, extensions and MCP.
---

Tools are how an agent reads state, validates invariants, and performs actions. PiSharp offers **six distinct ways** to define and expose tools depending on whether you are using the CLI, embedding `PiSharp.Agent`, writing an extension, or running `PiSharp.CustomAgents`.

## All six tool paradigms side by side

| Paradigm | Package & API | Schema source | Trust boundary | Replay / Hot-reload |
| --- | --- | --- | --- | --- |
| **1. Built-in coding tools** | `PiSharp.Tools` (`read`, `write`, `edit`, `bash`, `grep`, `find`, `ls`) + `PiSharp.Tools.Skia` | Built-in Pi v1.5/1.6 schemas | `pi` policy (full workspace/shell) or `explicit` (`IToolActionPolicy` grants) | `ToolReplay.Unsafe` in Durable (returns `interrupted` after crash) |
| **2. Typed C# handlers** | `PiSharp.Agent` (`IToolExecutor`) or `PiSharp.CustomAgents` (`AgentTool.Create<TArgs>`) | Manual `JsonData` (`Agent`) or auto-exported from C# records + `[Description]` (`CustomAgents`) | Full trust in-process | Configurable `ToolReplay.Safe` / `Unsafe` + `OnceAsync` |
| **3. JavaScript sandbox** | `PiSharp.Codemode` (`codemode`) & `PiSharp.CustomAgents.JavaScript` (`JsTool`, `create_tool`) | `JsToolDefinition.Parameters` (or dynamic schema for `create_tool`) | Jint sandbox: no FS, network or host objects; optional worker process (`JsWorkerHost`) | Pure tools rerun safely; durable JS tools replay recorded sub-tool calls |
| **4. Roslyn C# scripts** | `PiSharp.CustomAgents.Scripting` (`ScriptTool`, `ScriptToolSource` over `*.tool.csx`) | `TArgs` type or `// @tool:` JSON header | Full trust in-process; optional `TrustedSourceHashes` SHA-256 allowlist | Collectible assembly hot-reload on file save (`Watch = true`) |
| **5. Extensions & plugins** | `PiSharp.Extensions.Abstractions` (`IPiSharpExtension`), `IAgentPlugin`, `PiSharp.Compatibility.Node` | Explicit `JsonData` or TypeBox (in TS extensions) | Trusted plugin folders (+ optional SHA-256 pin) or out-of-process Node worker | Hot-reloaded via `AgentPluginLoader` / `/reload` |
| **6. MCP & deferred tools** | `PiSharp.Extensions.Runtime` (`mcp.json`) + `builtin:tool-search` (`tool_search`) | Remote MCP server tool schemas | Out-of-process stdio or HTTP server with OAuth | Deferred until matched by BM25 `tool_search` or called via `codemode` |

## 1. Built-in tools & output bounds

A standard CLI session activates `read`, `write`, `edit`, and `bash`, while `grep`, `find`, and `ls` are registered off until selected with `--tools` or `defaultTools`. All built-in tools enforce Pi's shared output ceiling so a runaway command cannot blow out the context window:
- **2,000 lines or 50 KB** (51,200 bytes), whichever hits first.
- `read` keeps the first lines from `offset`; `bash` keeps the **last** lines and saves full output to a temp log file (`pi-bash-<id>.log`); `grep`/`find`/`ls` keep the first matches and emit a limit notice.
- `read` inspects image magic bytes (PNG, JPEG, GIF, WebP, BMP) and uses `PiSharp.Tools.Skia` to apply EXIF orientation and fit images within `2000×2000` and `4.5 MB` base64.

When embedding `PiSharp.Tools` in your own app, built-in tools run through `IPreparedToolAdapter` → `ToolInvoker` → **`IToolActionPolicy.AuthorizeAsync`**, letting your host inspect the resolved path or shell command before anything touches disk or spawns a process. See [Built-in tools reference](/docs/reference/tools/) and [Coding agent SDK](/docs/embed/coding-agent/#built-in-tools).

:::note[1.6.0 source candidate tool updates]
On `sync/pi-1.6.0` (unreleased): `find` path patterns are anchored at the search directory (use `**/src/*.ts` for any depth); `read` reports unsupported vision models in `details.imageUnsupportedByModel` instead of appending a text note; and `PiSharp.Durable.Tools` adds `FitImages.CreateExtension(...)` to fit tool-returned images before storage (`47-fit-images`).
:::

## 2. Typed C# tools & dynamic tool sources

In `PiSharp.CustomAgents`, `AgentTool.Create<TArgs>` exports the JSON Schema directly from your C# record and validates the model's arguments via `pi-schema` before your handler runs:

```csharp
var refund = AgentTool.Create<OrderArgs>("refund", "Refunds an order.", async (args, tool) =>
{
    var id = await tool.OnceAsync("refund",
        ct => refunds.RefundAsync(args.OrderId, tool.IdempotencyKey("refund"), ct));
    return $"Refund {id} requested for order {args.OrderId}.";
}, new AgentToolOptions { Replay = ToolReplay.Safe, Sequential = true });
```

If tools need to appear or disappear while agents are running (for feature flags, hot-reloaded scripts, or loaded plugins), publish them through a **`DynamicToolSource`** or custom **`ToolSource`**. Agents referencing that source ID in `AgentDefinition.ToolSources` see updates on their next model turn, and name collisions across sources fail fast with `ToolConflictException`.

## 3. Sandboxed JavaScript, code mode & model-authored tools

- **`codemode`** lets the model write one short JavaScript snippet that calls multiple tools in sequence or loops, returning only the summarized result to context instead of bouncing ten raw tool outputs through the LLM.
- **`JsTool`** turns a pure or durable JS snippet into a first-class named tool with memory (`64 MB`), allocation (`256 MB`), recursion (`2000`), and wall-clock (`10 s`) ceilings.
- **`JsAuthoring.Enable(team)`** lets agents marked `CanCreateTools = true` write persistent `js_*` tools at runtime via `create_tool` and `remove_tool`. By default, `RequireIsolation = true` insists on running model-authored scripts inside a separate worker process (`JsWorkerHost.Configure()`).

:::note[1.6.0 source candidate: extension-declared namespaces]
In Durable 1.6.0 (unreleased), `CodemodeToolOptions.Namespaces` is replaced by declaring `Extension.Namespace = new ToolNamespace(name, description, instructions)` directly on each extension that contributes tools.
:::

## 4. Roslyn C# scripts (`*.tool.csx`)

`PiSharp.CustomAgents.Scripting` compiles method-body C# scripts into **collectible assemblies** with Roslyn and caches compiled images by content hash. Drop `*.tool.csx` files with a `// @tool: {...}` header into a folder wrapped by `ScriptToolSource` (`Watch = true`) to hot-reload C# tools while live runs finish safely on the previous assembly.

Because collectible load contexts are **not** security sandboxes, use `CSharpScriptOptions.TrustedSourceHashes` whenever scripts come from outside your build pipeline.

## 5. Native C# extensions, plugins & the Node bridge

- **Native C# extensions (`IPiSharpExtension`)** run in-process with zero Node requirement, registering tools, slash commands, keyboard shortcuts, custom renderers, and session lifecycle hooks. Put them in `~/.pi/agent/extensions` or `.pi/extensions` with a `pisharp-extension.json` manifest, or adapt them into `PiSharp.CustomAgents` via `PiSharpExtensionAdapter`.
- **Custom agent plugins (`IAgentPlugin`)** load byte-copied assemblies from explicitly trusted directories (`AgentPluginLoaderOptions.Trust`, optionally pinned to an assembly SHA-256) so you can rebuild and hot-reload plugins without stopping the host.
- **Pi TypeScript extensions** run unchanged through `PiSharp.Compatibility.Node` on Pi's own npm packages.

## 6. MCP & deferred tool discovery (`tool_search`)

Connecting three MCP servers can easily expose 80+ tool schemas, consuming thousands of prompt tokens before the user types a word. PiSharp solves this with **deferred tools**:
- Mark MCP servers (`"exposure": "deferred"` or `"codemode"`) or C# tools (`AgentToolOptions.Deferred = true`).
- Hidden tools stay out of the prompt until the model calls `tool_search(query, limit)` (BM25 ranking with ICU word segmentation) or a tool returns `ToolControl.AddTools`.
- On the **1.6.0 source candidate**, MCP tools activated by `tool_search` persist cleanly across session resume and `/reload`.

## Trust boundary checklist

| Question | PiSharp answer |
| --- | --- |
| Does `PiSharp.Agent` validate JSON Schema automatically? | No — validate in your `IToolExecutor`, or use `JsonSchemaValidator` (`PiSharp.Contracts.Schema`) / `AgentTool.Create<T>` (`PiSharp.CustomAgents`). |
| Can a model escape a JavaScript tool? | No FS/network/host objects are bound; enable `JsWorkerHost.Configure()` for out-of-process worker isolation against deep-nesting crashes. |
| Are Roslyn C# scripts or plugins sandboxed? | **No.** Both execute with full process trust. Pin SHA-256 hashes (`TrustedSourceHashes` / `PluginTrust.Sha256`) if artifacts are not strictly controlled. |
| How are untrusted web pages and inter-agent messages framed? | `web_search` prepends an explicit untrusted-content header, and team messages arrive HTML-encoded inside `<agent-message>` tags. |

## Next steps

- [Agent tools reference (CustomAgents)](/docs/custom-agents/tools/)
- [Built-in tools reference (CLI)](/docs/reference/tools/)
- [Approvals & human-in-the-loop patterns](/docs/patterns/approvals-and-hitl/)
- [Tool errors, validation & typed repairs](/docs/patterns/tool-errors-and-repairs/)
