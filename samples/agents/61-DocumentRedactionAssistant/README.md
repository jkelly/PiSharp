# Document redaction assistant

Native tools detect emails (including Unicode letters), US-style SSNs/phones and supplied case-insensitive literals, merge overlaps, and save a sanitized review. Offsets use .NET UTF-16 units. Overlapping and repeated literals are fully detected before spans are merged. Detection results omit raw sensitive values.

Every document must explicitly provide string-valued `text`. Missing, null, or nonstring text is rejected before saving a review or export. An explicitly supplied empty string is treated as a valid empty document and may produce an empty export with approval.

The usual replacement is `[REDACTED]`. If the substituted text contains a detected value, including within the label (for example, `Ted`) or across adjacent replacement boundaries, the tool retries with a deterministic single-character marker absent from the source. Final absent-value verification still applies to the complete result.

Run `dotnet run --project samples/agents/61-DocumentRedactionAssistant -- --demo` or `--self-test`. The demo produces `redaction-review-letter-1.json`; adding `--approve` also runs the fourth native tool call to export sanitized local text. `export_redacted` requires host approval and the current preview hash. It exports `redacted-ID.txt`, verifies all detected values are absent, accepts identical repeats and rejects conflicting existing files. All files remain local.

This is **plain-text-only** redaction. It does not process PDFs, Office files, image pixels, hidden text, document metadata or OCR, and it cannot detect every kind of personal data. Review the preview before exporting or distributing it. Original fixture text is never edited.
