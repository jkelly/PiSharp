# Helpdesk resolution

Matches a local ticket to KB steps; it does not close or update a ticket.

Run: dotnet run --project samples/agents/09-HelpdeskResolution -- --workspace artifacts/helpdesk-resolution

Test: dotnet run --project samples/agents/09-HelpdeskResolution -- --self-test

The demo inspects hd-901 and recommends kb-901 steps with an escalation condition. Use --input path.json for another dataset. --live selects the configured model provider; domain data still comes from the JSON adapter.
