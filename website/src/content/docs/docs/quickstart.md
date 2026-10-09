---
title: Quickstart
description: Install PiSharp and run it.
---

## Install

PiSharp's CLI is a .NET tool. It needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet tool install -g PiSharp.Cli
```

Then run it in your project folder:

```powershell
cd my-project
pisharp
```

`pisharp` opens the terminal UI, as `pi` does. Type `/login` to sign in to a provider, or set an API key such as `ANTHROPIC_API_KEY` or `OPENAI_API_KEY` first. Type `/model` to pick a model.

For a one-shot answer, use print mode:

```powershell
pisharp -p "Summarize this repository"
```

If you already use Pi, PiSharp finds your sign-ins, settings, sessions and resources in `~/.pi/agent`. See [Coming from Pi](/docs/coming-from-pi/).

### Update or uninstall

```powershell
dotnet tool update -g PiSharp.Cli
dotnet tool uninstall -g PiSharp.Cli
```

The libraries are separate NuGet packages. See the [SDK](/sdk/).

## Build from source

You need **.NET SDK 10.0.401** exactly: `global.json` pins it with roll-forward disabled.

The committed `NuGet.Config` clears all package feeds. The CLI build needs its third-party packages (YamlDotNet, SkiaSharp and Jint) from a local cache, or from nuget.org added as a source.

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
- [Command line](/docs/reference/cli/)
- [Providers and models](/docs/guides/providers/)
- [Interactive mode](/docs/guides/interactive/)
