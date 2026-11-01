# Grant application organizer

Three native tools inspect source requirements/documents, validate explicit requirement links against kind, minimum count and expiry at the deadline, and write `grant-arts-2026.json`. Every accepted and rejected item retains its source ID. The fixture intentionally has an expired certificate and one missing letter; assembling an incomplete review package is a successful operation.

Run `dotnet run --project samples/agents/59-GrantApplicationOrganizer -- --demo` or `--self-test`. No grant portal, eligibility decision, or automatic submission is implemented. Metadata and evidence excerpts are supplied inputs; authenticity and grant interpretation remain human review tasks.
