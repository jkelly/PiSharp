# Daily work planner

Commitments and capacity come from the local fixture.

Run: dotnet run --project samples/agents/04-DailyWorkPlanner -- --workspace artifacts/daily-work-planner

Test: dotnet run --project samples/agents/04-DailyWorkPlanner -- --self-test

The demo lists three commitments and schedules the first two within 240 minutes. Use --input path.json for another dataset. --live selects the configured model provider; domain data still comes from the JSON adapter.
