# Public close after automatic MCP retirement

Source-only successor of `23a86941c14727aab952d74944131e3cf08f4285`.

After automatic owner retirement, `McpPreOpenServerCapture.CloseAsync()` previously selected the completed withdrawal body before the registered owning-resource receipt. A failed physical channel stop with successful withdrawal could therefore appear successful to the public caller. Public close now always selects the owning-resource receipt when bound. That receipt already exists before automatic stop initiation and remains available after the attachment retires. Repeated public close retains the same task and original fault objects.

The transferred runtime wrapper still joins the actual bound body without duplicating faults already retained by the owner. Initial binding fault aggregation and unbound cleanup are unchanged.

Two additional synthetic groups (eleven total capture groups) were authored: first public close after completed automatic shutdown with failed stop/successful withdrawal; and repeated public close after failed stop/failed withdrawal. Both inspect the owning shutdown and public close independently, checking each original leaf exactly once, task/fault identity, one physical stop, and expected withdrawal state. The existing automatic successful shutdown control remains applicable.

Validation: source inspection and `git diff --check` only. No build, test execution, native process, live request, package acquisition, credentials, security policy change, or shared runner edit. The task-13 review file was unavailable at its supplied path during authoring; this change addresses the coordinator's supplied finding, not a claim of independently reading that artifact.
