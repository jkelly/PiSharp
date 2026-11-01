# Research workbench

Run `dotnet run --project samples/agents/28-ResearchWorkbench` or add `--self-test`.

The first tool persists a question and evidence claims in a native PiSharp v3 session log. The second tool reopens that log, uses `SessionBranchPlanner.Fork` at the evidence leaf, persists a new log with a follow-up question, and validates it with `SessionLogReader`. Source claims are supplied by the fixture; this sample does not browse or fact-check them.
