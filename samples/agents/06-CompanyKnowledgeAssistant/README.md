# Company knowledge assistant

Searches only versioned fixture passages and returns source citations.

Run: dotnet run --project samples/agents/06-CompanyKnowledgeAssistant -- --workspace artifacts/company-knowledge-assistant

Test: dotnet run --project samples/agents/06-CompanyKnowledgeAssistant -- --self-test

The demo searches for hotel policy and retrieves its exact passage with doc-601, revision, and section citation. Use --input path.json for another dataset. --live selects the configured model provider; domain data still comes from the JSON adapter.
