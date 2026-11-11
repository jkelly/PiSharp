# C-SUR native implementation

Source basis: frozen `b4afdddf44e059c67ccf5097e5317d913521b470`, `packages/chord/src/surfaces/*`, and the approved `sync-1.5.2-chord-discovery.md` contract. Implemented in `src/PiSharp.Chord/Surfaces` with tests in `tests/PiSharp.Chord.Tests/Surfaces`. This work package did not run builds or tests: root owns serialized validation. The requested service tier was not independently verifiable.

## Implemented behavior

- `Scripts`: JSON DTOs and actual v1 remote `WebScripts`/`TuiScripts` contracts on the server address, state member `set`.
- `LoadedFacets`: digest identity reuse, complete-set reload serialization, preparation and host failure retention, removal, and adoption of `aborted.details.committed` outcomes.
- `Registered`: explicit trusted native registration callbacks, module name allowlist, facet export validation, plus source-wrapper emission for a separate JavaScript builder. It does not evaluate JavaScript or sandbox native code.
- `ConnectionLoop`: injectable clock/timer/online/wake environment, 250 ms exponential retry capped at 5 s, 10 s stable reset, 20 s connecting timeout, 25 s silence plus 1 s wake grace, cancellation of owned timers, asynchronous retry decisions and policy rejection. `LoopEnvironment` is a concrete .NET timer implementation and accepts platform wake notifications.
- `Client`: a real Chord client host, shared close listeners, parent link reuse, server base check before reconnect, remote script-set watch, serial updates, identity validation of fetched scripts, actual accepted-script tracking, retained last-good plugins, newest-set retry after fetch failures, retry on reconnect after plugin failure, CSS prepare/settle adapter hooks, outdated notification and idempotent stop.
- `Themes`: registry/facet, base roles and dark/light fallbacks, selected mode/id fallback, web custom-property mapping, terminal overrides, truecolor and nearest xterm-256 conversion.
- `Preferences`, `ThemeChoice`, `LocaleChoice`: canonical per-principal persisted documents and live derives, shared by clients, bounded to the latest 10,000 choosers. The two choices have independent stores; both survive plugin reload.
- `Messages`, `SurfaceTranslations`, `Languages`, `Format`: catalogs with native signature validation; primitive message data/schema; deterministic plugin-name precedence and explicit overrides; invalid locale/conflict reporting; locale chain and first-catalog fallback; failed-contribution fallback; unknown message keys; once-per-key diagnostics; preferred locale selection; text direction; synchronous thread-local render scope and native locale-aware numbers/times.
- `ActiveTheme`, `ActiveLanguage`: follow registry and remote preference changes and device/mode changes; consume each native watch's acquisition value explicitly (native `Start` only delivers subsequent frames), avoiding missed initial preferences. Active language lives in host page state.
- `PageStates`: arbitrary typed local state by host/key, identity-based notifications, retained across facet reload and cleared at host close.

## Platform dispositions and seams

The native runtime does not implement browser DOM nodes, HTML `lang`/`dir` writes, browser network/visibility listeners, React hooks, ESM/CommonJS execution, Node permissions or secure execution of downloaded DLLs. These are explicitly documented as platform-specific in `Surfaces/README.md`; no struct is presented as a browser hook. `IClientStyles` requires an actual platform adapter. Registered delegate allowlisting restricts package lookup only and is not a security sandbox. .NET culture data/format patterns provide native locale behavior; exact `Intl` formatting options are a JavaScript adapter concern.

C-PUB owns actual publication and `ScriptsFacet`, using `ScriptSet` and `ScriptEntry` as agreed. C-JS/terminal owns `TerminalScope` and optimistic shown-action lifetime, consuming `PageStates`, `ActiveLanguage.HostLanguage`, `Messages.In` and `Themes.TerminalColor`. App server core facets are `Themes.Facet()`, `ThemeChoice.Facet()`, `LocaleChoice.Facet()`, `SurfaceTranslations.Facet()`.

## Authored verification cases and root execution

Case prefix `surfaces.`. Five deterministic reconnect tests cover retry limits/reset, timeout/offline wake, wake grace, policy stop and wake during an asynchronous decision. Eight portable tests cover DTO/registered validation, loaded identity/failure recovery/removal, page-state lifetime, themes/colors, runtime catalog signatures/message data, locale/translation precedence/diagnostics and render isolation. Four integration cases use real Chord hosts/links (and a SQLite-backed in-memory store for preferences) to exercise canonical identity sharing/isolation, persisted/live choices, initial active preference snapshots and live changes, failed newest plugin recovery on reconnect, and concurrent update queueing against the host dispatcher. These are authored native regressions, not captured upstream test results.

Root-owned validation passed all 17 surfaces cases within the full 1024/1024 Chord run, with stable source and without temporary diagnostic timing: `artifacts/gap-validation/sync152-chord-final-cd0fb209ba7f4e7789975ba2755cb1ab/receipt.json`. This work package launched no builds itself.

Validation uncovered and repaired two native integration defects: initial member watches must wait for a ready reference, and a synchronous update must never enter the host dispatcher while holding the client queue lock. Queue admission remains atomic under its lock, while execution starts after the lock is released. A deterministic barrier test puts an external synchronous fetch and a dispatcher-origin update on either side of that former inversion. The reconnect integration also waits for actual retry-timer admission before advancing virtual time. Test server hosts supply the principal required for remote serving and assert successful facet loading.
