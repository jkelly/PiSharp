---
title: Build from source
description: Clone, build and run PiSharp and its samples.
---

You build PiSharp from source to work on it, to run its tests, or to use a feature that is on the `main` branch but in no release yet. Custom agents, the media tools and library, and service tiers are such features: to use them, you [reference the projects from your app](#reference-the-projects-from-your-app).

## Prerequisites

| Tool | Version | Needed for |
| --- | --- | --- |
| .NET SDK | 10.0.401 exactly | Everything. [`global.json`](https://github.com/jkelly/PiSharp/blob/main/global.json) pins it with `"rollForward": "disable"`, so no other SDK version builds it. |
| Git | Any recent | Cloning, and the test cases that run `git`. |
| Node.js | 22.13 or later, with npm | The [Node bridge](/docs/extensions/node-bridge/) for Pi's TypeScript extensions, the `ExtensionParity` suite, and the Node tools under `tools/`. |
| Node.js | 22 or later | The [website](/docs/contribute/website/). |
| PowerShell | 7 | `tools/release/build-release-packages.ps1` and the other release scripts. |

You need a network connection, or a NuGet cache that already holds the packages, for the third-party packages listed in [Restore and lock files](#restore-and-lock-files). The libraries and the CLI target `net10.0`.

## Get the source

```sh
git clone https://github.com/jkelly/PiSharp.git
cd PiSharp
```

`main` holds the latest work, including unreleased features. Each release has a tag, such as `v1.2.0.1`. Check out the tag to build exactly what shipped.

## Repository layout

| Folder | Contents |
| --- | --- |
| [`src/`](https://github.com/jkelly/PiSharp/tree/main/src) | The libraries and the CLI (`PiSharp.Cli`). See [Architecture](/docs/reference/architecture/). |
| [`tests/`](https://github.com/jkelly/PiSharp/tree/main/tests) | The test suites, which are console programs. See [Tests](/docs/contribute/testing/). |
| [`samples/`](https://github.com/jkelly/PiSharp/tree/main/samples) | Runnable sample apps and two native extensions. See [Samples](#samples). |
| [`benchmarks/`](https://github.com/jkelly/PiSharp/tree/main/benchmarks) | `PiSharp.Durable.Benchmarks`, ports of upstream's durable benchmarks. |
| [`tools/`](https://github.com/jkelly/PiSharp/tree/main/tools) | The Node bridge host scripts, the compatibility and integrity tools, and the release and packaging scripts. |
| [`compatibility/`](https://github.com/jkelly/PiSharp/tree/main/compatibility) | The Pi baseline locks, sync worklists, the durable port map and the release record. See [How the port tracks Pi](/docs/contribute/porting/). |
| [`fixtures/`](https://github.com/jkelly/PiSharp/tree/main/fixtures) | Captured and authored test fixtures. |
| [`docs/`](https://github.com/jkelly/PiSharp/tree/main/docs) | Design documents, decision records, plans and release notes. |
| [`schemas/`](https://github.com/jkelly/PiSharp/tree/main/schemas) | JSON schemas for extension manifests. |
| [`website/`](https://github.com/jkelly/PiSharp/tree/main/website) | This site. |
| [`third-party/`](https://github.com/jkelly/PiSharp/tree/main/third-party) | License files of the third-party code PiSharp ships. |

At the root, [`PiSharp.slnx`](https://github.com/jkelly/PiSharp/blob/main/PiSharp.slnx) lists every library, the core, companion and release test suites, the samples other than the extension samples, and the benchmarks. [`Directory.Build.props`](https://github.com/jkelly/PiSharp/blob/main/Directory.Build.props) holds the settings every project shares, and [`NuGet.Config`](https://github.com/jkelly/PiSharp/blob/main/NuGet.Config) clears every package feed.

## Build

Build one project and the projects it references:

```powershell
dotnet build src/PiSharp.Cli/PiSharp.Cli.csproj -c Release -p:RestoreSources=https://api.nuget.org/v3/index.json -p:RestoreLockedMode=false
```

The CLI lands in `src/PiSharp.Cli/bin/Release/net10.0/`. Run it with `dotnet`:

```powershell
dotnet src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll --help
```

To build everything in the solution, pass `PiSharp.slnx` instead of a project. The solution holds about 95 projects, so building one project is faster when that is all you need.

Every project gets these settings from `Directory.Build.props`:

| Setting | Value | Effect |
| --- | --- | --- |
| `VersionPrefix` | `1.2.0.1` | The version a build reports, unless you pass `-p:VersionPrefix`. A build of `main` reports it even when it holds unreleased features. |
| `TargetFramework` | `net10.0` | |
| `Nullable`, `ImplicitUsings` | `enable` | |
| `TreatWarningsAsErrors` | `true` | Any compiler warning fails the build. |
| `Deterministic` | `true` | |
| `RestorePackagesWithLockFile` | `true` | Restore reads and writes `packages.lock.json`. |
| `NuGetAudit` | `false` | Restore does not query vulnerability data. |

The CLI has one build switch of its own. `-p:EnablePromptTemplateYaml=false` builds it without `PiSharp.PromptTemplates.Yaml` and YamlDotNet, and restores against `packages.prompt-yaml-disabled.lock.json`. It defaults to `true`.

## Restore and lock files

The committed `NuGet.Config` clears every package source. A project with no third-party packages restores without any feed. A project that has them restores only when the packages are already in your NuGet cache, or when you name a feed, as the CI and release builds do:

```powershell
dotnet build src/PiSharp.Codemode/PiSharp.Codemode.csproj -p:RestoreSources=https://api.nuget.org/v3/index.json -p:RestoreLockedMode=false
```

These projects use third-party packages:

| Project | Packages |
| --- | --- |
| `PiSharp.Codemode` | Jint 4.16.3 (and Acornima, which Jint brings) |
| `PiSharp.PromptTemplates.Yaml` | YamlDotNet 16.3.0 |
| `PiSharp.Tools.Skia` | SkiaSharp 4.152.1 and its native assets for Windows, macOS and Linux |
| `PiSharp.Durable.Sqlite` | Microsoft.Data.Sqlite 10.0.12 and SQLitePCLRaw 2.1.12 |
| `PiSharp.CustomAgents.Scripting` | Microsoft.CodeAnalysis.CSharp 5.6.0 |

Each project commits a `packages.lock.json`. Some of them still record the sibling PiSharp projects at an older version, so the official builds restore with lock-file checking off (`RestoreLockedMode=false`). Lock-file checking is not on by default, but pass the switch anyway to match them.

Don't commit lock-file changes a restore makes as a side effect: delete untracked lock files it creates and revert tracked ones it rewrites. When you add or change a package reference on purpose, commit the updated lock file with it.

## Run the CLI from source

`dotnet run` builds the CLI and starts it. Arguments after `--` go to `pisharp`:

```powershell
dotnet run --project src/PiSharp.Cli -p:RestoreSources=https://api.nuget.org/v3/index.json -- --help
dotnet run --project src/PiSharp.Cli -- -p "Summarize this repository"
dotnet run --project src/PiSharp.Cli -- durable
```

Once the packages are in your cache, you can leave out `-p:RestoreSources`. The CLI built this way includes everything on `main`, including the CLI features that are in no release yet: `--tier` and `/tier`, and the media tools in `pisharp durable`. See [Command line](/docs/reference/cli/).

`dotnet run` starts in Debug. Add `-c Release` for the build the packages use. The build copies `CHANGELOG.md` next to the executable, where What's New and `/changelog` read it.

## Install your build as a tool

To run your build as `pisharp`, pack it the way a release does and install it from the output folder. The macOS CI job does exactly this.

```powershell
pwsh ./tools/release/build-release-packages.ps1 -Version 1.2.0.1 -SourceCommit (git rev-parse HEAD)
```

The script packs every library and the CLI into `artifacts/packages/`, and checks each package. It expects exactly 20 packages there when it ends, so empty the folder before you run it again. The version's first three numbers must match the Pi baseline in `compatibility/target.lock.json` (1.2.0 today), and `-SourceCommit` must be a full 40-character commit id. See [Releasing](/docs/contribute/releasing/#build-the-packages) for what it checks.

Then install the tool from a folder outside the repository, because the repository's `NuGet.Config` would hide nuget.org, where the tool's dependencies come from:

```powershell
cd ..
dotnet tool install PiSharp.Cli --tool-path ./pisharp-dev --add-source PiSharp/artifacts/packages --version 1.2.0.1
./pisharp-dev/pisharp --version
```

A `--tool-path` install keeps your build apart from a `dotnet tool install -g PiSharp.Cli` from NuGet.

## Reference the projects from your app

Features that are in no release yet have no NuGet package. To use them, add project references from your app to the PiSharp projects in your clone. This also works for any released library, when you want `main` or a local change.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- The experimental APIs this app uses. -->
    <NoWarn>$(NoWarn);PISHARP_DURABLE001;PISHARP_AGENTS001</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../PiSharp/src/PiSharp.CustomAgents/PiSharp.CustomAgents.csproj" />
    <ProjectReference Include="../PiSharp/src/PiSharp.Durable.Sqlite/PiSharp.Durable.Sqlite.csproj" />
  </ItemGroup>
</Project>
```

Your app must target `net10.0`. Reference every PiSharp library you use from source, and don't mix in PiSharp NuGet packages: the packages are built from a release and the projects from your clone, and the two can differ. Restore your app with `-p:RestoreSources=https://api.nuget.org/v3/index.json` if it can't find a third-party package that a PiSharp project needs.

Experimental APIs carry `[Experimental]`, and using one is a compiler error until you suppress its id:

| Feature | Projects | Suppress |
| --- | --- | --- |
| [Durable agents](/docs/embed/durable/) (released, experimental) | `PiSharp.Durable`, `PiSharp.Durable.Sqlite`, `PiSharp.Durable.Tools` | `PISHARP_DURABLE001` |
| [Custom agents](/docs/custom-agents/overview/) (experimental, unreleased) | `PiSharp.CustomAgents`, `PiSharp.CustomAgents.JavaScript`, `PiSharp.CustomAgents.Scripting` | `PISHARP_AGENTS001` and `PISHARP_DURABLE001` |
| [Media library](/docs/embed/media/) (experimental, unreleased) | `PiSharp.AI`, namespace `PiSharp.AI.Media` | `PISHARP_MEDIA001` |
| [Service tiers](/docs/guides/service-tiers/) (experimental, unreleased) | `ServiceTiers` in `PiSharp.AI`, and the tier members in `PiSharp.AI`, `PiSharp.Agent` and `PiSharp.CodingAgent` | `PISHARP_TIERS001` |

The durable and custom agents projects mark the whole assembly, so every type in them needs the suppression. The media and tier attributes sit on single types and members. An app inside the PiSharp folder, like the samples, also inherits `Directory.Build.props`, so its warnings are errors and restore writes a lock file for it.

## Samples

The samples reference the `src/` projects, so they always build against the code beside them. Run them from the repository root.

| Sample | What it shows | Run |
| --- | --- | --- |
| [`Durable.Chat`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Chat) | One prompt answered by a real provider through a durable session, on SQLite by default, with optional coding tools. | `dotnet run --project samples/Durable.Chat -- --provider anthropic --model <id> "<prompt>"` |
| [`Durable.Examples`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Examples) | Ports of upstream's durable examples on scripted models. Name examples by number or file name, or run them all. | `dotnet run --project samples/Durable.Examples -- [--storage memory\|jsonl\|sqlite] [example ...]` |
| [`CustomAgents.Examples`](https://github.com/jkelly/PiSharp/tree/main/samples/CustomAgents.Examples) (experimental, unreleased) | An orders workflow, a team, and web search, on scripted models unless you pass `--provider` and `--model`. | `dotnet run --project samples/CustomAgents.Examples -- [orders\|team\|web\|all]` |
| [`CustomAgents.Research`](https://github.com/jkelly/PiSharp/tree/main/samples/CustomAgents.Research) (experimental, unreleased) | A research workflow that writes a report. See its README. | `dotnet run --project samples/CustomAgents.Research -c Release -- "<question>"` |
| [`extensions/StatefulTodo`](https://github.com/jkelly/PiSharp/tree/main/samples/extensions/StatefulTodo) | A native extension with a `todo` tool whose state lives in the session. | See below. |
| [`extensions/SessionCheckpoint`](https://github.com/jkelly/PiSharp/tree/main/samples/extensions/SessionCheckpoint) | A native extension with a `/checkpoint` command that switches sessions. | See below. |

`Durable.Chat` reads the provider's API key from its environment variable, such as `ANTHROPIC_API_KEY`. The custom agents samples are covered in [Custom agents samples](/docs/custom-agents/samples/), and the durable ones in [Pi Durable](/docs/guides/durable/).

The extension samples are not in `PiSharp.slnx`. Build one, then load it for one run with `-e`:

```powershell
dotnet build samples/extensions/StatefulTodo/StatefulTodo.csproj -c Release
pisharp -e samples/extensions/StatefulTodo/bin/Release/net10.0/pisharp-extension.json
```

Or copy the `bin/Release/net10.0` folder into `~/.pi/agent/extensions/<name>/`, where it loads like any extension. See [Build an extension](/docs/extensions/build-an-extension/).

The durable benchmarks run the same way: `dotnet run -c Release --project benchmarks/PiSharp.Durable.Benchmarks -- storage`. [`docs/durable-benchmarks.md`](https://github.com/jkelly/PiSharp/blob/main/docs/durable-benchmarks.md) lists the benchmarks and their arguments.
