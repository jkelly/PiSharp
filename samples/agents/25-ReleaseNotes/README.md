# Group commit messages

Build user-facing notes from categorized commits.

Run `dotnet run --project samples/agents/25-ReleaseNotes` for the fixture demo, or append `--self-test` for the deterministic tool check. The demo writes artifacts to the runner workspace. `--input` accepts a replacement JSON fixture and `--workspace` selects the output directory.

The fixture is illustrative; no repository or external service is changed.
