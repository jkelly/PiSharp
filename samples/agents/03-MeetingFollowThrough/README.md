# Meeting follow through

Meeting notes are local; follow-up drafts are returned, never sent.

Run: dotnet run --project samples/agents/03-MeetingFollowThrough -- --workspace artifacts/meeting-follow-through

Test: dotnet run --project samples/agents/03-MeetingFollowThrough -- --self-test

The demo extracts two action items from mtg-301 and produces an attendee follow-up draft. Use --input path.json for another dataset. --live selects the configured model provider; domain data still comes from the JSON adapter.
