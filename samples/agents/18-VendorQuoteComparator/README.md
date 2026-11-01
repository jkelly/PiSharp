# Vendor Quote Comparator

Normalize vendor prices including shipping and rank quotes with lead time.

```powershell
dotnet run --project samples/agents/18-VendorQuoteComparator
dotnet run --project samples/agents/18-VendorQuoteComparator -- --self-test
dotnet run --project samples/agents/18-VendorQuoteComparator -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `normalize_quotes` then `rank_quotes` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `rank_quotes` derives a result from the fixture after `normalize_quotes` examines source data. Replace the fixture using `--input` to test other records.

The fixture recommends **Deskly at $1,720 total** for 20 chairs. `Steelworks` is excluded because its 18-day lead exceeds the 14-day limit. Totals include shipping; ranking sorts eligible quotes by total cost, then lead time. No order is placed.
