# PiSharp

PiSharp is an independent native .NET implementation of bounded Pi behavior targeting the public Pi **v0.99.1** source baseline. This repository preserves its original public planning history and adds a source snapshot. It is preview implementation source; complete upstream parity and release readiness remain open.

The snapshot includes native contracts, AI adapters, Agent, tools, sessions, coding-agent profile, extension APIs/runtime, RPC, terminal UI, CLI, optional Node compatibility source, schemas and examples. Azure Responses and Mistral Conversations are bounded explicit-configuration adapters; no credentials, catalog authority or live API qualification is supplied.

## Build and public offline checks

Use SDK **10.0.401**, pinned in [global.json](global.json) with roll-forward disabled. Projects target `net10.0` and have no external PackageReference dependencies. [NuGet.Config](NuGet.Config) clears package feeds; matching SDK reference packs must already be available.

The public solution contains all 14 production projects and two self-contained fake-HTTP test projects. From the repository root:

```powershell
dotnet restore PiSharp.slnx --locked-mode --configfile NuGet.Config
dotnet build PiSharp.slnx --configuration Release --no-restore
dotnet tests/PiSharp.AzureResponses.Tests/bin/Release/net10.0/PiSharp.AzureResponses.Tests.dll
dotnet tests/PiSharp.MistralConversations.Tests/bin/Release/net10.0/PiSharp.MistralConversations.Tests.dll
dotnet src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll --help
```

The two test products contain 15 Azure and 23 Mistral authored offline groups using injected fake HTTP handlers. They make no live provider requests. No passing result on this new snapshot is claimed before its fresh checks complete.

Private development history, acquired archives, genuine reference captures/goldens and operational qualification records are excluded. The original complete native/reference gate depends on those omitted inputs, so these commands do not reproduce it. See [source scope and qualification limits](docs/public-source-scope.md).

Optional Node worker source is included for separately configured compatibility work. Node/npm acquisition, optional worker execution and module/package parity are outside these native commands. This snapshot publishes no binary package or release.

## Plans and attribution

The [original planning index](docs/plans/README.md) remains available. Planned work, historical acceptance labels and current bounded observations are distinct. [Architecture](docs/architecture.md), [schemas](schemas), and [samples](samples) provide context.

PiSharp uses [MIT](LICENSE). Copied or substantially adapted Pi source retains its pinned MIT attribution in [third-party notices](THIRD-PARTY-NOTICES.md). Those notices do not establish redistribution clearance for omitted compiled/vendored/generated archives. This is an independent port, not an official upstream release.
