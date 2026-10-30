# Validate a translation fixture

Detect missing keys, placeholder mismatches, and write a review sheet.

Run `dotnet run --project samples/agents/26-LocalizationAssistant` for the fixture demo, or append `--self-test` for the deterministic tool check. The demo writes artifacts to the runner workspace. `--input` accepts a replacement JSON fixture and `--workspace` selects the output directory.

The fixture is illustrative; no repository or external service is changed.
