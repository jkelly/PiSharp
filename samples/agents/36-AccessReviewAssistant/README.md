# Access Review Assistant

Review grants against a role's allowed resources, grant expiry and explicit approved exceptions using a fixed `asOf` date. For `emp-301` on 2026-11-01, repository access and an approved admin exception are retained; out-of-role payroll access and an expired documentation grant are recommended for removal.

```powershell
dotnet run --project samples/agents/36-AccessReviewAssistant -- --demo
dotnet run --project samples/agents/36-AccessReviewAssistant -- --demo --approve
dotnet run --project samples/agents/36-AccessReviewAssistant -- --self-test
```

Run from the repository root. `inspect_access` resolves the employee and unique policy, `review_access` computes evidence-bearing decisions, and `save_access_review` writes `access-review-emp-301.json`. A grant or exception is expired on its expiration date (`asOf >= expiry`). An exception requires an approver and never overrides an expired grant. Missing policies, duplicate grants or ambiguous exceptions fail honestly.

With `--approve`, `remove_local_access` writes the recommended grant IDs to `access-removals-emp-301.json`. This is **workspace-only state**, not remote identity administration. Both host and direct callbacks enforce approval. Repeated identical requests return the original receipt; changed data or review dates require a separate workspace/review rather than reusing the old approval. Later reviews incorporate local removals. Use a dedicated workspace for this sequential demonstration.

Use `--input FILE --workspace DIR` for another dataset or `--live --model MODEL` for native model orchestration. Supply `employeeId`, `asOf`, `roles`, and `employees` with explicit grants and exceptions. Role policies and grant decisions are deliberately transparent; no production IAM integration is implied.

Self-tests verify native three/four-stage execution, policy and expiration decisions, exception boundaries, missing/ambiguous data, second employee isolation, callback approval, precise removals, persisted state and replay conflict.
