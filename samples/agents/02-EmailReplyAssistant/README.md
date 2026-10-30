# Email reply assistant

Threads are local records; the draft is returned, never sent.

Run: dotnet run --project samples/agents/02-EmailReplyAssistant -- --workspace artifacts/email-reply

Test: dotnet run --project samples/agents/02-EmailReplyAssistant -- --self-test

The demo finds thread t-201 and drafts a factual reply that says training is included. Use --input path.json for another dataset. --live selects the configured model provider; domain data still comes from the JSON adapter.
