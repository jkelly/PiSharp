# C-A1 — native application bootstrap and authentication

Implemented against frozen `upstream-v152` commit `b4afdddf44e059c67ccf5097e5317d913521b470`, including its final `node/app` source. New public namespace: `PiSharp.Chord.Hosting.App`.

## Behavior

- `App.StartServerAsync` validates owner/server canonical inequality and worker principal configuration before opening resources, takes the canonical application-directory lock, opens the private SQLite store, assembles required built-in and optional user plugin roots, then loads the server and listens on loopback/port zero by default. Application code can select additional core facets through `AppOptions.CoreFacets`.
- The returned app owns HTTP, worker groups, plugin managers/runtime, files, session pin, store, surface builds and the directory lock. Its close task remains owned if one caller cancels its wait. A callback-lifetime registration counts actual original callbacks and native resources, including core facets; after the host's bounded public close, app cleanup waits for that count to reach zero before unloading plugins, closing files/SQLite or releasing the lock. Partial starts use the same cleanup path. Applying changes is serialized, server first, worker kinds next, then surface publication.
- Worker and surface base builds start after listening, have cached independent outcomes and report `base_build` without rejecting the whole application. Their exact build inputs join the base watcher. The server graph is a bounded best-effort snapshot of file-backed loaded assemblies and explicit `Base` roots taken at startup. A subsequently dynamically loaded assembly must be included explicitly; this does not prove a complete dynamic native dependency graph.
- `Layout` refuses the filesystem root, the user's home and directories owned by another user, resolves canonical lock identity, creates private descendants without changing existing ancestors, and protects directories/files with Unix 0700/0600 or Windows current-user/SYSTEM ACLs. SQLite sidecars are checked both before open and after schema initialization. Address directories encode uppercase, separators and dots and hash long keys.
- Sessions use the same persisted Chord singleton document in SQLite as upstream, never a separate credentials JSON file. Only SHA-256 hashes are stored. Tokens have a 12-byte random ID and 32-byte random secret, strict input parsing and fixed-time hash comparison. Browser/device sessions survive restart, expire after 400 idle days and persist last use at most once per minute. Cookies are signed, HttpOnly, Strict and Secure for remote provenance, renewed after one day. Ephemeral sessions are memory-only, bounded, excluded from listings and expire after a minute with no initial or remaining link; reopening a link cancels expiration.
- The owner fragment endpoint consumes a ten-minute token once, clears the fragment before posting and sets a cookie. The private owner token file reuses only a valid canonical same-owner session; an owner change revokes the old file session. Pairing admits at most 100 anonymous requests/10 per real peer by default, hashes polling credentials, reserves approval atomically, and returns the device credential once. Owner-created requests are exempt from anonymous caps.
- `Users.Define`, `Invite` and `EphemeralLogin.CreateFacet` are exported opt-in facets. Invites expire after seven days, are single-use and bounded. User profile schemas are applied by the actual Chord contract/document validation.
- `WorkerApp` validates its private parent envelope, opens its own private SQLite/files, connects the parent before plugin startup, installs information/files/archive/translations, and uses an in-memory telemetry recorder with a one-second sender when server ingestion is enabled. It never opens the server telemetry SQLite store. Worker close hooks flush telemetry before host/link close and release files/store after original host/runtime cleanup.
- App archive methods delegate to C-A3. The frozen source uses archive format 2 with `server.archive`; the earlier discovery narrative's format 1 is superseded by that source. C-A2 file persistence and C-A3 tar validation are separate ownership areas.

## Verification handoff

No local build or test process was launched; root serializes all .NET validation. A whitespace audit of all new owned source files passed. Register these hosting executable cases:

- `AppLayoutTests.RunAsync`
- `AppAuthRouteTests.OwnerLinksAndCaps`
- `AppAuthRouteTests.RoutesAndInvites`
- `AppStartTests.StartupAndRecovery`
- `AppStartTests.SessionExpiryAndEphemeralLinks`
- `AppStartTests.BaseBuildIsolation`
- `AppStartTests.TelemetryShutdown`
- `AppStartTests.CleanupRetainsStoreAndLock`

They cover real SQLite restart/revocation, throttled persisted touches, strict malformed tokens, ephemeral caps/timers/reconnect, real loopback startup, private-route admission, owner-token replay, required-root failure/cleanup/restart, cancelled close wait, isolated base failure, owner-file reuse/change, pairing/approval caps and races, optional login/invites, directory lock/ACL/modes, symlinks and atomic secret writes. The telemetry shutdown regression holds ingestion indefinitely until reference release and requires bounded shutdown; the periodic loop cancels only its public wait, while the sender owns the original send until `Stop` releases its reference. Worker process snapshot/link-loss tests remain C-WK's cases; these are not substitutes for them.

Native adaptation: app API sharing is explicit assembly identity plus policy; native assemblies do not execute in a browser or provide a permission sandbox. Surface builders are the explicit C-PUB artifact adapter. Requested ultrafast service tier was not independently verifiable.
