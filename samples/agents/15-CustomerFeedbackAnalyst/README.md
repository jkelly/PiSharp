# Customer Feedback Analyst

Group feedback by issue theme and rank themes by frequency and severity.

```powershell
dotnet run --project samples/agents/15-CustomerFeedbackAnalyst
dotnet run --project samples/agents/15-CustomerFeedbackAnalyst -- --self-test
dotnet run --project samples/agents/15-CustomerFeedbackAnalyst -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `analyze_themes` then `rank_themes` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `rank_themes` derives a result from the fixture after `analyze_themes` examines source data. Replace the fixture using `--input` to test other records.

The fixture ranks **checkout** first from three matching comments and a combined severity score of eight. The theme match uses case-insensitive keyword lists from the input; it is a transparent rule-based analysis, not sentiment modeling.
