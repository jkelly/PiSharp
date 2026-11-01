# Invoice Reconciliation

Compare an invoice with a purchase order and receiving record, then compute payment readiness.

```powershell
dotnet run --project samples/agents/17-InvoiceReconciliation
dotnet run --project samples/agents/17-InvoiceReconciliation -- --self-test
dotnet run --project samples/agents/17-InvoiceReconciliation -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `match_invoice` then `calculate_payable` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `calculate_payable` derives a result from the fixture after `match_invoice` examines source data. Replace the fixture using `--input` to test other records.

The fixture invoice totals **$270**, but only **$255** is payable because five `PAD` units have not been received. The remaining **$15** is held. The tools compare vendor, ordered quantity, unit price, and received quantity; they do not send payment.
