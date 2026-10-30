# Receipt to Expense

Parse OCR-transcribed receipt text, then classify and validate an expense.

```powershell
dotnet run --project samples/agents/16-ReceiptToExpense
dotnet run --project samples/agents/16-ReceiptToExpense -- --self-test
dotnet run --project samples/agents/16-ReceiptToExpense -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `parse_receipt_text` then `classify_expense` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `classify_expense` derives a result from the fixture after `parse_receipt_text` examines source data. Replace the fixture using `--input` to test other records.

The fixture is **OCR-transcribed text**, not image pixels. It yields a balanced **$17.82** Cedar Cafe receipt in the `Meals` category. The parser expects a merchant line, ISO date, item/tax lines ending in amounts, and a `Total` line. It reports malformed lines and unbalanced totals.

For an actual receipt image, use a vision-capable live model:

```powershell
dotnet run --project samples/agents/16-ReceiptToExpense -- --live --api openai-responses --model gpt-6.1-sol --image path/to/receipt.png
```

The shared host sends PNG, JPEG, or WebP image bytes to the configured live provider. In image mode the model reads the image and calls `capture_receipt` with merchant, date, `totalCents`, and line charges containing integer `amountCents`. Using integer cents avoids floating-point money values and fits the native Responses argument replay profile. The tool checks required fields and that charges sum to the total before `classify_expense` runs. Extraction quality depends on the selected provider/model; the fixture demo itself does not perform OCR. No expense is posted to an external system.
