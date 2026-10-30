# Order Status Concierge

Look up an order and calculate a delivery estimate from its scan history.

```powershell
dotnet run --project samples/agents/11-OrderStatusConcierge
dotnet run --project samples/agents/11-OrderStatusConcierge -- --self-test
dotnet run --project samples/agents/11-OrderStatusConcierge -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `lookup_order` then `estimate_delivery` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `estimate_delivery` derives a result from the fixture after `lookup_order` examines source data. Replace the fixture using `--input` to test other records.

The fixture returns order `ORD-1042` as in transit, with a last scan on 2026-10-29 and an estimated delivery of **2026-10-31**. An unshipped order returns an unavailable estimate. The estimate is shipped date plus configured transit days; it is not a carrier promise.
