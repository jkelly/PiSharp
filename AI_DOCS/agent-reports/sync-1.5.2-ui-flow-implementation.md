# Additional interactive UI contracts — implementation

Source: frozen Pi v1.5.2 (`upstream-v152`), reviewed contracts in `sync-1.5.2-tui-discovery.md`. All changes are native terminal behavior.

Implemented:

- `InteractiveMode.Commands.cs` / `Events.cs`: pending user bash components inherit current expansion for both direct execution and history rendering; toggles visit pending output; queue refresh reattaches pending bash before prompts; settled flushes once and redraws; full session rebuild clears pending references.
- `InteractiveMode.ExtensionHost.cs`: each blocked custom overlay owns an independent program-status source until retirement, including hidden overlays. Retirement is idempotent, closes its own overlay handle, and clears only its status source. Reset retires every extension-owned custom component and completes its task. `cli_ui` wired `ResetCustomComponents` into `ExtensionUi.ResetExtensionUI` in place of hiding an arbitrary topmost overlay. Built-in overlays remain intact. Mount/handle failures run the same cleanup.
- `Events.cs`: adjacent cache-warming entries with the same note reuse the same themed component and sum costs in a cloned entry for the existing formatting callback. Intervening content or different note starts another notice; display settings remain respected.
- `Utilities/Changelog.cs`: explicit `TryParseVersion` result retains whitespace/v-prefix/prerelease handling and native fourth version component. Startup and extension changelog callers repair invalid markers with installed version without replaying notes; startup repair does not invoke installation telemetry.
- Expanded draft transfer applied in `ExtensionHost.cs` and queued-message restoration in `Events.cs`; the editor interface already supplies plain-text fallback. `cli_ui` owns the corresponding `ExtensionUi` replacement path.
- Transformer integration coordinated with `markdown_transforms`: summary/custom/skill constructors receive transformers and persisted metadata. Shared changelog Markdown factory excludes app headings and is used by `/changelog`; startup callers wired by `markdown_transforms`.

Authored regression file: `tests/PiSharp.InteractiveParity.Tests/Sync152UiFlowCases.cs`.

Registration: automatically discovered by the existing static `*Cases.All()` reflection registry; no `Program.cs` edits required. Filter: `INTERACTIVEPARITY_FILTER=sync152.ui`.

Cases:

- `sync152.ui.invalid-changelog-marker-repair`
- `sync152.ui.cache-warm-adjacent-notices`
- `sync152.ui.pending-bash-expansion-queue-settle`
- `sync152.ui.overlay-blocked-hidden-reset`
- `sync152.ui.changelog-transform-factory`

Validation: targeted `git diff --check` passed. No builds or test executions run by this worker, per serialized-build instruction; root owns compilation and execution. These are authored native assertions, not upstream runtime captures. Effective requested ultrafast service tier is unverified.
