# Reproduce and fix a tiny bug

Locate a wrong comparison, prepare a patch, and verify cases.

Run `dotnet run --project samples/agents/23-BugInvestigation` for the fixture demo, or append `--self-test` for the deterministic tool check. The demo writes artifacts to the runner workspace. `--input` accepts a replacement JSON fixture and `--workspace` selects the output directory.

The fixture is illustrative; no repository or external service is changed.

The patch verifies the supplied boundary cases by evaluating the comparison; it does not compile arbitrary C# input.
