---
title: Build an extension
description: Write a native PiSharp extension in C#.
---

A native extension is a .NET class library that implements `IPiSharpExtension`. When PiSharp loads it, it calls `InitializeAsync` with a registry, and the extension registers its tools, commands, shortcuts, renderers and hooks.

:::caution[Experimental]
The native package loader and its manifest format are experimental and may change before they're frozen.
:::

## Project file

Reference the extension contracts without copying them into your output, and enable dynamic loading:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <GenerateRuntimeConfigurationFiles>true</GenerateRuntimeConfigurationFiles>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="path/to/PiSharp.Extensions.Abstractions.csproj" Private="false" ExcludeAssets="runtime" />
    <ProjectReference Include="path/to/PiSharp.Contracts.csproj" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

## Register a tool

This is the shape of the `StatefulTodo` sample, ported from Pi's `todo.ts` example:

```csharp
using PiSharp.Contracts;
using PiSharp.Extensions;

public sealed class StatefulTodoExtension : IPiSharpExtension
{
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        registry.RegisterTool(new("todo", "todo", Description,
            JsonData.Parse(ParametersJson), ExecuteAsync));
        return ValueTask.CompletedTask;
    }

    // ExecuteAsync(JsonData arguments, IExtensionToolContext context, CancellationToken token)
}
```

Tool parameters are declared as JSON Schema. The host validates arguments against the schema before your tool runs.

## Keep state on the session branch

Don't keep mutable state in fields. Rebuild it from the current branch's session snapshot each time the tool runs. Your state then stays correct when the user branches or navigates the session tree. `StatefulTodo` shows the pattern.

## Samples

- [StatefulTodo](https://github.com/jkelly/PiSharp/tree/main/samples/extensions/StatefulTodo): a tool whose state is rebuilt from the session branch.
- [SessionCheckpoint](https://github.com/jkelly/PiSharp/tree/main/samples/extensions/SessionCheckpoint): a `/checkpoint` command, a session-start observer and a pre-switch handler.

The manifest format is defined in [`schemas/pisharp-extension.schema.json`](https://github.com/jkelly/PiSharp/blob/main/schemas/pisharp-extension.schema.json).
