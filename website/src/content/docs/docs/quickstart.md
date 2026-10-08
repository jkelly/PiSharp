---
title: Quickstart
description: Install PiSharp and run it.
---

## Install

PiSharp's CLI is a .NET tool. It needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet tool install -g PiSharp.Cli
```

Then run it:

```powershell
pisharp --help
```

### Update or uninstall

```powershell
dotnet tool update -g PiSharp.Cli
dotnet tool uninstall -g PiSharp.Cli
```

The libraries are separate NuGet packages. See the [SDK](/sdk/).

## Build from source

You need **.NET SDK 10.0.401** exactly: `global.json` pins it with roll-forward disabled.

The committed `NuGet.Config` clears all package feeds. The default CLI build uses YamlDotNet for prompt-template frontmatter, so it needs that package from a local cache, or from nuget.org added as a source.

```powershell
git clone https://github.com/jkelly/PiSharp.git
cd PiSharp
dotnet build src/PiSharp.Cli/PiSharp.Cli.csproj -c Release -p:RestoreSources=https://api.nuget.org/v3/index.json -p:RestoreLockedMode=false
dotnet src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll --help
```

The committed lock files still record the sibling projects at their pre-0.99.1 version, so the build above restores without lock-file checking. This matches what the release pipeline does.

## Run the offline tests

These test projects use fake HTTP handlers and make no live provider requests:

```powershell
dotnet run --project tests/PiSharp.AzureResponses.Tests -c Release -p:RestoreLockedMode=false
dotnet run --project tests/PiSharp.MistralConversations.Tests -c Release -p:RestoreLockedMode=false
```

## Next steps

- [Coming from Pi](/docs/coming-from-pi/)
- [Build an extension](/docs/extensions/build-an-extension/)
