# D2: client and operation discovery

| Field | Result |
| --- | --- |
| Status | complete |
| Model / effort | gpt-6-sol / medium |
| Changed files | none; read-only explorer |
| Commands | Targeted `rg`, `Get-Content`, `git status --short`; decision 0009 and relevant source/tests |
| Blockers | none for planning |

`JobDefinition.ResultPath` points into shared App State; depth 24 / 100,000 characters makes it suitable for a latest-result reference, not a growing artifact corpus. Use retained per-run conversation records like `Jobs.RecordDoc`. Extend execution detail with bounded artifact/evidence links. Existing rendering uses Razor encoding, `MarkdownRenderer` with raw HTML disabled/sanitized, and escaped dynamic fields in `automation.js`; preserve those boundaries for typed cards.

General feedback/preferences have no domain/routes; existing job approval is distinct. Define target/actor binding, event idempotency, explicit scope, revisions, undo and deletion. `DeviceService.Capabilities` is flat strings; Browser supports schedules/executions while terminal entry supports local durable, serve and token. Remote attach and parity management commands remain absent.

Hub `DeviceRegistry` encrypts device credentials and `DeviceProxy` handles authenticated forwarding; keep tokens out of browser scripts. Reuse Client streaming/reducers for remote attach, explicitly verify device identity/capabilities, preserve pending request IDs, and avoid opening local locks for remote sessions.

No doctor or service installation paths exist; decision 0008 defers them. Add read-only redacted diagnostics, explicit optional probes/repairs, foreground lifecycle integration and Windows/systemd/launchd templates with real OS evidence. Relevant tests: Server `AutomationApiCases.cs`, browser `AutomationCases.cs`, CLI `RuntimeCases.cs`. Follow-up: freeze artifact/feedback/manifest contracts and remote cancellation/reconnect tests. Incorporated into artifacts, feedback, C1–C3 and O1–O3 packages.
