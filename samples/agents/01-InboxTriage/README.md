# Inbox triage

Reads local message records and scores an individual message from its sender, request, and due date. It does not move or send mail.

```powershell
dotnet run --project samples/agents/01-InboxTriage -- --workspace artifacts/inbox-demo
dotnet run --project samples/agents/01-InboxTriage -- --self-test
```

The fixture demo lists two unread messages and marks `m-101` high priority with reasons and a `message:m-101` source. `--input path.json` replaces the fixture. `--live` uses the configured model provider, but message access remains the local JSON adapter; no mailbox integration is configured.
