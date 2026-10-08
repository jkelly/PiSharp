---
title: Quickstart
description: Build PiSharp from source and run it.
---

:::note[Packaged install coming]
A packaged install is on the way. Until then, build PiSharp from source.
:::

## Requirements

- **.NET SDK 10.0.401.** The repository pins it in `global.json` with roll-forward disabled.
- Git.

PiSharp has no external NuGet package dependencies. `NuGet.Config` clears package feeds, so the matching SDK reference packs must already be installed.

## Build from source

```powershell
git clone https://github.com/jkelly/PiSharp.git
cd PiSharp
dotnet restore PiSharp.slnx --locked-mode --configfile NuGet.Config
dotnet build PiSharp.slnx --configuration Release --no-restore
```

## Run it

```powershell
dotnet src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll --help
```

## Run the offline tests

The repository includes self-contained test projects that use fake HTTP handlers and make no live provider requests:

```powershell
dotnet tests/PiSharp.AzureResponses.Tests/bin/Release/net10.0/PiSharp.AzureResponses.Tests.dll
dotnet tests/PiSharp.MistralConversations.Tests/bin/Release/net10.0/PiSharp.MistralConversations.Tests.dll
```

## Next steps

- [Coming from Pi](/docs/coming-from-pi/)
- [Build an extension](/docs/extensions/build-an-extension/)
