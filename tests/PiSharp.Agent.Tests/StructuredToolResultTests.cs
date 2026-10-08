using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class StructuredToolResultTests
{
    private static readonly ModelDescriptor Model = new("structured-model", "openai-responses", "fixture-provider");

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("structured result preserves legacy constructors and deconstruction", LegacySurface),
        ("structured result owns raw tokens/order and distinguishes absent from null", OwnedRawPresence),
        ("structured result survives normalization and explicit result transforms", ResultTransforms),
        ("structured completed output survives cancellation and finalization errors", CompletedFailures),
        ("structured output has separate exact bounds and escaped one MiB capacity", SeparateBudgets),
        ("structured admission enforces depth finite numbers Unicode and owned strict JSON", StrictAdmission),
        ("output details preserve inert NUL raw ownership and exact strict JSON budgets", OutputDetailsAdmission),
        ("structured executor rejects retained permissive raw syntax without exposing metadata", ExecutorRawSyntax),
        ("structured transforms reject retained permissive raw syntax and preserve last validated output", TransformRawSyntax),
        ("structured result traverses genuine scheduler hooks and completion ordering", SchedulerFlow),
        ("structured output remains in Agent outcomes and outside model message history", TranscriptExclusion),
        ("ordinary and truncation details NUL output survive awaited progress transforms Agent JSON and next request", NulTextOutputFlow)
    ];

    private static Task LegacySurface()
    {
        var content = ImmutableArray.Create(new TextContent("legacy"));
        var failure = new ToolFailure(ToolFailureKind.ExecutionError, "failed");
        var original = new ToolResult(content, JsonData.EmptyObject, true, true, failure);
        var (actualContent, details, isError, terminate, actualFailure) = original;
        Equal(content, actualContent); Check(ReferenceEquals(JsonData.EmptyObject, details), "Legacy details changed.");
        Check(isError && terminate && ReferenceEquals(failure, actualFailure), "Legacy positional result fields changed.");
        Check(original.StructuredContent is null && ToolResult.Success("ok").StructuredContent is null &&
            ToolResult.Error(ToolFailureKind.ExecutionError, "failed").StructuredContent is null, "Legacy results gained metadata.");
        var extended = original with { StructuredContent = JsonData.Null };
        var (_, _, extendedError, extendedTerminate, _) = extended;
        Check(extendedError && extendedTerminate && original.StructuredContent is null, "Nonpositional extension changed legacy state.");

        var options = new ToolInvokerOptions(128, 16, 1024, 65_536, 65_536, 65_536, 32, 128, 128);
        var (tools, transforms, assistantBlocks, arguments, actions, results, depth, entries, resultBlocks) = options;
        Check(tools == 128 && transforms == 16 && assistantBlocks == 1024 && arguments == 65_536 && actions == 65_536 &&
            results == 65_536 && depth == 32 && entries == 128 && resultBlocks == 128, "Legacy options deconstruction changed.");
        Equal(6 * 1024 * 1024 + 65_536, options.MaximumStructuredContentCharacters);
        return Task.CompletedTask;
    }

    private static async Task OwnedRawPresence()
    {
        const string raw = """{ "z":1.0,"precise":9007199254740993,"exponent":1e2,"negativeZero":-0,"nil":null,"opaque":[false,"\u6587\uD83D\uDE42"] }""";
        JsonData owned;
        using (var document = JsonDocument.Parse(raw)) owned = JsonData.FromElement(document.RootElement);
        var finalized = await Invoke(ToolResult.Success("model text") with { StructuredContent = owned });
        Check(!finalized.IsError && ReferenceEquals(owned, finalized.StructuredContent), "Owned output was lost or reconstructed.");
        Equal(raw, finalized.StructuredContent!.ToString());
        Equal("9007199254740993", finalized.StructuredContent.Value.GetProperty("precise").GetRawText());
        Equal("1.0", finalized.StructuredContent.Value.GetProperty("z").GetRawText());
        Equal("1e2", finalized.StructuredContent.Value.GetProperty("exponent").GetRawText());
        Equal("-0", finalized.StructuredContent.Value.GetProperty("negativeZero").GetRawText());
        Sequence(["z", "precise", "exponent", "negativeZero", "nil", "opaque"],
            finalized.StructuredContent.Value.EnumerateObject().Select(value => value.Name));
        foreach (var metadata in new JsonData?[] { null, JsonData.Null, JsonData.Parse("[]"), JsonData.Parse("false"), JsonData.Parse("\"text\"") })
        {
            var result = await Invoke(ToolResult.Success("ok") with { StructuredContent = metadata });
            Check(!result.IsError && ReferenceEquals(metadata, result.StructuredContent), "Absent/null/scalar distinction changed.");
        }
    }

    private static async Task ResultTransforms()
    {
        var first = JsonData.Parse("""{"phase":"executed","scale":1.0}""");
        var final = JsonData.Parse("""{"phase":"final","precise":9007199254740993}""");
        var executed = ToolResult.Success("effect") with
        {
            StructuredContent = first, Failure = new(ToolFailureKind.ExecutionError, "exit status")
        };
        var stages = new List<string>();
        var result = await Invoke(executed, transforms:
        [
            (_, _, value, _) =>
            {
                stages.Add("first"); Check(value.IsError, "Failure normalization did not set the error flag.");
                Check(ReferenceEquals(first, value.StructuredContent), "Failure normalization dropped structured output.");
                return ValueTask.FromResult(value with { StructuredContent = final, Failure = null, IsError = false });
            },
            (_, _, value, _) =>
            {
                stages.Add("second"); Check(ReferenceEquals(final, value.StructuredContent), "Metadata override did not reach the next transform.");
                return ValueTask.FromResult(value with { Content = [new("final content")], Details = JsonData.Parse("{\"final\":true}"), Terminate = true });
            }
        ]);
        Sequence(["first", "second"], stages);
        Check(!result.IsError && result.Terminate && result.Failure is null && ReferenceEquals(final, result.StructuredContent), "Final result fields changed.");
        Equal("final content", result.Content.Single().Text);
        foreach (var replacement in new JsonData?[] { null, JsonData.Null })
        {
            var replaced = await Invoke(executed, transforms:
                [(_, _, value, _) => ValueTask.FromResult(value with { StructuredContent = replacement })]);
            Check(ReferenceEquals(replacement, replaced.StructuredContent), "Explicit metadata replacement/clearing was ignored.");
        }
        var nonzero = await Invoke(ToolResult.Success("nonzero") with { IsError = true, StructuredContent = first });
        Check(nonzero.IsError && ReferenceEquals(first, nonzero.StructuredContent), "Error result lost programmatic output.");
        var recovered = await new ToolInvoker([new Adapter("lookup", (_, _) => throw new InvalidOperationException("hidden"))], new Policy(),
            resultTransforms: [(_, _, value, _) =>
            {
                Failure(value, ToolFailureKind.ExecutionError);
                return ValueTask.FromResult(ToolResult.Success("recovered") with { StructuredContent = final });
            }]).ExecuteAsync(Invocation(), default);
        Check(!recovered.IsError && ReferenceEquals(final, recovered.StructuredContent), "Execution-error override lost metadata.");
    }

    private static async Task CompletedFailures()
    {
        var first = JsonData.Parse("""{"phase":"executed"}""");
        var latest = JsonData.Parse("""{"phase":"validated-transform"}""");
        var executed = ToolResult.Success("completed") with { StructuredContent = first, Terminate = true };
        using var canceled = new CancellationTokenSource();
        var adapter = new Adapter("lookup", (_, _) => { canceled.Cancel(); return ValueTask.FromResult(executed); });
        var cancellation = await new ToolInvoker([adapter], new Policy()).ExecuteAsync(Invocation(), canceled.Token);
        Failure(cancellation, ToolFailureKind.Canceled);
        Check(cancellation.Terminate && ReferenceEquals(first, cancellation.StructuredContent), "Post-execution cancellation concealed metadata.");
        Equal("completed", cancellation.Content[0].Text);

        foreach (var failingTransform in new ToolResultTransform[]
        {
            (_, _, _, _) => throw new InvalidOperationException("private finalization payload"),
            (_, _, _, _) => ValueTask.FromResult<ToolResult>(null!),
            (_, _, value, _) => ValueTask.FromResult(value with { StructuredContent = JsonData.Parse("1e309") })
        })
        {
            var failed = await Invoke(executed, transforms:
            [(_, _, value, _) => ValueTask.FromResult(value with { StructuredContent = latest }), failingTransform]);
            Failure(failed, ToolFailureKind.HookError);
            Check(ReferenceEquals(latest, failed.StructuredContent) && failed.Terminate, "Finalization concealed the last validated metadata.");
            Equal("completed", failed.Content[0].Text);
            Check(failed.Content.All(value => !value.Text.Contains("private", StringComparison.Ordinal)), "Finalization exception leaked.");
        }
        using var transformCanceled = new CancellationTokenSource();
        var transformed = await Invoke(executed, transforms:
        [(_, _, value, _) => { transformCanceled.Cancel(); return ValueTask.FromResult(value with { StructuredContent = latest }); }], token: transformCanceled.Token);
        Failure(transformed, ToolFailureKind.Canceled);
        Check(ReferenceEquals(latest, transformed.StructuredContent), "Cancellation dropped a successfully validated transform.");
    }

    private static async Task SeparateBudgets()
    {
        var metadata = JsonData.Parse("""{"output":"\u0000"}""");
        var size = metadata.ToString().Length;
        var executed = ToolResult.Success("ok") with { StructuredContent = metadata };
        Check(!(await Invoke(executed, new() { MaximumStructuredContentCharacters = size })).IsError, "Exact metadata boundary was rejected.");
        Failure(await Invoke(executed, new() { MaximumStructuredContentCharacters = size - 1 }), ToolFailureKind.ExecutionError);
        foreach (var cap in new[] { 0, -1 })
            Throws<ArgumentOutOfRangeException>(() => new ToolInvoker([new Adapter("lookup")], new Policy(),
                options: new() { MaximumStructuredContentCharacters = cap }));

        // Six raw characters per NUL represent the worst JSON expansion of one output byte.
        var escapedMiB = JsonData.Parse(JsonSerializer.Serialize(new
        {
            output = new string('\0', 1024 * 1024), truncated = true,
            full_output_path = "/synthetic/full-output", exit_code = 1, wall_time_seconds = 1.0
        }));
        Check(escapedMiB.ToString().Length > 6 * 1024 * 1024, "Escaping control did not exercise the required capacity.");
        var large = await Invoke(ToolResult.Success("model excerpt") with { StructuredContent = escapedMiB });
        Check(!large.IsError && ReferenceEquals(escapedMiB, large.StructuredContent), "One MiB escaped programmatic output exceeded defaults.");
        var ordinaryBoundary = ToolResult.Success(new string('x', 65_536 - 2)) with { StructuredContent = escapedMiB };
        Check(!(await Invoke(ordinaryBoundary)).IsError, "Structured output consumed the ordinary text/details allowance.");
        Failure(await Invoke(ordinaryBoundary with { Content = [new(new string('x', 65_536 - 1))] }), ToolFailureKind.ExecutionError);
        Failure(await Invoke(ToolResult.Success("ok") with { Details = JsonData.Parse("\"" + new string('x', 65_536) + "\""), StructuredContent = metadata }),
            ToolFailureKind.ExecutionError);
    }

    private static async Task StrictAdmission()
    {
        foreach (var raw in new[] { "{}", "[]", "{\"number\":1.7976931348623157e308}", "-1.7976931348623157e308",
            "\"\\uD83D\\uDE42\\u0000\"", "{\"\\u0000\":\"\\u6587\"}", "null" })
        {
            var value = JsonData.Parse(raw);
            var result = await Invoke(ToolResult.Success("ok") with { StructuredContent = value }, new(MaximumJsonDepth: 1));
            Check(!result.IsError && ReferenceEquals(value, result.StructuredContent), "Supported strict JSON metadata was rejected.");
            Equal(raw, result.StructuredContent!.ToString());
        }
        foreach (var raw in new[] { "1e309", "-1e309", "{\"number\":1e9999}", "\"\\uD800\"", "\"\\uDC00\"",
            "{\"\\uD800\":0}", "{\"nested\":{}}", "[[]]" })
        {
            JsonData value;
            try { value = JsonData.Parse(raw); }
            catch (JsonException) { continue; }
            catch (InvalidOperationException) { continue; } // Invalid property-name Unicode may fail while taking ownership.
            Failure(await Invoke(ToolResult.Success("ok") with { StructuredContent = value }, new(MaximumJsonDepth: 1)), ToolFailureKind.ExecutionError);
        }
        var exactDepth = JsonData.Parse(new string('[', 32) + "0" + new string(']', 32));
        Check(!(await Invoke(ToolResult.Success("ok") with { StructuredContent = exactDepth })).IsError, "Exact structured container depth was rejected.");
        Failure(await Invoke(ToolResult.Success("ok") with { StructuredContent = JsonData.Parse("[" + exactDepth + "]") }), ToolFailureKind.ExecutionError);
        foreach (var raw in new[] { "NaN", "Infinity", "-Infinity", "{\"a\":1,\"a\":2}", "{\"a\":1,\"\\u0061\":2}", "{} trailing", "{\"a\":1,}", "/*comment*/{}" })
            Throws<JsonException>(() => JsonData.Parse(raw));
        using var duplicate = JsonDocument.Parse("{\"a\":1,\"a\":2}");
        Throws<JsonException>(() => JsonData.FromElement(duplicate.RootElement));
        Throws<JsonException>(() => JsonData.FromElement(default));

        // TextDecoder output is data. NUL survives ordinary and structured output without relaxing action strings.
        var nul = await Invoke(ToolResult.Success("text\0output") with { StructuredContent = JsonData.Parse("\"\\u0000\"") });
        Check(!nul.IsError, "Decoded NUL text was rejected.");
        Equal("text\0output", nul.Content.Single().Text); Equal("\0", nul.StructuredContent!.Value.GetString());
        foreach (var invalid in new ToolResult[]
        {
            ToolResult.Success("ok") with { Details = JsonData.Parse("\"\\uD800\"") },
            ToolResult.Success("ok") with { Failure = new(ToolFailureKind.ExecutionError, "bad\0failure") },
            ToolResult.Success("ok") with { Content = [new("ok", JsonFields.Empty.Set("bad\0key", JsonData.Null))] },
            ToolResult.Success("ok") with { Content = [new("ok", JsonFields.Empty.Set("extra", JsonData.Parse("\"\\u0000\"")))] },
            ToolResult.Success("\ud800"), ToolResult.Success("\udc00")
        })
            Failure(await Invoke(invalid), ToolFailureKind.ExecutionError);
        foreach (var invalid in new Func<PreparedToolAction, PreparedToolAction>[]
        {
            value => value with { Target = "/synthetic/\0file" },
            value => value with { WorkingDirectory = "/synthetic/\0cwd" },
            value => value with { Kind = PreparedToolActionKind.Command, WorkingDirectory = "/synthetic", CommandArguments = ["a\0b"] },
            value => value with { Environment = ImmutableDictionary<string, string>.Empty.Add("OUTPUT", "a\0b") },
            value => value with { Environment = ImmutableDictionary<string, string>.Empty.Add("bad\0key", "value") },
        })
        {
            var adapter = new Adapter("lookup"); var policy = new Policy();
            var rejected = await new ToolInvoker([adapter], policy, [(_, value, _) => ValueTask.FromResult(invalid(value))]).ExecuteAsync(Invocation(), default);
            Failure(rejected, ToolFailureKind.InvalidArguments); Equal(0, adapter.Executions); Equal(0, policy.Authorizations);
        }
    }

    private static async Task OutputDetailsAdmission()
    {
        const string raw = """{ "truncation":{"content":"head\u0000tail\u6587\uD83D\uDE42","truncated":true,"truncatedBy":"bytes"},"scale":1.0,"precise":9007199254740993,"negativeZero":-0,"nil":null,"\u0000opaque":{"\u0000":"\u0000"} }""";
        var details = PermissiveOwned(raw);
        var output = ToolResult.Success("text\0") with { Details = details, StructuredContent = JsonData.Parse("\"programmatic\\u0000\"") };
        var characters = raw.Length + output.Content.Single().Text.Length;
        var accepted = await Invoke(output, new(MaximumResultCharacters: characters));
        Check(!accepted.IsError && ReferenceEquals(details, accepted.Details), "Exact output details admission changed owned data.");
        Equal(raw, accepted.Details.ToString());
        Sequence(["truncation", "scale", "precise", "negativeZero", "nil", "\0opaque"], accepted.Details.Value.EnumerateObject().Select(value => value.Name));
        Equal("head\0tail\u6587\U0001f642", accepted.Details.Value.GetProperty("truncation").GetProperty("content").GetString());
        Equal("1.0", accepted.Details.Value.GetProperty("scale").GetRawText()); Equal("9007199254740993", accepted.Details.Value.GetProperty("precise").GetRawText());
        Equal("-0", accepted.Details.Value.GetProperty("negativeZero").GetRawText()); Equal(JsonValueKind.Null, accepted.Details.Value.GetProperty("nil").ValueKind);
        Equal("\0", accepted.Details.Value.GetProperty("\0opaque").GetProperty("\0").GetString());
        Failure(await Invoke(output, new(MaximumResultCharacters: characters - 1)), ToolFailureKind.ExecutionError);
        Equal(raw, details.ToString());

        foreach (var validRaw in new[] { "null", "\"\\u0000\"", "[\"\\u0000\",null]", "{\"\\u0000\":\"\\u0000\"}", "1.7976931348623157e308" })
        {
            var value = JsonData.Parse(validRaw);
            var result = await Invoke(new([], value), new(MaximumResultCharacters: validRaw.Length, MaximumJsonDepth: 1));
            Check(!result.IsError && ReferenceEquals(value, result.Details), "Valid inert scalar/container details were rejected or rewritten.");
            Equal(validRaw, result.Details.ToString());
            Failure(await Invoke(new([], value), new(MaximumResultCharacters: validRaw.Length - 1, MaximumJsonDepth: 1)), ToolFailureKind.ExecutionError);
        }
        var exactDepth = JsonData.Parse(new string('[', 32) + "0" + new string(']', 32));
        Check(!(await Invoke(new([], exactDepth))).IsError, "Exact output details depth was rejected.");
        Failure(await Invoke(new([], JsonData.Parse("[" + exactDepth + "]"))), ToolFailureKind.ExecutionError);
        foreach (var invalidRaw in new[] { "1e309", "-1e9999", "{\"number\":1e9999}", "\"\\uD800\"", "\"\\uDC00\"", "{\"\\uD800\":0}" })
        {
            JsonData value;
            try { value = JsonData.Parse(invalidRaw); }
            catch (JsonException) { continue; }
            catch (InvalidOperationException) { continue; }
            var result = await Invoke(new([], value)); Failure(result, ToolFailureKind.ExecutionError);
            Equal("{}", result.Details.ToString()); Equal(invalidRaw, value.ToString());
        }
        foreach (var invalidRaw in new[] { "NaN", "Infinity", "-Infinity", "{\"a\":1,\"a\":2}", "{\"a\":1,\"\\u0061\":2}", "{} trailing" })
            Throws<JsonException>(() => JsonData.Parse(invalidRaw));
        using (var duplicate = JsonDocument.Parse("{\"a\":1,\"a\":2}")) Throws<JsonException>(() => JsonData.FromElement(duplicate.RootElement));
        foreach (var invalidRaw in PermissiveRawInputs)
        {
            var invalid = PermissiveOwned(invalidRaw);
            var rejected = await Invoke(output with { Details = invalid }); Failure(rejected, ToolFailureKind.ExecutionError);
            Equal("{}", rejected.Details.ToString()); Check(!rejected.Content.Single().Text.Contains("privateMarker", StringComparison.Ordinal), "Rejected details leaked into diagnostics.");
            var stages = 0;
            var finalized = await Invoke(output, transforms:
            [
                (_, _, value, _) => { stages++; return ValueTask.FromResult(value with { Terminate = true }); },
                (_, _, value, _) => { stages++; return ValueTask.FromResult(value with { Details = invalid, Content = [new("unvalidated replacement")] }); },
                (_, _, value, _) => { stages++; return ValueTask.FromResult(value); }
            ]);
            Failure(finalized, ToolFailureKind.HookError); Equal(2, stages);
            Check(finalized.Terminate && ReferenceEquals(details, finalized.Details) && ReferenceEquals(output.StructuredContent, finalized.StructuredContent),
                "Rejected output details transform replaced last validated output.");
            Equal(raw, finalized.Details.ToString()); Equal("text\0", finalized.Content[0].Text); Equal(invalidRaw, invalid.ToString());
        }
    }

    private static readonly string[] PermissiveRawInputs =
    [
        "{\"privateMarker\":1,}",
        "{\"privateMarker\":/*retained comment*/1}",
        "{\"privateMarker\":{\"nested\":1,}}",
        "{\"privateMarker\":[1,]}"
    ];

    private static JsonData PermissiveOwned(string raw)
    {
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return JsonData.FromElement(document.RootElement);
    }

    private static async Task ExecutorRawSyntax()
    {
        foreach (var raw in PermissiveRawInputs)
        {
            var metadata = PermissiveOwned(raw);
            Equal(raw, metadata.ToString());
            var result = await Invoke(ToolResult.Success("unvalidated output") with { StructuredContent = metadata });
            Failure(result, ToolFailureKind.ExecutionError);
            Check(result.StructuredContent is null, "Invalid executor metadata survived strict normalization.");
            Equal("Tool action execution failed.", result.Content.Single().Text);
            Check(!result.Details.ToString().Contains("privateMarker", StringComparison.Ordinal), "Invalid metadata reached diagnostics.");
            Equal(raw, metadata.ToString());
        }
        const string validRaw = "{ \"precise\":9007199254740993,\"one\":1.0,\"scale\":1e2,\"zero\":-0,\"nil\":null }";
        var valid = PermissiveOwned(validRaw);
        var accepted = await Invoke(ToolResult.Success("ok") with { StructuredContent = valid });
        Check(!accepted.IsError && ReferenceEquals(valid, accepted.StructuredContent), "Strictly valid permissive ownership was changed.");
        Equal(validRaw, accepted.StructuredContent!.ToString());
        var exact = await Invoke(ToolResult.Success("ok") with { StructuredContent = valid },
            new() { MaximumStructuredContentCharacters = validRaw.Length });
        Check(!exact.IsError, "Exact valid raw-size bound was rejected.");
        Failure(await Invoke(ToolResult.Success("ok") with { StructuredContent = valid },
            new() { MaximumStructuredContentCharacters = validRaw.Length - 1 }), ToolFailureKind.ExecutionError);
    }

    private static async Task TransformRawSyntax()
    {
        var initial = JsonData.Parse("{\"stage\":\"executed\",\"scale\":1.0}");
        const string validatedRaw = "{\"stage\":\"validated\",\"precise\":9007199254740993,\"zero\":-0}";
        var validated = PermissiveOwned(validatedRaw);
        foreach (var raw in PermissiveRawInputs)
        {
            var invalid = PermissiveOwned(raw); var stages = 0;
            var result = await Invoke(ToolResult.Success("completed effect") with { StructuredContent = initial }, transforms:
            [
                (_, _, value, _) => { stages++; return ValueTask.FromResult(value with { StructuredContent = validated, Terminate = true }); },
                (_, _, value, _) => { stages++; return ValueTask.FromResult(value with { StructuredContent = invalid, Content = [new("unvalidated replacement")] }); },
                (_, _, value, _) => { stages++; return ValueTask.FromResult(value); }
            ]);
            Failure(result, ToolFailureKind.HookError); Equal(2, stages);
            Check(result.Terminate && ReferenceEquals(validated, result.StructuredContent), "Invalid transform replaced the last validated metadata.");
            Equal(validatedRaw, result.StructuredContent!.ToString());
            Equal("completed effect", result.Content[0].Text);
            Equal("Tool action hook or policy failed.", result.Content[1].Text);
            Equal(raw, invalid.ToString());
        }
    }

    private static async Task SchedulerFlow()
    {
        var startedA = Gate(); var startedB = Gate(); var releaseA = Gate(); var releaseB = Gate(); var endedB = Gate();
        var first = JsonData.Parse("""{"output":"A","token":1.0}""");
        var second = JsonData.Parse("""{"output":"B","token":9007199254740993}""");
        var a = new Adapter("a", async (_, token) => { startedA.TrySetResult(); await releaseA.Task.WaitAsync(token); return ToolResult.Success("A") with { StructuredContent = first }; });
        var b = new Adapter("b", async (_, token) => { startedB.TrySetResult(); await releaseB.Task.WaitAsync(token); return ToolResult.Success("B") with { StructuredContent = second }; });
        var invoker = new ToolInvoker([a, b], new Policy(), resultTransforms:
            [(_, _, value, _) => ValueTask.FromResult(value with { Content = [new("final " + value.Content[0].Text)] })]);
        var hooks = new Hooks((invocation, value, _) =>
        {
            Check(ReferenceEquals(invocation.Call.Name == "a" ? first : second, value.StructuredContent), "Scheduler afterhook lost normalized metadata.");
            return ValueTask.FromResult(value with { Details = JsonData.Parse("{\"after\":true}"), Terminate = true });
        });
        var sink = new Sink(observation =>
        {
            if (observation is ToolExecutionEnded { Outcome.Invocation.Call.Name: "b" }) endedB.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var calls = ImmutableArray.Create<AssistantContent>(new ToolCallContent("call-a", "a", JsonData.EmptyObject), new ToolCallContent("call-b", "b", JsonData.EmptyObject));
        var running = new ToolBatchScheduler([new("a", invoker), new("b", invoker)], hooks).RunAsync(Message(calls), sink);
        try
        {
            await Task.WhenAll(startedA.Task, startedB.Task); releaseB.TrySetResult(); await endedB.Task;
            Check(!running.IsCompleted && !sink.Events.OfType<ToolResultMessageEnded>().Any(), "Messages were committed before the batch settled.");
            releaseA.TrySetResult(); var batch = await running;
            Sequence(["b", "a"], sink.Events.OfType<ToolExecutionEnded>().Select(value => value.Outcome.Invocation.Call.Name));
            Sequence(["a", "b"], batch.Outcomes.Select(value => value.Invocation.Call.Name));
            Sequence(["a", "b"], batch.Messages.Select(value => value.ToolName));
            Check(ReferenceEquals(first, batch.Outcomes[0].Result.StructuredContent) && ReferenceEquals(second, batch.Outcomes[1].Result.StructuredContent), "Batch outcomes lost metadata.");
            foreach (var ended in sink.Events.OfType<ToolExecutionEnded>())
                Check(ReferenceEquals(batch.Outcomes.Single(value => value.Invocation.Call.Name == ended.Outcome.Invocation.Call.Name), ended.Outcome), "Event replaced the finalized outcome.");
            Check(batch.Terminate && !batch.ShouldContinue && hooks.AfterCalls == 2, "Final hook fields did not reach the scheduler.");
            Sequence(["final A", "final B"], batch.Messages.Select(value => value.Content.Single().Text));
        }
        finally { releaseA.TrySetResult(); releaseB.TrySetResult(); try { await running; } catch { } }
    }

    private static async Task TranscriptExclusion()
    {
        foreach (var metadata in new JsonData?[] { JsonData.Parse("{\"programmaticOnlyMarker\":\"kept outside model context\"}"), JsonData.Null, null })
        {
            var transport = new ScriptTransport([Invocation().AssistantMessage, Message([new TextContent("done")], StopReason.Stop)]);
            var invoker = new ToolInvoker([new Adapter("lookup", (_, _) => ValueTask.FromResult(ToolResult.Success("model excerpt") with { StructuredContent = metadata }))], new Policy());
            var sink = new Sink();
            await using var agent = new NativeAgent(new(Model, transport, [new("lookup", invoker)]), () => 0, sink);
            var result = await agent.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"lookup\",\"timestamp\":0}")));
            Equal(2, result.Turns.Length); Equal(2, transport.Requests.Count);
            Check(ReferenceEquals(metadata, agent.Snapshot.CompletedToolOutcomes.Single().Result.StructuredContent), "High-level Agent snapshot lost metadata.");
            Check(ReferenceEquals(metadata, sink.Events.OfType<ToolExecutionEnded>().Single().Outcome.Result.StructuredContent), "Execution event lost metadata.");
            var message = result.Turns[0].Result.Tools.Messages.Single();
            Equal("model excerpt", message.Content.Single().Text);
            // Exercise the real canonical projection used by both the next model request and persistence callers.
            foreach (var entry in agent.Snapshot.Messages.Concat(transport.Requests[1].Messages))
            {
                var serialized = entry.WireBody.ToString();
                Check(!serialized.Contains("structuredContent", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("programmaticOnlyMarker", StringComparison.Ordinal),
                    "Programmatic output entered canonical/model transcript.");
                if (entry.Role == "toolResult")
                {
                    var reloaded = JsonData.Parse(serialized);
                    Sequence(["role", "toolCallId", "toolName", "content", "details", "isError", "timestamp"], reloaded.Value.EnumerateObject().Select(value => value.Name));
                    Equal("model excerpt", reloaded.Value.GetProperty("content")[0].GetProperty("text").GetString());
                }
            }
            var messageJson = JsonSerializer.Serialize(message);
            Check(!messageJson.Contains("StructuredContent", StringComparison.Ordinal), "Native ToolResultMessage grew a metadata field.");
        }
    }

    private static async Task NulTextOutputFlow()
    {
        const string partialText = "partial\0\u6587\U0001f642";
        const string finalText = "final\0\u6587\U0001f642";
        var partialMetadata = JsonData.Parse("""{"output":"partial\u0000","scale":1.0}""");
        var finalMetadata = JsonData.Parse("""{"output":"final\u0000","precise":9007199254740993}""");
        var partialDetails = DetailsFor(partialText); var finalDetails = DetailsFor(finalText);
        var transformedDetails = DetailsFor("transformed\0" + finalText);
        var updateEntered = Gate(); var updateRelease = Gate(); var resumed = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var adapter = new ProgressAdapter(async (progress, token) =>
        {
            await progress(ToolResult.Success(partialText) with { Details = partialDetails, StructuredContent = partialMetadata }, token);
            resumed = true;
            return ToolResult.Success(finalText) with { Details = finalDetails, StructuredContent = finalMetadata };
        });
        var invoker = new ToolInvoker([adapter], new Policy(), resultTransforms:
            [(_, _, value, _) =>
            {
                Check(ReferenceEquals(finalDetails, value.Details), "Executor details lost ownership before result transformation.");
                return ValueTask.FromResult(value with { Content = [new("transformed\0" + value.Content.Single().Text)], Details = transformedDetails });
            }]);
        var sink = new Sink(async observation =>
        {
            if (observation is ToolExecutionUpdated update)
            {
                Equal(partialText, update.PartialResult.Content.Single().Text);
                Check(ReferenceEquals(partialMetadata, update.PartialResult.StructuredContent), "Progress lost structured NUL output.");
                Check(ReferenceEquals(partialDetails, update.PartialResult.Details), "Progress lost decoded truncation details ownership.");
                Equal(partialText, update.PartialResult.Details.Value.GetProperty("truncation").GetProperty("content").GetString());
                var encoded = JsonSerializer.Serialize(update.PartialResult.Content);
                Check(encoded.Contains("\\u0000", StringComparison.Ordinal) && !encoded.Contains('\0'), "Progress emitted literal NUL in JSON.");
                updateEntered.TrySetResult(); await updateRelease.Task.WaitAsync(deadline.Token);
            }
        });
        var transport = new ScriptTransport([Invocation().AssistantMessage, Message([new TextContent("done")], StopReason.Stop)]);
        await using var agent = new NativeAgent(new(Model, transport, [new("lookup", invoker)]), () => 0, sink,
            new(ProgressDelivery: new(Mode: ToolProgressDeliveryMode.NativeAwaited)));
        var running = agent.PromptAsync(new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"lookup","timestamp":0}""")));
        try
        {
            await updateEntered.Task.WaitAsync(deadline.Token);
            Check(!resumed && !running.IsCompleted && transport.Requests.Count == 1, "Pending NUL progress was not an awaited barrier.");
            updateRelease.TrySetResult(); var result = await running.WaitAsync(deadline.Token);
            Check(resumed && result.Turns.Length == 2 && transport.Requests.Count == 2, "NUL output blocked a real next model request.");
            var expected = "transformed\0" + finalText;
            var outcome = sink.Events.OfType<ToolExecutionEnded>().Single().Outcome.Result;
            Check(!outcome.IsError && ReferenceEquals(finalMetadata, outcome.StructuredContent), "Final transform lost structured NUL data.");
            Check(ReferenceEquals(transformedDetails, outcome.Details), "Final transformation changed owned output details.");
            Equal(expected, outcome.Content.Single().Text);
            Equal(expected, outcome.Details.Value.GetProperty("truncation").GetProperty("content").GetString());
            Equal(expected, result.Turns[0].Result.Tools.Messages.Single().Content.Single().Text);
            Check(ReferenceEquals(transformedDetails, result.Turns[0].Result.Tools.Messages.Single().Details), "Scheduler lost final details ownership.");
            Equal(expected, agent.Snapshot.CompletedToolOutcomes.Single().Result.Content.Single().Text);
            foreach (var entry in agent.Snapshot.Messages.Concat(transport.Requests[1].Messages).Where(entry => entry.Role == "toolResult"))
            {
                var raw = entry.WireBody.ToString();
                Check(raw.Contains("\\u0000", StringComparison.Ordinal) && !raw.Contains('\0'), "Canonical JSON lost safe NUL escaping.");
                Equal(expected, JsonData.Parse(raw).Value.GetProperty("content")[0].GetProperty("text").GetString());
                Equal(expected, JsonData.Parse(raw).Value.GetProperty("details").GetProperty("truncation").GetProperty("content").GetString());
                Check(!raw.Contains("partial", StringComparison.Ordinal), "Partial truncation details entered final canonical history.");
                Check(!raw.Contains("structuredContent", StringComparison.OrdinalIgnoreCase) &&
                    !raw.Contains("precise", StringComparison.Ordinal), "Programmatic NUL output entered model history.");
            }
        }
        finally { updateRelease.TrySetResult(); try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { } }

        var exact = ToolResult.Success("a\0b") with { StructuredContent = finalMetadata };
        Check(!(await Invoke(exact, new(MaximumResultCharacters: 5))).IsError, "Exact NUL text budget was rejected.");
        Failure(await Invoke(exact, new(MaximumResultCharacters: 4)), ToolFailureKind.ExecutionError);
        foreach (var (text, cap, accepted) in new[] { ("a\0b", 5, true), ("a\0b", 4, false), ("\ud800", 32, false) })
        {
            var published = 0; var effects = 0;
            var checkedAdapter = new ProgressAdapter(async (progress, token) =>
            {
                await progress(ToolResult.Success(text) with { StructuredContent = finalMetadata }, token);
                effects++; return ToolResult.Success("ok");
            });
            var checkedInvoker = new ToolInvoker([checkedAdapter], new Policy(), options: new(MaximumResultCharacters: cap));
            var checkedResult = await checkedInvoker.ExecuteAsync(Invocation(),
                (_, _) => { published++; return ValueTask.CompletedTask; }, default);
            Equal(accepted ? 1 : 0, published); Equal(accepted ? 1 : 0, effects);
            if (accepted) Check(!checkedResult.IsError, "Exact NUL progress budget failed finalization.");
            else Failure(checkedResult, ToolFailureKind.ExecutionError);
        }
        foreach (var call in new[]
        {
            new ToolCallContent("bad\0id", "lookup", JsonData.EmptyObject),
            new ToolCallContent("call-lookup", "look\0up", JsonData.EmptyObject)
        })
        {
            var rejectedAdapter = new Adapter("lookup"); var policy = new Policy();
            var invocation = new ToolInvocation(Message([call]), call, 0);
            Failure(await new ToolInvoker([rejectedAdapter], policy).ExecuteAsync(invocation, default), ToolFailureKind.InvalidArguments);
            Equal(0, rejectedAdapter.Executions); Equal(0, policy.Authorizations);
        }

        static JsonData DetailsFor(string text)
        {
            var value = ToolOutputTruncator.Tail("discarded\n" + text, new(MaxLines: 1));
            Check(value.Truncated && value.Content == text, "Truncation fixture did not retain decoded NUL output.");
            return JsonData.Parse(JsonSerializer.Serialize(new
            {
                truncation = new { content = value.Content, truncated = value.Truncated, truncatedBy = "lines",
                    totalLines = value.TotalLines, totalBytes = value.TotalBytes, outputLines = value.OutputLines, outputBytes = value.OutputBytes,
                    lastLinePartial = value.LastLinePartial, firstLineExceedsLimit = value.FirstLineExceedsLimit, maxLines = value.MaxLines, maxBytes = value.MaxBytes },
                fullOutputPath = "/synthetic/output"
            }));
        }
    }

    private static ValueTask<ToolResult> Invoke(ToolResult result, ToolInvokerOptions? options = null,
        IEnumerable<ToolResultTransform>? transforms = null, CancellationToken token = default) =>
        new ToolInvoker([new Adapter("lookup", (_, _) => ValueTask.FromResult(result))], new Policy(), resultTransforms: transforms, options: options)
            .ExecuteAsync(Invocation(), token);

    private sealed class Adapter(string name, Func<PreparedToolAction, CancellationToken, ValueTask<ToolResult>>? execute = null) : IPreparedToolAdapter
    {
        public string Name => name;
        public int Executions;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) =>
            ValueTask.FromResult(new PreparedToolAction(Name, "read", PreparedToolActionKind.Path, "/synthetic/file", invocation.Call.Arguments,
                [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { Executions++; return execute?.Invoke(action, token) ?? ValueTask.FromResult(ToolResult.Success("ok")); }
    }

    private sealed class ProgressAdapter(Func<ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> execute) : IPreparedToolAdapter
    {
        private readonly Adapter _input = new("lookup");
        public string Name => "lookup";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => _input.PrepareAsync(invocation, token);
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => _input.ValidateAsync(action, token);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) =>
            execute(static (_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback onProgress, CancellationToken token) =>
            execute(onProgress, token);
    }

    private sealed class Policy : IToolActionPolicy
    {
        public int Authorizations;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Authorizations++; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }

    private sealed class Hooks(Func<ToolInvocation, ToolResult, CancellationToken, ValueTask<ToolResult>> after) : IToolHooks
    {
        public int AfterCalls;
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(ToolPreflightDecision.Allow);
        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken token)
        { AfterCalls++; return after(invocation, result, token); }
    }

    private sealed class Sink(Func<AgentEvent, ValueTask>? emit = null) : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Events.Add(observation); return emit?.Invoke(observation) ?? ValueTask.CompletedTask; }
    }

    private sealed class ScriptTransport(AssistantMessage[] messages) : IChatTransport
    {
        public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var final = messages[Requests.Count]; Requests.Add(request);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            for (var index = 0; index < final.Content.Length; index++)
            {
                if (final.Content[index] is ToolCallContent call)
                {
                    yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject });
                    yield return new ToolCallDelta(index, call.Arguments.ToString()); yield return new ToolCallEnded(index, call);
                }
                else if (final.Content[index] is TextContent text)
                {
                    yield return new TextStarted(index, new("")); yield return new TextDelta(index, text.Text); yield return new TextEnded(index, text.Text);
                }
            }
            yield return new StreamDone(final.StopReason, final);
            await Task.CompletedTask;
        }
    }

    private static ToolInvocation Invocation()
    {
        var call = new ToolCallContent("call-lookup", "lookup", JsonData.EmptyObject);
        return new(Message([call]), call, 0);
    }
    private static AssistantMessage Message(ImmutableArray<AssistantContent> content, StopReason stop = StopReason.ToolUse) =>
        new(Model.Api, Model.Provider, Model.Id, 0, content, TokenUsage.Zero, stop);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Failure(ToolResult value, ToolFailureKind kind) => Check(value.IsError && value.Failure?.Kind == kind, "Expected " + kind + " failure.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Check(expected.SequenceEqual(actual), "Sequence differs.");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
