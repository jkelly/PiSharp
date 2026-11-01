# Desktop workflow assistant

Run `dotnet run --project samples/agents/30-DesktopWorkflowAssistant` for the fixture, or add `--self-test`.

For a desktop client, use `--stdio`. Send one JSON object per line: `{"action":"ping"}` or `{"action":"run","input":{"requestId":"r1","intent":"prepare-meeting","title":"Review","participants":["Ada"],"notes":["Decide"]}}`. Each line receives one JSON response with a workspace path and the actual Agent report. The adapter writes a local meeting brief and receipt. It does not invoke PiSharp.Rpc, control desktop windows, open a calendar, or send invites.
