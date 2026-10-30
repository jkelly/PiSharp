# Client Meeting Briefing

Fetch account and opportunity data concurrently, then assemble a factual meeting brief.

```powershell
dotnet run --project samples/agents/14-ClientMeetingBriefing
dotnet run --project samples/agents/14-ClientMeetingBriefing -- --self-test
dotnet run --project samples/agents/14-ClientMeetingBriefing -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `fetch_meeting_context` then `build_meeting_brief` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `build_meeting_brief` derives a result from the fixture after `fetch_meeting_context` examines source data. Replace the fixture using `--input` to test other records.

The fixture brief totals **$57,000** across two opportunities and shows the latest activity, “Demo completed.” `fetch_meeting_context` loads account, opportunity, and activity records concurrently inside the tool, even though the agent calls its tools sequentially. The brief makes no external CRM calls.
