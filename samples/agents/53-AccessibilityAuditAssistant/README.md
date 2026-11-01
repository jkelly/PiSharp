# Accessibility Audit Assistant

Audit two supplied documents, propose exact attribute fixes, apply approved local changes, and rescan. The fixture repairs four findings across `checkout` and `contact`: missing document language, an image alternative, an email control name, and a button name.

```powershell
dotnet run --project samples/agents/53-AccessibilityAuditAssistant -- --demo --approve
dotnet run --project samples/agents/53-AccessibilityAuditAssistant -- --self-test
dotnet run --project samples/agents/53-AccessibilityAuditAssistant -- --input path/to/fixture.json --workspace path/to/output --approve
```

The native agent calls `inspect_accessibility`, `propose_accessibility_fixes`, then `apply_accessibility_fixes`, passing explicit `documentIds`. Both the host and callback require approval before edits. A source fingerprint rejects changed documents or fix instructions between proposal and application. Only configured values may be applied to matching findings on unique element IDs; unfixable findings remain visible.

Input `documents` contain `id` and well-formed XHTML `html`. `fixes` contain `documentId`, `elementId`, `rule`, and `value`. Supported rules are `document-language`, `image-alt`, and `control-name`. The checker understands explicit labels, enclosing labels, direct ARIA labels and referenced ARIA label text. A present empty `alt` is accepted as an author's decorative-image decision. Missing element IDs can be reported but cannot be targeted safely.

Outputs are `accessibility-plan.json`, `accessibility-documents.json`, and `accessibility-rescan.json` in the workspace, plus the shared runner's report/transcript. The fixture starts with four findings and ends with zero findings from this checker. Persisted documents survive restart; applying the same plan twice is idempotent.

This is a limited XML-based XHTML checker, **not an accessibility certification** or browser accessibility-tree audit. Malformed/non-XHTML input is reported as unauditable. It does not check contrast, keyboard behavior, focus, CSS visibility, dynamic DOM, full accessible-name computation, or every WCAG criterion. No remote page is fetched or edited. Replace the fixture with `--input` to explore missing labels, malformed documents, ambiguous IDs, and unresolved fixes. Seven offline regression cases cover these limits and mutation guards.
