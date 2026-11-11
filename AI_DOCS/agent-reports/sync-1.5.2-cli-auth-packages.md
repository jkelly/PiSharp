# I-CLI-AUTH-PKG implementation

Source: frozen upstream `upstream-v152`, tag 1.5.2, commit `b4afdddf44e059c67ccf5097e5317d913521b470`. Implements the approved D-CLI auth/package scope. Requested high reasoning / ultrafast; effective service tier unverified.

## Changes

- Shell login refreshes only the signed-in provider after saving credentials, with a 15-second cancellation deadline. A failed, cancelled, or timed-out refresh reports a diagnostic without undoing a successful login. The refresh reads the actual login store, including an injected store. Credential writes still complete through the existing `StoredOAuthLifecycle`; no separate in-memory synchronization authority was added.
- Unknown shell logout now fails and lists sorted stored provider IDs. Known providers with no saved credential still succeed. API-key removal and OAuth logout have distinct messages. Provider references and displayed authentication URLs replace terminal control characters.
- `Mcp/RadiusMcpServer.cs` is the shared plan/apply helper for shell login and interactive offers. It reads only global configuration, reuses the existing Radius URL regardless of its name/trailing slashes, preserves other fields, removes MCP OAuth configuration when provider auth replaces it, and avoids namespace collisions. Shell Radius login applies it for both API-key and OAuth login. UI binding is coordinated with I-CLI-UI, which owns the interactive files.
- MCP shell listing includes `authProvider`; the command resolves provider credentials from its configured agent directory and supplies the same per-server refresh lock to interactive OAuth sign-in as connection refreshes use. Generic OAuth state-machine changes remain owned by I-MCP.
- Package updates emit `result` progress events only for changed npm/git identities, after concurrent work settles, in configured order. Git versions display seven characters; project entries have `(project)`. CLI aggregates now distinguish changed, already current, warnings, and offline; the richer native per-package receipt remains available separately.
- Model catalog updates warn and continue for typed OAuth refresh/minimum-validity failures. Credential-store, synchronization, and other catalog errors still fail. This consumes the existing `OAuthLifecycleException` contract agreed with the model-refresh owner.
- llama.cpp ordinary and SSE connection setup use one request helper that names the normalized server URL and the underlying network cause. HTTP status failures, caller cancellation, and timeout classification remain distinct.

## Authored regressions and registration

No builds or dotnet test processes were launched; root owns serialized validation. Scoped `git diff --check` passed.

Root must register (requested directly):

- `tests/PiSharp.CliParity.Tests/Program.cs`: `cases.AddRange(Sync152AuthPackageCases());`
- `tests/PiSharp.ExtensionParity.Tests/Program.cs`: `cases.AddRange(Sync152PackageCases());`

Six CLI case groups cover unknown logout/control stripping/credential kind, shared Radius plan and shell application, refresh failure/cancellation/timeout, llama ordinary+SSE transport diagnostics/cancellation, per-agent MCP tokens/JSON, and stale-OAuth warning with real package command integration plus fatal non-OAuth errors. Two extension case groups cover concurrent package completion order/result identities and aggregate outcomes. Existing package e2e/CLI expectations were updated to the reviewed 1.5.2 output format.

No changes to the generic authentication adapter, TUI rendering/selection, subscription policy, or remote-catalog family architecture were made.
