# Audit project map

- Product source is a C#/.NET 10 monorepo; exact SDK 10.0.401. Root is E:/AI/PiSharp/PiSharp, origin https://github.com/jkelly/PiSharp.
- Current checkout is old and dirty; audit uses isolated archive of origin/main 14dd34b. No checkout, reset, merge or product edit was performed.
- Contracts/Schema + Agent validate tools; AI owns provider protocols/catalog metadata; CodingAgent and Sessions own persistent prompts/history; CLI binds models, auth, MCP, extensions and RPC; Tools/Codemode own execution.
- Durable owns transaction/session/harness contracts; Durable.Sqlite persists storage; Durable.Hosting and CustomAgents wrap policy; Durable.Store/Chord remain partially implemented and unpacked.
- Tests are console executables: use dotnet run for their documented arguments. Merely building a solution or invoking dotnet test is not a full execution gate.
- Main NuGet.Config clears sources; six exact SQLite package versions were copied from the existing local cache into the isolated audit cache for one offline probe. No package versions changed.
- Relevant inventory: compatibility/sync-1.5.0-applicability.json, durable-port-map.json, durable-sync-1.5.0-worklist.json; release notes document important intentional differences.
- Commands and actual results are in verification.md. Selected runtime probes are narrow; no full platform/provider/concurrency parity claim is made.
