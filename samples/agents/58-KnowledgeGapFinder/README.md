# Knowledge gap finder

Runs native PiSharp tools to inspect unresolved questions and zero-result searches, cluster them by supplied topic keywords, measure article text coverage, and save `knowledge-support-q4.json` with grounded draft article tasks. Source IDs are retained. Tied/unmatched signals remain explicitly unclassified; lexical coverage is an editorial aid, not semantic proof.

Run `dotnet run --project samples/agents/58-KnowledgeGapFinder -- --demo` or add `--self-test`. `--input` accepts the fixture schema; IDs must be unique. All output stays in the selected workspace. No external publishing occurs.
