# Insurance claim organizer

Three native tools inventory supplied photo/receipt/correspondence metadata, reconcile receipts by normalized issuer and receipt number, and save `claim-claim-1.json`. Duplicate copies retain all source IDs but contribute once to integer-cent totals. Conflicting duplicates, incomplete receipts and currency mismatches are excluded and reported. The fixture balances at 37,500 cents while honestly requesting a missing policy document.

Run `dotnet run --project samples/agents/62-InsuranceClaimOrganizer -- --demo` or `--self-test`. Evidence files are represented by supplied metadata; the sample does not open PDFs, inspect photo pixels or verify authenticity. No claim is filed and no coverage or legal determination is made. The saved manifest is for human review only.
