---
title: C# extensions
description: Write a native PiSharp extension in C# and load it like a Pi extension.
---

A native extension is a .NET class library with a class that implements `IPiSharpExtension`. PiSharp finds it in the same folders as Pi's TypeScript extensions, loads it into the process, and calls `InitializeAsync` with a registry. The extension registers its tools, commands and event handlers there.

Native extensions don't need Node. They share the session, the command list and the event bus with any TypeScript extensions that are loaded.

## Project file

Reference the extension contracts without copying them into your output, and enable dynamic loading:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="PiSharp.Extensions.Abstractions" Version="1.1.0.2" ExcludeAssets="runtime" />
    <PackageReference Include="PiSharp.Contracts" Version="1.1.0.2" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

PiSharp supplies `PiSharp.Contracts`, `PiSharp.Extensions.Abstractions` and .NET itself. Your other dependencies are loaded from your build output, through its `.deps.json`.

## The extension class

The entry class must be public, not abstract and not generic, with a constructor that takes no arguments:

```csharp
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

public sealed class HelloExtension : IPiSharpExtension
{
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        registry.RegisterCommand(new("hello", "hello", "Say hello", (arguments, context, token) => ValueTask.CompletedTask));

        registry.RegisterTool(new("echo", "echo", "Echoes text",
            JsonData.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}"""),
            (arguments, context, token) => ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = "echo: " + arguments.Value.GetProperty("text").GetString() } },
                details = new { },
            })))));

        registry.Observe(new("on-start", "session_start", (observation, context, token) => ValueTask.CompletedTask));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

The registry also takes handlers for `input`, `before_agent_start`, `context`, `tool_call` and `tool_result`. If it implements `IExtensionEventBusRegistry`, its `Events` property is the event bus Pi extensions use with `pi.events`.

Tool parameters are declared as JSON Schema. The host validates arguments against the schema before your tool runs.

### Classifier and image providers

If the registry implements `IExtensionModelOperationProviderRegistry`, `RegisterModelOperationProvider` adds classifier and image models, as `pi.registerProvider` with `classifiers` and `images` does in Pi. Give an `ExtensionModelOperationProvider` its models (catalog objects with `type` `"classifier"` or `"image"`) and the implementations by API id. The models join `ctx.modelRegistry` and codemode's `models` global, and leave when your extension unloads. A provider without an `ApiKey` needs a key the user stored with `/login` or passed with the request, as in Pi.

## The extension folder

Put your build output in a folder with a `pisharp-extension.json` next to it:

```text
~/.pi/agent/extensions/hello/
├── pisharp-extension.json
├── Hello.dll
└── Hello.deps.json
```

```json
{
  "assembly": "Hello.dll",
  "entryType": "HelloExtension"
}
```

| Field | Value |
| --- | --- |
| `assembly` | Path of the extension assembly, relative to the manifest. The file must exist. |
| `entryType` | Full name of the entry class, with its namespace. Use `+` for a nested class. |

Both are required and must be non-empty strings. Other fields are ignored.

## Where PiSharp finds it

The same places as TypeScript extensions:

- `~/.pi/agent/extensions/<name>/pisharp-extension.json`
- `.pi/extensions/<name>/pisharp-extension.json`, when you trust the project
- an `extensions` entry in `settings.json`, or a [package](/docs/extensions/packages/)
- `-e path/to/pisharp-extension.json` for one run

In a folder that has a `package.json` with `pi.extensions`, an `index.ts` or an `index.js`, those win over `pisharp-extension.json`.

Each load gets its own assembly load context, and the files are read into memory, so you can rebuild while PiSharp runs. `/reload` loads the new build.

## Keep state on the session branch

Don't keep mutable state in fields. Rebuild it from the current branch's session entries each time the tool runs. Your state then stays correct when the user branches or navigates the session tree. The `StatefulTodo` sample shows the pattern.

## Samples

- [StatefulTodo](https://github.com/jkelly/PiSharp/tree/main/samples/extensions/StatefulTodo): a tool whose state is rebuilt from the session branch, ported from Pi's `todo.ts` example.
- [SessionCheckpoint](https://github.com/jkelly/PiSharp/tree/main/samples/extensions/SessionCheckpoint): a `/checkpoint` command, a session-start observer and a pre-switch handler.

Each sample ships a `pisharp-extension.json`, so its build output loads straight from an extension folder or with `-e`. The older `session …` loader still accepts them too.

:::note[The older package loader]
The `pisharp session …` commands keep an older, Windows-only loader with a signed manifest, defined in [`schemas/pisharp-extension.schema.json`](https://github.com/jkelly/PiSharp/blob/main/schemas/pisharp-extension.schema.json), and an approval file. Plain `pisharp` doesn't use it.
:::
