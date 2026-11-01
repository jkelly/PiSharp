# Project delivery coordinator

Run `dotnet run --project samples/agents/29-ProjectDeliveryCoordinator` or add `--self-test`.

The coordinator starts two `SampleHost.ExecuteAsync` tasks before awaiting either, so separate PiSharp `Agent` instances inspect delivery status and risk concurrently. Their transcripts and reports remain in `status/` and `risk/`; the parent writes `delivery.json`. The fixture contains declared tasks and risks, not a live project tracker.
