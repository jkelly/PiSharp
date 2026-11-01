# IT troubleshooting

Checks local device observations and diagnostic rules; no device commands run.

Run: dotnet run --project samples/agents/10-ItTroubleshooting -- --workspace artifacts/it-troubleshooting

Test: dotnet run --project samples/agents/10-ItTroubleshooting -- --self-test

The demo inspects dev-1001 and yields DNS and VPN checks. Use --input path.json for another dataset. --live selects the configured model provider; domain data still comes from the JSON adapter.
