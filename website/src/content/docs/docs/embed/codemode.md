---
title: Code mode library
description: Run model-written JavaScript against your tools with PiSharp.Codemode.
---

Code mode lets the model write a short JavaScript program instead of calling tools one at a time. The script calls tools as `await tools.<name>(args)`, runs independent calls in parallel, filters large output, and returns only what the model needs. `PiSharp.Codemode` is the library behind PiSharp's built-in `codemode` tool. It ports Pi v1.2.0's `codemode` package and the coding agent's codemode extension.

Pi runs scripts in QuickJS compiled to WebAssembly. PiSharp runs them in [Jint](https://github.com/sebastienros/jint) 4.16.3, a JavaScript engine written in .NET, in a child process per script. Scripts get Pi's globals and limits, and the messages the prelude, the tools and the host write are Pi's. Only the engine's own error messages differ. For the tool's settings and how it behaves in a session, see [Code mode](/docs/guides/codemode/).

## Install

```powershell
dotnet add package PiSharp.Codemode --version 1.2.0.1
```

The package depends on `PiSharp.Contracts`, `PiSharp.Extensions.Abstractions` and Jint, pinned to exactly 4.16.3. Jint's and its parser Acornima's license notices are in the package under `licenses/`. Everything is in the `PiSharp.Codemode` namespace.

The library has two levels:

| Level | Types | Use it to |
| --- | --- | --- |
| The tool | `CodemodeToolDefinition`, `CodemodeExecutor`, `ICodemodeHost`, `ICodemodeModelRuntime` | Offer Pi's `codemode` tool to a model: its description, its result format, nested tool calls, `store()` and `models`. |
| The sandbox | `CodemodeSandbox`, `CodemodeTool`, `CodemodeSandboxOptions`, `CodemodeResult` | Run JavaScript against .NET functions you supply, with no tool conventions. |

## Set up the worker process

Each script runs in a worker process that is a copy of your own executable, started with the single argument `--codemode-worker`. A script that overflows the engine's stack then ends only its worker, and your process keeps running. Hand that argument to the worker entry point at the top of `Main`, and set the default launcher before you create a sandbox:

```csharp
using PiSharp.Codemode;

public static async Task<int> Main(string[] args)
{
    if (args is [CodemodeWorker.Argument])
        return await CodemodeWorker.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
    CodemodeWorker.Default ??= CodemodeWorkerLauncher.ForCurrentProcess();

    // The rest of your app.
    return 0;
}
```

The PiSharp CLI does exactly this.

- `CodemodeWorkerLauncher.ForCurrentProcess()` launches the current executable, or `dotnet <entry assembly>` when the app runs under `dotnet`. It returns null when the process has no launchable entry.
- `new CodemodeWorkerLauncher(fileName, arguments)` starts another executable. Its entry point must pass `CodemodeWorker.Argument` to `CodemodeWorker.RunAsync`. `Environment` adds variables to the worker's inherited environment.
- `CodemodeSandboxOptions.Worker` picks the launcher per sandbox. It defaults to `CodemodeWorker.Default` as it was when the options object was created.
- `PISHARP_CODEMODE_WORKER_STACK_BYTES` sets the worker's engine stack, from 1 MiB. The default is 256 MiB, or 64 MiB in a 32-bit process.

:::caution[Without a launcher]
When `CodemodeWorker.Default` is null and no `Worker` is set, the engine runs on a thread of your own process. Most scripts work, but one that nests values hundreds of thousands of levels deep can overflow the stack, and .NET cannot recover from that: your process ends.
:::

One worker runs one script. The library starts the next worker ahead, with the engine already compiled, so a script waits about 100 to 300 ms for its process. A finished, timed-out or aborted script's worker is killed and reaped before the result is returned. A worker exits when its standard input closes, so it ends with its host. A worker that does not start within 60 seconds fails the script as a sandbox error.

## Run a script

`CodemodeSandbox` runs a script as the body of an async function, so top-level `await` and `return` work. Give it the tools the script may call:

```csharp
using System.Text.Json;
using PiSharp.Codemode;
using PiSharp.Contracts;

await using var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions
{
    Tools =
    [
        new CodemodeTool("add", (arguments, cancellationToken) =>
        {
            var values = arguments!.Value;
            var sum = values.GetProperty("a").GetDouble() + values.GetProperty("b").GetDouble();
            return ValueTask.FromResult<JsonData?>(JsonData.Parse(JsonSerializer.Serialize(sum)));
        })
        {
            Description = "Adds two numbers.",
            InputSchema = JsonData.Parse("""{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}"""),
            OutputSchema = JsonData.Parse("""{"type":"number"}"""),
        },
    ],
    TimeoutMs = 10_000,
});

var result = await sandbox.ExecuteAsync("""
    const sums = await Promise.all([tools.add({ a: 1, b: 2 }), tools.add({ a: 3, b: 4 })]);
    text(`sums: ${sums.join(", ")}`);
    return sums[0] + sums[1];
    """);

if (result.Ok)
{
    foreach (var item in result.Output)
        if (item is CodemodeTextOutput text) Console.WriteLine(text.Text);
    Console.WriteLine($"returned {result.Value}");
}
else Console.WriteLine($"{result.Error!.Kind}: {result.Error.Message}");
```

A tool's implementation is a `CodemodeToolExecute` delegate. It receives the script's argument after a JSON round trip, null for `undefined`, and returns JSON, null for `undefined`. An exception becomes an `Error` with the same message in the script. Its token is cancelled when the script finishes, times out or is aborted, also for calls the script never awaited.

`ExecuteAsync` never throws for a script failure. It returns a failed `CodemodeResult` instead, and fails only when the sandbox is closed.

### What the script sees

| Global | Meaning |
| --- | --- |
| `tools.<id>(args)`, `tools["<name>"](args)` | Your tools. `<id>` is the name with characters not valid in a JavaScript identifier replaced by `_` (`CodemodeIdentifier.ToIdentifier`). |
| `ALL_TOOLS` | Each tool's script identifier and description. |
| `text(value)`, `image(dataUrlOrBlock)`, `console.*` | Output, in order. |
| `exit()` | Ends the script with no value. |
| `store(key, value)`, `load(key)` | JSON values: `load` reads the store you pass in, and `store` writes are returned in `StoreWrites`. |
| Your globals | `CodemodeSandboxOptions.Globals`, as `name(args)` or `ns.member(args)`. |

There are no timers, `fetch`, file system, modules or `require`, and no way to reach .NET types. `eval` and `Function` work inside the engine. Globals may not use the reserved names `tools`, `ALL_TOOLS`, `console`, `text`, `image`, `exit`, `globalThis`, `store` and `load`. A global with `Spread` set receives all its call arguments as one array. `Signature` replaces the TypeScript signature rendered from its schemas.

### Sandbox options

| Option | Default | Meaning |
| --- | --- | --- |
| `Tools` | none | Tools on `tools`. `RegisterTool` and `UnregisterTool` change them later. Names must be unique. |
| `Globals` | none | Functions exposed as top-level names or `ns.member`. Their calls are not recorded in `Calls`. |
| `TimeoutMs` | 300000 | Deadline of each execution, including time in tools. Null disables it. |
| `MemoryLimitBytes` | 256 MiB | Bytes the engine may allocate between two host entries: the script's start and each settled call. |
| `TotalAllocationLimitBytes` | 1 GiB | Bytes one execution may allocate in total. |
| `RecursionLimit` | 10000 | Maximum JavaScript call depth. |
| `InterruptBudget` | none | Jint statements one execution may run before it fails as a timeout. It doesn't depend on the machine's speed. |
| `Worker` | `CodemodeWorker.Default` | The worker launcher. Null runs the engine on a thread of this process. |

`CodemodeExecuteOptions` sets `TimeoutMs` for one execution (`double.PositiveInfinity` disables it), the `Store` values `load()` reads, and the `AbortMessage` of an execution your token cancels. The default message is `This operation was aborted`.

`CloseAsync()`, or disposing the sandbox, aborts the executions in flight and refuses new ones.

### Results

| `CodemodeResult` | Meaning |
| --- | --- |
| `Ok` | True when the script returned, exited or ran to its end. |
| `Value` | What the script returned. Null for `undefined` or `exit()`. |
| `Output` | `CodemodeTextOutput(Text, Console)` and `CodemodeImageOutput(Data, MimeType)` items in the order the script made them, kept up to a failure. |
| `Calls` | `CodemodeCall(Name, Status, DurationMs)` for each tool call; `Status` is `Ok`, `Error` or `Cancelled`. |
| `StoreWrites` | `Set` and `Delete` keys the script changed with `store()`. |
| `Error` | `CodemodeError(Kind, Message, Name, Stack)` when the script failed. |

| `CodemodeErrorKind` | When |
| --- | --- |
| `Script` | The script threw or did not parse. |
| `Timeout` | The deadline or the interrupt budget ran out. |
| `Aborted` | Your token fired or the sandbox closed. |
| `Sandbox` | The engine failed outside the script's control, for example a worker that exited. |

### Limits

`CodemodeLimits` holds the limits as constants:

| Constant | Value | Meaning |
| --- | --- | --- |
| `MaxStoreValueChars` | 256 Ki characters | One stored value. |
| `MaxStoreTotalChars` | 1 Mi characters | All stored values together. |
| `MaxOutputChars` | 16 Mi characters | All output of one script. |
| `MaxOutputItems` | 100000 | Output items of one script. |
| `DefaultMemoryLimitBytes` | 256 MiB | Default of `MemoryLimitBytes`. |
| `DefaultTotalAllocationLimitBytes` | 1 GiB | Default of `TotalAllocationLimitBytes`. |
| `DefaultRecursionLimit` | 10000 | Default of `RecursionLimit`. |
| `DefaultTimeoutMs` | 300000 | The sandbox's default deadline. |

The engine also caps arrays at 32 Mi elements and stops a regular expression after 5 seconds. Memory and recursion overruns end the script; unlike in QuickJS, the script can't catch them. [`codemode-engine.md`](https://github.com/jkelly/PiSharp/blob/main/docs/compatibility/codemode-engine.md) lists every difference a script can observe.

## The codemode tool

To give a model Pi's `codemode` tool, declare it with `CodemodeToolDefinition` and run each call with `CodemodeExecutor`. The executor reaches your session through an `ICodemodeHost`.

| `CodemodeToolDefinition` | Value |
| --- | --- |
| `Name` | `codemode` |
| `Parameters` | `{ code: string }`, the raw JavaScript source |
| `CreateDescription(tools, models, deferred, guidelines, inlineBudget)` | The model-facing description: the globals, the `models` API when `models` is true, and one section per tool, grouped by namespace, up to `inlineBudget` tokens (null lists every tool) |
| `PromptSnippet`, `PromptGuidelines` | The system prompt line and guideline Pi adds for the tool |
| `ConstrainedSampling` | A Lark grammar, so models that support grammar-constrained input send the script as raw text |
| `DefaultInlineBudget` | 3000 tokens of tool sections in the description |
| `StoreEntryType` | `codemode-store`, the custom session entry that holds `store()` writes |
| `DocsPath` | The script reference the description points to, for the `models` API. Default: `docs/codemode.md` next to your app. |

The description follows Pi's text, so it names QuickJS although PiSharp runs Jint. When the `models` API is on, set `DocsPath` to a copy of the CLI's [script reference](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Cli/docs/codemode.md), or ship that file in a `docs` folder next to your app.

### Implement the host

```csharp
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Codemode;
using PiSharp.Contracts;

public sealed class WeatherHost : ICodemodeHost
{
    private readonly List<JsonData> storeEntries = [];

    public ImmutableArray<CodemodeNestedTool> Tools { get; } =
    [
        new CodemodeNestedTool("weather", "Returns the current weather for a city.",
            JsonData.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")),
    ];

    public ICodemodeModelRuntime? Models => null;

    public ValueTask<CodemodeNestedOutcome> ExecuteToolAsync(string name, JsonData? arguments, CancellationToken cancellationToken)
    {
        var city = arguments?.Value.GetProperty("city").GetString() ?? "nowhere";
        var result = JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = $"Sunny in {city}" } } }));
        return ValueTask.FromResult(new CodemodeNestedOutcome(ToolCallId: null, result, IsError: false));
    }

    public IReadOnlyList<KeyValuePair<string, JsonData>> ReadStore() =>
        CodemodeStore.Read(storeEntries.Select(entry => ((string?)CodemodeToolDefinition.StoreEntryType, entry.Value)));

    public ValueTask AppendStoreEntryAsync(JsonData data, CancellationToken cancellationToken)
    {
        storeEntries.Add(data);
        return ValueTask.CompletedTask;
    }
}
```

| `ICodemodeHost` | Meaning |
| --- | --- |
| `Tools` | The tools scripts may call, as `CodemodeNestedTool(Name, Description, Parameters)` with optional `OutputSchema`, `Namespace` and `PromptGuidelines`. A tool named `codemode` is left out. |
| `ExecuteToolAsync(name, arguments, token)` | Runs one nested call. Send it through your normal tool path, with validation, hooks and permissions. |
| `ReadStore()` | The values `load()` sees. |
| `AppendStoreEntryAsync(data, token)` | Saves one successful script's `store()` writes, `{ set, delete }`. |
| `Models` | The `models` API, or null for none. |

A tool with an `OutputSchema` resolves in the script to its result's `structuredContent`, also for an error result that carries one. For such a tool, `CodemodeNestedOutcome.ScriptValue` replaces that value. A tool without an output schema resolves to its text, and an error result rejects with that text.

In a session, keep each script's writes as a session entry of type `codemode-store`, and read the entries of the current branch from the root. Each branch then sees its own values, as in Pi. `CodemodeStore.Read` applies the entries in order, and `CodemodeStore.EntryData` writes one.

### Run a call

```csharp
var host = new WeatherHost();
JsonData result;
try
{
    result = await CodemodeExecutor.ExecuteAsync("call-1", """
        // @options: {"timeout_ms": 20000}
        const reports = await Promise.allSettled(["Oslo", "Lima"].map(city => tools.weather({ city })));
        for (const report of reports) text(report.status === "fulfilled" ? report.value : String(report.reason));
        """, host);
}
catch (CodemodeSourceException error)
{
    result = JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = error.Message } }, isError = true }));
}
Console.WriteLine(result);
```

`ExecuteAsync(toolCallId, input, host, onUpdate, models, cancellationToken)` returns the tool result as JSON:

```text
{ content, details: { calls, fullOutputPath? }, usage?, isError? }
```

- `content` starts with a `Script completed` or `Script failed` header and the wall time, followed by the output. A failure adds the error and the tool calls made before it, which are not undone.
- `details.calls` has one row per nested call, with `id`, `name`, `args`, `status` and, when known, `durationMs`, `error` and `cost`.
- `usage` totals the token usage of `models` calls.
- `onUpdate` receives `{ content: [], details }` as calls start and finish, for a live view.

The input's optional first line, `// @options: {"max_output_tokens": 10000, "timeout_ms": 60000}`, sets the output budget and the deadline. The executor sets no deadline otherwise. Output beyond `max_output_tokens`, 10000 by default (`CodemodeExecutor.DefaultMaxOutputTokens`), keeps its start and end, and the full text goes to a temp file named in the result. Images the script shows are saved to temp files too, and the result names each path. An invalid options line throws `CodemodeSourceException`. `CodemodeSource.Parse` checks it on its own.

`CodemodeRenderer` renders a call and its result as plain text rows, and `CodemodeRenderer.Renderers` hands them to an extension host.

### Present the tool loadout

`CodemodeToolDefinition.PrepareLoadout(loadout, mode, models, inlineBudget, outputSchema)` is Pi's loadout preparation. It returns the descriptions to replace and the declarations to hide for one request:

- `CodemodeMode.On`: the model keeps its direct tools, and each callable tool's description says how a script calls it. The codemode description lists the tools that are not direct.
- `CodemodeMode.Only`: the codemode description lists every callable tool, and the request leaves out the declarations of the active direct tools.

Deferred tools are never listed, so the description doesn't change while late tools, such as an MCP server's, arrive. `CodemodeToolDefinition.ReadSettings(settings)` reads `codemode.mode` and `codemode.inlineBudget` from a settings object: `"only"` gives `Only`, anything else `On`, and a missing budget gives null, which `PrepareLoadout` treats as `DefaultInlineBudget`. The keys are on the [settings reference](/docs/reference/settings/).

`GetScriptReach(input)` returns the tools a script may reach, from the words of its source. A script that uses `ALL_TOOLS`, `searchTools`, `describeTool` or `describeNamespace` reaches every tool. The CLI uses it to wait for the MCP servers a script needs.

## Models in scripts

With `models: true` and a host whose `Models` is not null, scripts get a `models` global for classifiers and image generation: `getModelsOfType`, `getAvailableOfType`, `getModelOfType`, `classify` and `generateImages`. A script runs at most four `classify` or `generateImages` calls at once (`CodemodeExecutor.MaxConcurrentModelCalls`), and each appears in `details.calls` with its cost.

`ICodemodeModelRuntime` is the part of a model registry scripts reach. Models travel as catalog JSON without `headers`. This adapter puts it over the registry from [Classifiers and images](/docs/embed/model-operations/):

```csharp
using System.Collections.Immutable;
using PiSharp.AI.ModelOperations;
using PiSharp.Codemode;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

public sealed class RegistryModelRuntime(ModelOperationsRegistry registry) : ICodemodeModelRuntime
{
    public ImmutableArray<JsonData> GetModelsOfType(ModelType type, string? provider) =>
        [.. registry.GetModelsOfType(type, provider).Select(model => model.ToPublicJson())];

    public async Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(ModelType type, string? provider, CancellationToken cancellationToken) =>
        [.. (await registry.GetAvailableOfTypeAsync(type, provider, cancellationToken)).Select(model => model.ToPublicJson())];

    public JsonData? GetModelOfType(ModelType type, string provider, string id) =>
        registry.GetModelOfType(type, provider, id)?.ToPublicJson();

    public Task<ClassifierResult> ClassifyAsync(JsonData model, ClassifierContext context, CancellationToken cancellationToken) =>
        registry.ClassifyAsync((ClassifierModel)Find(ModelType.Classifier, model), context, null, cancellationToken);

    public Task<AssistantImages> GenerateImagesAsync(JsonData model, ImagesContext context, CancellationToken cancellationToken) =>
        registry.GenerateImagesAsync((ImageModel)Find(ModelType.Image, model), context, null, cancellationToken);

    // The executor passes back an entry GetModelOfType returned. Use the registry's own model, with its headers.
    private OperationModel Find(ModelType type, JsonData model) =>
        registry.GetModelOfType(type, model.Value.GetProperty("provider").GetString()!, model.Value.GetProperty("id").GetString()!)
        ?? throw new InvalidOperationException("Unknown model.");
}
```

Return it from your host's `Models`, pass `models: true` to `ExecuteAsync`, and pass `models: true` to `CreateDescription` or `PrepareLoadout` so the description mentions the API.

## Deterministic runs

`CodemodeSandbox.Start(code, runOptions, token)` runs a script deterministically and hands every call to you. The same script, start time, seed, tools, globals and store, answered with the same results in the same order, makes the same calls and ends the same way. A host can therefore record the answers and replay a script after a restart. Durable code mode uses it; see [Durable library](/docs/embed/durable/).

```csharp
using System.Threading.Channels;
using PiSharp.Codemode;
using PiSharp.Contracts;

var calls = Channel.CreateUnbounded<CodemodeHostCall>();
await using var sandbox = new CodemodeSandbox();
var run = sandbox.Start("""
    const city = await tools.lookup({ key: "city" });
    text(`${city} at ${new Date().toISOString()}, lucky number ${Math.random()}`);
    """, new CodemodeRunOptions
{
    StartedAt = 1_790_000_000_000,
    Seed = 42,
    Tools = [new CodemodeDeclaredTool("lookup")],
    OnCall = call => calls.Writer.TryWrite(call),
});

long seq = 0;
while (!run.Finished)
{
    var next = calls.Reader.ReadAsync().AsTask();
    if (await Task.WhenAny(next, run.Result) != next) break;
    var call = await next;
    run.Deliver(++seq, 1_790_000_000_000 + seq * 1000, call.Id, CodemodeOutcome.Success(JsonData.Parse("\"Oslo\"")));
}
var result = await run.Result;
```

| `CodemodeRunOptions` | Meaning |
| --- | --- |
| `StartedAt` (required) | The engine clock at the start, in milliseconds since the epoch. |
| `Seed` (required) | Seeds `Math.random()`. |
| `Tools`, `Globals` | `CodemodeDeclaredTool(Name)` with optional `JsName` and `Description`, and `CodemodeDeclaredGlobal(Name)` with `Spread`. The sandbox's own tools and globals are not used. |
| `OnCall` (required) | Receives each `CodemodeHostCall(Id, Target, Name, Args)`. Answer it now or later. |
| `OnStarted` | Receives the engine's fingerprint before the script runs. |
| `OnIdle` | The engine processed result `seq` and waits again, having issued this many calls. |
| `Store`, `TimeoutMs`, `AbortMessage` | As for `ExecuteAsync`. `TimeoutMs` defaults to none here. |

`CodemodeRun.Deliver(seq, now, id, outcome)` answers call `id` as result number `seq`. Results are numbered 1, 2 and so on without gaps, and the engine applies them in that order whatever order they arrive in. While the engine processes a result, its clock reads `now`. `CodemodeOutcome.Success(value)` resolves the call, and `CodemodeOutcome.Failure(message, properties)` rejects it with an `Error` carrying the properties. `AbortAsync(message)` ends the run with an error the script can't catch. `Finished` is set as soon as the outcome is decided. When the script itself ended the run, the result's `During` says which result the engine was processing then.

The engine runs in UTC with the invariant culture, and `WeakRef`, `FinalizationRegistry` and `ShadowRealm` don't exist. The fingerprint names the engine. A script that started on PiSharp's Jint engine can't resume on Pi's QuickJS engine, or the other way round.

## Other helpers

| Type | Use |
| --- | --- |
| `CodemodeDeclarations` | Renders tools and globals as TypeScript declarations (`RenderDeclarations`, `RenderToolSignature`, `RenderToolSample`) and a JSON Schema as a TypeScript type (`SchemaToType`). |
| `CodemodeIdentifier.ToIdentifier` | The script identifier of a tool name: `my-tool` becomes `my_tool`. |
| `CodemodeSource.Parse` | Splits the `// @options:` line from the script and checks it. |
| `CodemodeStore.WriteOutputFileAsync` | Writes a temp file only the current user can read, as the executor does for long output and images. |
