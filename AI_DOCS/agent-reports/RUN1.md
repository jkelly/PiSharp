# RUN1: strict run options on Codex inline tools

Status: source frozen for main's .NET validation. No .NET build/test, Git operation, remote write, or changes outside the assigned WebAgent files were performed by RUN1. Requested model/tier: gpt-6.1-sol high, Ultrafast; effective tier unverified.

## Diagnosis and authority

The actual Linux `3024b1e0` failure is `runs.engine.codex-takes-its-service-tier-and-the-patched-options` in the 539-case WebAgent report. `RunCaptureHandler.PatchAsync` called `RunModelOptions.ApplyToBody`, which searched only root `tools`. The selected Codex model now declares its tools as direct `input` entries with `type: additional_tools`, `role: developer`, and flat function definitions in `tools`; even the initial batch lives there. The required `get_weather` was therefore incorrectly reported absent and the request was stopped before HTTP.

Pinned source authority is `compatibility/target.lock.json`: Pi v1.5.0, commit `f9a26365568127461c47a07a37dcb42c140a3ac5`. Read from the existing `E:/AI/PiSharp/upstream-v150` checkout:

- `packages/ai/src/api/openai-codex-responses.ts:549-592`: `getResponsesToolSupport`, shared message conversion, and optional root tools.
- `packages/ai/src/api/openai-responses-shared.ts:156-174`: inline batches accumulate; later batches replace same-name inline definitions; inline declarations cannot replace top-level tools.
- `packages/ai/src/api/openai-responses-shared.ts:237-245`: direct developer `additional_tools` items.

Existing C# authority: `ResponsesTranscriptTools.FoldedSystems` returns zero folded systems for additional-tools support; `ResponsesTranscriptProjector` emits the corresponding inline items; `OpenAICodexResponsesTransport.BuildBody` uses those shared projections. The shared converter also serves OpenAI Responses and Azure Responses, so inline handling explicitly admits only `openai-responses`, `azure-openai-responses`, and `openai-codex-responses`.

## Changes

- `src/PiSharp.WebAgent/Runs/RunModelOptions.cs`: retain root flat Responses-family and nested Chat Completions function support. Recognize direct developer inline declarations on the three explicit family APIs. Mark every required initial and incremental definition, including legitimate later replacements. Reject absent tools, wrong function shapes/types, duplicates within a batch, and root/inline collisions through the existing missing-tool refusal result. Do not traverse nested user/tool content or another API's inline arrays.
- `src/PiSharp.WebAgent/Routing/RunCaptureHandler.cs`: parse/edit/write the request using existing `JsonUtf16.MutableNode`/`ToJsonString`, preserving escaped lone surrogate values and property names during a run patch. This is limited to the existing patch serialization seam. Compression, header copying, cache edits, retries, timeout, and task ownership logic remain in their existing path.
- `tests/PiSharp.WebAgent.Tests/RunEngineCases.cs`: update only the proven wire assertion for the failing actual-host run: this selected model has no duplicate root declarations and carries strict `get_weather` in the direct developer inline batch. Keep all service-tier, verbosity, parallel, session, zstd, history, and unsupported-tier assertions.
- `tests/PiSharp.WebAgent.Tests/RunStrictOptionCases.cs`: four added authored cases cover root APIs; initial/incremental/replacement declarations and API isolation; missing/duplicate/malformed required declarations with zero HTTP and zero retries; plain/zstd patches preserving root options, cache/session, request/content headers, and UTF-16 names/values.

## Verification and exact selections

Initial static Node check parsed all 17 JSON literals in the new test file and verified four unique new case IDs. The followup below brings the literal count to 18. No .NET results are claimed. Main owns all .NET execution.

Focused runner selections (the existing runner accepts `--only <prefix>`):

- `runs.engine.codex-takes-its-service-tier-and-the-patched-options`
- `runs.options.strict-` (two added cases)
- `runs.capture.strict-` (two added cases, including twelve refusal bodies and plain/zstd preservation)
- Existing carry/option coverage, if needed: `runs.options.` and `runs.carry.`

Then run the complete WebAgent report; expected case count is 543 (539 existing plus four new). Required release evidence remains exact-candidate-SHA hosted validation; this source checkpoint alone does not establish release readiness.

## Followup: preserve invalid UTF-8 refusal

Main identified a concrete regression at the new decode seam: `Encoding.UTF8.GetString(bytes)` permits replacement fallback before the UTF-16-preserving JSON parser sees the text. The [official .NET 10 Encoding.UTF8 contract](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoding.utf8?view=net-10.0#remarks) confirms replacement fallback. The [official .NET 10 runtime JSON helper](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Reader/JsonReaderHelper.Unescaping.cs#L34-L35) explicitly configures `UTF8Encoding(false, true)` to reject invalid byte data. This confirms that replacement decoding is inappropriate for the existing byte JSON edit boundary; no .NET behavior was inferred from a local test run.

`RunCaptureHandler` now uses one static `UTF8Encoding(false, true)` decoder before `JsonUtf16.MutableNode`. Escaped lone surrogates remain valid ASCII JSON escapes and continue through the existing preservation helper. A decoding failure is caught in the existing patch-parse catch and becomes `RunPatchException` before the provider/retry path.

The existing `runs.capture.strict-missing-ambiguous-and-malformed-declarations-never-reach-http` case now also puts literal byte `0xFF` inside an otherwise valid JSON string, with a valid required tool. Plain and zstd variants each assert `RunPatchException` and zero provider requests, successful patches, and retries despite `MaxRetries = 2`. No new case IDs were added; full count remains 543. A static Node control verifies the marker position, fatal UTF-8 rejection, and that replacement decoding would instead create parseable JSON; all 18 test JSON literals parse.

This followup changes source after main's already-built full WebAgent run began. That run covers its earlier built bytes; rebuild and rerun `--only runs.capture.strict-` for this followup before claiming the new boundary is validated. RUN1 performed no .NET execution or Git/remote operations.

## Fixture followup: isolate Windows Event Log writes

Main reported a newly built `runs.` selection with 32 cases, 31 passing, including all new strict cases and the actual Codex engine regression. The remaining `runs.app.streams-over-sse-records-chats-and-a-restart-marks-a-run-interrupted` failure occurred during child-app startup: a SpendLedger warning attempted to open Windows Event Log's `.NET Runtime` source and received AccessDenied in the sandbox. This is a test-host operating-system permission dependency; it is not evidence of a run-option runtime failure.

The child-process fixture in `tests/PiSharp.WebAgent.Tests/WebAgentSupport.cs` now adds `--Logging:EventLog:LogLevel:Default=None` alongside its existing `--Logging:LogLevel:Default=Warning`. Console warnings remain enabled, and production logging source/configuration is unchanged. Both `StartAsync` and `RunToExitAsync` use this same `StartInfo` seam, so the narrow isolation also applies to idle-exit fixtures. The executable remains the existing `dotnet` selection; no SDK resolution change is included.

Source is re-frozen for main's rebuild and narrow rerun of the failing app case, plus idle-exit coverage as necessary. RUN1 ran no .NET process; the 32-case result above is main-reported evidence, not RUN1 execution.
