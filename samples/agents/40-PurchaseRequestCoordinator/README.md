# Purchase Request Coordinator

The workflow inspects a purchase request, computes its total and budget route, and creates a workspace-only pending-approval submission. For `PR-100`, three devices at 25,000 cents total 75,000 cents. Engineering has 150,000 cents available before local commitments; the route requires its department manager and finance controller.

```powershell
dotnet run --project samples/agents/40-PurchaseRequestCoordinator -- --demo --approve
dotnet run --project samples/agents/40-PurchaseRequestCoordinator -- --self-test
```

`submit_purchase` requires host approval, a caller-supplied operation ID, and an exact integer `expectedTotalCents`. Its callback checks approval, recalculates current eligibility and rejects stale totals, missing routes, insufficient budgets, or a request already submitted under another key. The receipt at `purchases/<operationId>.json` binds the key to the complete source request and expected total. Identical replay returns the existing result, even in a fresh context using the same workspace; conflicting payloads fail. Existing local receipts count against the department budget.

Prices and budgets are fixture USD integer cents; quantities and prices must be positive. A process-level gate serializes mutations and `CreateNew` prevents overwriting an existing receipt. This is a teaching fixture, not a cross-process transactional budget ledger. Use one running process per workspace; a production adapter needs a transactional database with request and operation uniqueness plus atomic budget reservation.

`--approve` authorizes only local submission, never spending approval. The saved status remains `pending_human_approval`; no external system is contacted and no funds are spent. Live model mode uses the same local fixture tools. Real procurement integration needs authenticated budget/request sources, durable approval identities, and a separately authorized submission adapter.
