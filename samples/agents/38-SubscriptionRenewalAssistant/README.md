# Subscription Renewal Assistant

Three tools inspect a selected contract, compute renewal economics, and save a host-reviewed action plan. The fixture clock is `2026-11-01`; no system clock affects results. `SUB-CRM` renews in 44 days with a November 15 notice deadline. Its 12 active seats out of 20 licensed seats produce a proposed annual cost of 158,400 cents and a 105,600-cent saving against the full renewal quote.

```powershell
dotnet run --project samples/agents/38-SubscriptionRenewalAssistant -- --demo --approve
dotnet run --project samples/agents/38-SubscriptionRenewalAssistant -- --self-test
```

The `--approve` flag permits saving `renewals/SUB-CRM.json` as host-reviewed. The callback independently checks approval. Without approval, inspection and comparison are available, but saving the reviewed plan is blocked. Repeated saves for the same source data produce identical content. Both contract and usage source IDs appear in the plan.

The comparison assumes per-seat annual USD pricing, uses integer cents, honors minimum seats, and distinguishes an open notice window from a missed deadline or a renewal outside the next 60 days. It rejects inconsistent seat counts and ambiguous IDs. Recommendations need contract-owner review; active-seat counts alone cannot establish whether a subscription should be canceled.

This fixture is local data, not a subscription API. Even live model mode only selects these same local tools. The sample never renews, cancels, sends mail, or changes vendor billing. A real integration would need authenticated contract/usage retrieval, currency and pricing-tier handling, source freshness checks, and a separate reviewed vendor-action adapter.
