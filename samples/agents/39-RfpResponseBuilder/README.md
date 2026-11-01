# RFP Response Builder

The tools inspect one RFP, match its requirements against approved current evidence, and write `rfp/<rfpId>.json`. Every supported response includes source IDs. The fixture supports SAML SSO and CSV export; its expired EU-residency evidence leaves a mandatory gap and its stronger draft claim is explicitly rejected.

```powershell
dotnet run --project samples/agents/39-RfpResponseBuilder -- --demo
dotnet run --project samples/agents/39-RfpResponseBuilder -- --self-test
```

Matching uses exact capability keys and the fixture `asOf` timestamp. Evidence must be approved and unexpired. Draft claims must exactly match a current approved source statement for the same requirement; paraphrases are conservatively flagged for review. This demonstrates auditable matching, not natural-language entailment. Duplicate source or requirement IDs fail instead of selecting an arbitrary record. Every tool recomputes from the selected RFP, so calls for different IDs cannot reuse old matches.

Saving is a local report operation and needs no mutation approval. The draft distinguishes readiness for human review from readiness to submit, lists mandatory gaps and unsupported claims, and never submits to a procurement portal. Repeated runs with unchanged inputs produce identical draft content.

The supplied evidence is fixture data. A real integration would need authenticated retrieval, document versioning and review ownership, extraction with stable citations, and a separate authorized submission workflow. Live model mode uses the same local evidence and cannot attest to actual compliance.
