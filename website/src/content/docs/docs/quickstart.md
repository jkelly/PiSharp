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

```powershell
git clone https://github.com/jkelly/PiSharp.git
cd PiSharp
dotnet build src/PiSharp.Cli/PiSharp.Cli.csproj -c Release -p:RestoreSources=https://api.nuget.org/v3/index.json -p:RestoreLockedMode=false
dotnet src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll --help
```

[Build from source](/docs/contribute/build/) covers the prerequisites, the restore settings, the samples and how to reference the projects from your own app. [Tests](/docs/contribute/testing/) lists the test suites and how to run them.

## Next steps

- [Coming from Pi](/docs/coming-from-pi/)
- [Command line](/docs/reference/cli/) and [Settings](/docs/reference/settings/)
- [Providers and models](/docs/guides/providers/)
- [Interactive mode](/docs/guides/interactive/)
- [Use the SDK](/docs/embed/sdk/) to embed PiSharp in your own app
