# Document change explainer

Compares local document revisions by section; it does not edit documents.

Run: dotnet run --project samples/agents/07-DocumentChangeExplainer -- --workspace artifacts/document-change-explainer

Test: dotnet run --project samples/agents/07-DocumentChangeExplainer -- --self-test

The demo reports changed eligibility and deadline sections plus the new receipts section. Use --input path.json for another dataset. --live selects the configured model provider; domain data still comes from the JSON adapter.
