# C-A3 app archives implementation

Source oracle: `upstream-v152` commit `b4afdddf44e059c67ccf5097e5317d913521b470`, `packages/chord/src/node/app/{archive,tar,data,app}.ts`.

The discovery report's format-1 summary is stale: the frozen source uses format **2**, includes mandatory `server.archive`, supports replace imports, and retains the installed directory and any prior directory after an unknown server commit outcome. The implementation follows the frozen source; root approved this discrepancy.

Implemented `App/Archive.cs`, `App/Tar.cs`, and `App/HostData.cs`. The public `AppArchiveService` coordinates live worker RPC or a supervisor-owned idle lease; the latter also reserves the supervisor entry against starts. `Archive.PackStoreAsync` consumes the store's consistent export queue, writes private files under an adjacent private staging directory, then renames the tar. Completed files are read one descriptor at a time; dot paths/uploads and reparse entries are excluded. Store and file snapshots intentionally need not represent one filesystem point.

Import charges entry count and header sizes before materializing bytes, requires the manifest first, rejects duplicates/foreign entries, and accepts only regular files/directories. The custom ustar codec rejects links, devices, PAX/GNU extension headers, bad checksums, malformed octal, unsafe paths and truncation. It does not delegate to System.Formats.Tar because its automatic extension-header handling would expand this dialect's trust boundary. Windows ADS/device/path alias names are additionally refused.

Store import occurs in private adjacent staging. The server archive is validated and its source address checked against the manifest before installation. Server import rewrites host ownership for the target address, runs after directory installation, and is the last consequential mutation. Definite refusal restores the prior directory; unknown `unavailable/storage_failed` preserves new and old directories. Successful cleanup is best effort. Address registration is left to the caller.

`HostData.Type` is the private `chord.app.data` remote contract; `HostData.CreateFacet` admits only the canonical server principal. Application startup owns integrating that facet into each worker.

Tests in `tests/PiSharp.Chord.Hosting.Tests/ArchiveCases.cs`, entry point `ArchiveCases.RunAsync()`, cover tar round-trip, traversal/absolute/empty/backslash/NUL paths, checksums, type/PAX refusal, truncation, a real open-store snapshot through the private endpoint, canonical principal authorization, hidden-upload omission, limits, failed store import cleanup and staging installation. An idle supervisor integration case covers export, remap import, default refusal, replace, cancellation and delete. No builds or tests were run by this owner; root owns serialized verification. Separate process/live-worker orchestration and unknown-commit fault injection remain integration verification responsibilities.

Requested high/ultrafast execution tier cannot be independently verified by this agent.
