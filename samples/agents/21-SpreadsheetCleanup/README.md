# Clean a contacts CSV

Normalize names and emails, remove duplicate contacts, and summarize quality issues.

Run `dotnet run --project samples/agents/21-SpreadsheetCleanup` for the fixture demo, or append `--self-test` for the deterministic tool check. The demo writes artifacts to the runner workspace. `--input` accepts a replacement JSON fixture and `--workspace` selects the output directory.

The fixture is illustrative; no repository or external service is changed.

The CSV parser handles simple three-column rows without quoted commas or multiline fields.
