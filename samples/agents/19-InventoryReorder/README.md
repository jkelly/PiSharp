# Inventory Reorder

Calculate stock gaps from demand and lead time, then prepare a budgeted reorder.

```powershell
dotnet run --project samples/agents/19-InventoryReorder
dotnet run --project samples/agents/19-InventoryReorder -- --self-test
dotnet run --project samples/agents/19-InventoryReorder -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `calculate_stock_gaps` then `build_reorder_plan` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `build_reorder_plan` derives a result from the fixture after `calculate_stock_gaps` examines source data. Replace the fixture using `--input` to test other records.

The fixture proposes **11 INK and 17 PAPER** units, spending **$217** of a $500 budget. Reorder point is daily demand × lead days + safety stock; stock gap accounts for on-hand and on-order units. The plan can partially fill gaps when budget is short, and reports any unfilled SKUs. It does not place a purchase order.
