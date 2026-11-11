# Native terminal client

`TerminalLauncherSession` owns the real terminal and privileged operations. `TerminalChannel` exposes input, resizes, framed output, requests and relayed Chord links to a caller-trusted C# client. `PiTerminalIo` adapts the existing `PiSharp.Tui.Pi.ITerminal`, including its raw-mode lifecycle. `TerminalRuntime.StartAsync` composes this bridge with the native `Surfaces.Client`: remote `TuiScripts` sets, reconnects, managed registrations, live themes and language, and an application render callback. `TerminalScope` owns a component's holds, watches, registries, shared page state, messages and optimistic state.

Construct a session with a `TerminalStart`, a `PiTerminalIo`, and trusted services (normally `HttpTerminalServices.ConnectAsync`). Call `session.Start()`, then `TerminalRuntime.StartAsync(session.Bridge, options)`. The options must supply a resolver of explicitly trusted `RegisteredScript` delegates; it does not evaluate the server's JavaScript or load downloaded assemblies. Dispose the runtime and session when finished. A UI can construct `TerminalChannel` and `BridgeTerminal` directly instead of using the runtime.

The runtime's `CopyAsync`, `OpenAsync` and `AttachAsync` ask through the trusted launcher's prompt. A prompt quarantines its first 300 ms of keys, holds bounded application output, uses explicit visible attributes, and refuses if it cannot fit. An attachment is looked up only after the initial path confirmation; the resolved opened file is reconfirmed when different, held across the prompt and read through the same handle. This avoids reopening a path after confirmation. Windows, Linux and macOS have concrete regular-file checks; other platforms refuse attachments.

`HttpTerminalServices` retains bearer credentials and constrains client fetches to same-origin terminal artifact paths. Its built-in HTTP client refuses redirects. Callers supplying their own `HttpClient` are responsible for equally restrictive redirect/credential behavior. Socket messages and downloads are bounded; `TerminalPipe` implements the upstream binary protocol without executing payloads. `TerminalOutput.Filter` is frame-local and strips all output except text and the documented drawing/mode allowlist.

Downloaded terminal JavaScript is **not available** in this native implementation. `TerminalLauncher.StartDownloadedAsync` throws before reading credentials, touching the terminal or downloading content. It requires the separate exact Node 26.5.1 permission-sandbox adapter; none is bundled here. A .NET assembly load context is not a permission sandbox. Browser DOM modules and React hooks likewise require actual platform adapters and are not implemented by native terminal APIs.

Upstream mapping at Pi `b4afdddf44e059c67ccf5097e5317d913521b470`:

| Upstream module | Native counterpart / disposition |
| --- | --- |
| `tui/bridge`, `channel`, `terminal`, `frames` | `Bridge`, `TerminalChannel`, `BridgeTerminal`, `TerminalPipe`, `TerminalOutput` |
| `tui/scope`, `theme` | `TerminalScope`, `TerminalOptimistic`, `TerminalTranslator`, `TerminalTheme` |
| `tui/runtime` | `TerminalRuntime` for trusted native registrations |
| `node/tui/launcher`, `filter`, `actions`, `credentials`, `pipe` | `TerminalLauncherSession`, `TerminalOutput`, `TerminalActions`, `TerminalCredentials`, `HttpTerminalServices`, `TerminalPipe` |
| `node/tui/sandbox` | Explicitly unavailable; early safe failure, no native substitute |

Limits: pipe payload 17 MiB, pipe JSON header 1 MiB, terminal frame 64 KiB, HTTP body 8 MiB, attachment 16 MiB, four relayed sockets, eight requests, 16 MiB queued per socket, and 1 MiB held output. Triple Ctrl-C within one second ends a session with status 130 and restores terminal modes.
