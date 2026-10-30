# Review a diff fixture

Find an introduced API key and risky missing null check.

Run `dotnet run --project samples/agents/24-PullRequestReview` for the fixture demo, or append `--self-test` for the deterministic tool check. The demo writes artifacts to the runner workspace. `--input` accepts a replacement JSON fixture and `--workspace` selects the output directory.

The fixture is illustrative; no repository or external service is changed.

The scanner demonstrates two narrow checks on added diff lines. It is not a comprehensive security or correctness review.
