# CRM Update Assistant

Find a contact, validate a proposed change, and optionally save a copy in the sample workspace.

```powershell
dotnet run --project samples/agents/13-CrmUpdateAssistant
dotnet run --project samples/agents/13-CrmUpdateAssistant -- --self-test
dotnet run --project samples/agents/13-CrmUpdateAssistant -- --input path/to/input.json --workspace path/to/output
```

The default demo uses the local fixture and calls `find_contact` then `preview_update` through the shared agent runner. Its output is a JSON tool transcript in the selected workspace. Use `--live` only with a configured live provider. Any tool that writes files requires `--approve`; the demo does not perform writes.

Expected fixture behavior: `preview_update` derives a result from the fixture after `find_contact` examines source data. Replace the fixture using `--input` to test other records.

The fixture preview changes contact `C-17` from stage `lead` to `qualified`. A third tool, `save_update`, requires `--approve` and saves an updated JSON copy under the sample workspace. It does not connect to or modify a CRM service. Only `email`, `stage`, and `owner` can be changed; email and stage values are validated.
