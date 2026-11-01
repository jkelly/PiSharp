# Form intake assistant

Validates and routes a local form record; no external ticket is created.

Run: dotnet run --project samples/agents/08-FormIntakeAssistant -- --workspace artifacts/form-intake-assistant

Test: dotnet run --project samples/agents/08-FormIntakeAssistant -- --self-test

The demo validates form-801 and prepares an identity-team routing packet. Use --input path.json for another dataset. --live selects the configured model provider; domain data still comes from the JSON adapter.
