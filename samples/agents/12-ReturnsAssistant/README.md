# Returns Assistant

Check a return against policy and calculate the refund amount.

```powershell
dotnet run --project samples/agents/12-ReturnsAssistant
dotnet run --project samples/agents/12-ReturnsAssistant -- --self-test
dotnet run --project samples/agents/12-ReturnsAssistant -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `check_eligibility` then `calculate_refund` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `calculate_refund` derives a result from the fixture after `check_eligibility` examines source data. Replace the fixture using `--input` to test other records.

The fixture order `ORD-220` is eligible. Its proposed refund is **$127.39** after a $13.00 restocking fee; the $8.00 original shipping charge is not refunded. `ORD-221` is excluded as final sale. This sample calculates a quote and does not issue a refund.
